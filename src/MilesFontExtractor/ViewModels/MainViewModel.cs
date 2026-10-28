using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MilesFontExtractor.Core;
using MilesFontExtractor.Models;

namespace MilesFontExtractor.ViewModels;

public enum AppState { Idle, Scanning, Review, Copying, Countdown, Removing, Done }

public sealed class DriveChoice : Observable
{
    public required string Root { get; init; }
    public required string Label { get; init; }
    public bool IsFolder { get; init; }
    private bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

public sealed class CategoryCard : Observable
{
    public required FontCategory Category { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public bool Includable => CategoryInfo.IsExtractable(Category);
    public Brush Accent => CategoryInfo.Brushes(Category).solid;

    private int _count; private long _bytes;
    public int Count { get => _count; set { Set(ref _count, value); Raise(nameof(Summary)); } }
    public long Bytes { get => _bytes; set { Set(ref _bytes, value); Raise(nameof(Summary)); } }
    public string Summary => Count == 0 ? "None found" : $"{CategoryInfo.FormatSize(Bytes)}";

    private bool _include;
    public bool Include { get => _include; set { if (Set(ref _include, value)) IncludeChanged?.Invoke(this); } }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public event Action<CategoryCard>? IncludeChanged;
}

public sealed class MainViewModel : Observable
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _cts;
    private ScanState _scan = new();
    private WorkState _work = new();
    private PendingJob? _job;
    private List<FontItem> _all = new();
    private bool _bulk;

    public MainViewModel()
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
            Drives.Add(new DriveChoice { Root = d.Name, Label = $"{d.Name.TrimEnd('\\')}  {label}" });
        }

        Cards = new ObservableCollection<CategoryCard>
        {
            new() { Category = FontCategory.Loose, Title = "Loose fonts", Hint = "Not tied to Windows or any program", Include = true },
            new() { Category = FontCategory.WindowsUser, Title = "Installed by you", Hint = "Added just for your account", Include = true },
            new() { Category = FontCategory.Review, Title = "Needs review", Hint = "Might belong to Office or a program", Include = false },
            new() { Category = FontCategory.Program, Title = "Program fonts", Hint = "Left alone" },
            new() { Category = FontCategory.System, Title = "Windows fonts", Hint = "Left alone" }
        };
        foreach (var c in Cards) c.IncludeChanged += OnIncludeChanged;

        View = new ListCollectionView(_all);
        View.Filter = FilterItem;

        // Not under Documents: that is often synced by OneDrive, and the result can be hundreds of MB.
        DestFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Extracted Fonts");

        ApplyCommandLine();

        _poll.Tick += (_, _) => Poll();
        _clock.Tick += (_, _) => Tick();

        ScanCommand = new RelayCommand(StartScan);
        AddFolderCommand = new RelayCommand(AddFolder);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        FilterCommand = new RelayCommand(p => SetFilter(p as CategoryCard));
        BrowseCommand = new RelayCommand(Browse);
        ExtractCommand = new RelayCommand(StartExtract, () => SelectedCount > 0 && !string.IsNullOrWhiteSpace(DestFolder));
        KeepCommand = new RelayCommand(Keep);
        RemoveNowCommand = new RelayCommand(() => _ = RemoveAsync());
        OpenFolderCommand = new RelayCommand(() => OpenInExplorer(_job?.DestDir ?? DestFolder));
        OpenLogCommand = new RelayCommand(() => { if (_job != null) OpenInExplorer(Extractor.LogPath(_job), select: true); });
        ShowInFolderCommand = new RelayCommand(() => { if (PreviewItem != null) OpenInExplorer(PreviewItem.Path, select: true); });
        RestartAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsAdmin);
        AgainCommand = new RelayCommand(Reset);
        SelectAllCommand = new RelayCommand(p => SelectVisible(p is true or "true"));

        var pending = Extractor.LoadJob();
        if (pending != null) Resume(pending);
    }

    // ------------------------------------------------------------------------------------------ state

    private AppState _state = AppState.Idle;
    public AppState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            if (value is AppState.Review or AppState.Countdown or AppState.Done or AppState.Idle)
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, MemoryTrim.Trim);
            foreach (var n in new[] { nameof(IsIdle), nameof(IsScanning), nameof(IsReview), nameof(IsWorking), nameof(IsCountdown), nameof(IsDone) })
                Raise(n);
        }
    }
    public bool IsIdle => State == AppState.Idle;
    public bool IsScanning => State == AppState.Scanning;
    public bool IsReview => State == AppState.Review;
    public bool IsWorking => State is AppState.Copying or AppState.Removing;
    public bool IsCountdown => State == AppState.Countdown;
    public bool IsDone => State == AppState.Done;
    public bool IsBusy => State is AppState.Scanning or AppState.Copying or AppState.Removing;
    public bool KeepsTimerRunning => State == AppState.Countdown;

    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ------------------------------------------------------------------------------------------ idle / scan

    public ObservableCollection<DriveChoice> Drives { get; } = new();

    private int _scanFolders, _scanFound;
    private string _scanCurrent = "";
    public int ScanFolders { get => _scanFolders; private set => Set(ref _scanFolders, value); }
    public int ScanFound { get => _scanFound; private set => Set(ref _scanFound, value); }
    public string ScanCurrent { get => _scanCurrent; private set => Set(ref _scanCurrent, value); }

    public RelayCommand ScanCommand { get; }
    public RelayCommand AddFolderCommand { get; }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to scan", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FolderNames) AddFolderChoice(f);
    }

    private void AddFolderChoice(string folder)
    {
        if (Drives.Any(d => d.Root.Equals(folder, StringComparison.OrdinalIgnoreCase))) return;
        Drives.Add(new DriveChoice { Root = folder, Label = folder, IsFolder = true });
    }

    /// <summary>
    /// Optional launch arguments: <c>--folder "C:\path"</c> (repeatable) scans just those folders instead of
    /// whole drives, and <c>--dest "C:\path"</c> presets the save folder.
    /// </summary>
    private void ApplyCommandLine()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool folders = false;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--dest") DestFolder = args[++i];
            else if (args[i] == "--folder" && Directory.Exists(args[i + 1])) { AddFolderChoice(args[++i]); folders = true; }
        }
        if (folders) foreach (var d in Drives.Where(d => !d.IsFolder)) d.IsChecked = false;
    }

    public RelayCommand CancelCommand { get; }

    private async void StartScan()
    {
        if (!Drives.Any(d => d.IsChecked))
        {
            MessageBox.Show("Tick at least one drive or folder to scan.", "Miles Font Extractor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _all = new List<FontItem>();
        _scan = new ScanState();
        ScanFolders = ScanFound = 0;
        ScanCurrent = "";
        _cts = new CancellationTokenSource();
        State = AppState.Scanning;
        _poll.Start();

        try
        {
            var picked = Drives.Where(d => d.IsChecked).Select(d => d.Root.TrimEnd('\\')).ToList();
            // A folder already inside another picked root would be scanned twice.
            var roots = picked.Where(r => !picked.Any(o => o != r && r.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.Length == 2 ? r + "\\" : r).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var classifier = await Task.Run(() => new Classifier());
            var found = await Scanner.ScanAsync(roots, classifier, DestFolder, _scan, _cts.Token);
            LoadResults(found);
            State = AppState.Review;
        }
        catch (OperationCanceledException)
        {
            State = AppState.Idle;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            State = AppState.Idle;
        }
        finally
        {
            _poll.Stop();
            Raise(nameof(IsBusy));
        }
    }

    private void Poll()
    {
        if (State == AppState.Scanning)
        {
            ScanFolders = _scan.Folders;
            ScanFound = _scan.FontFiles;
            ScanCurrent = _scan.Current;
        }
        else if (State is AppState.Copying or AppState.Removing)
        {
            WorkDone = _work.Done;
            WorkTotal = Math.Max(1, _work.Total);
            WorkCurrent = _work.Current;
        }
    }

    // ------------------------------------------------------------------------------------------ review

    public ObservableCollection<CategoryCard> Cards { get; }
    public ListCollectionView View { get; private set; }
    public RelayCommand FilterCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand ExtractCommand { get; }
    public RelayCommand ShowInFolderCommand { get; }
    public RelayCommand SelectAllCommand { get; }

    private FontCategory? _filter;
    private string _search = "";
    public string SearchText { get => _search; set { if (Set(ref _search, value)) Refresh(); } }

    private FontItem? _preview;
    public FontItem? PreviewItem { get => _preview; set => Set(ref _preview, value); }

    private string _destFolder = "";
    public string DestFolder { get => _destFolder; set { if (Set(ref _destFolder, value)) ExtractCommand?.Refresh(); } }

    private int _selectedCount; private long _selectedBytes;
    public int SelectedCount { get => _selectedCount; private set { if (Set(ref _selectedCount, value)) ExtractCommand.Refresh(); } }
    public long SelectedBytes { get => _selectedBytes; private set { Set(ref _selectedBytes, value); Raise(nameof(SelectionText)); } }
    public string SelectionText => SelectedCount == 0
        ? "Nothing selected"
        : $"{SelectedCount:N0} font{(SelectedCount == 1 ? "" : "s")} · {CategoryInfo.FormatSize(SelectedBytes)}";

    public string ListTitle => _filter == null ? "All fonts found" : Cards.First(c => c.Category == _filter).Title;
    public string VisibleCountText => $"{View.Count:N0} shown";
    public bool IsListEmpty => View.Count == 0;

    private void LoadResults(List<FontItem> found)
    {
        _all = found;
        _filter = FontCategory.Loose;
        foreach (var card in Cards)
        {
            var items = found.Where(f => f.Category == card.Category).ToList();
            card.Count = items.Count;
            card.Bytes = items.Sum(i => i.Size);
            card.IsActive = card.Category == _filter;
        }

        _bulk = true;
        foreach (var item in found)
        {
            item.SelectionChanged += OnItemSelectionChanged;
            var card = Cards.First(c => c.Category == item.Category);
            item.IsSelected = card.Includable && card.Include;
        }
        _bulk = false;

        View = new ListCollectionView(_all) { Filter = FilterItem };
        Raise(nameof(View));
        PreviewItem = null;
        RecountSelection();
        Refresh();
    }

    private bool FilterItem(object o)
    {
        var f = (FontItem)o;
        if (_filter != null && f.Category != _filter) return false;
        if (_search.Length == 0) return true;
        return f.Family.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || f.FullName.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || f.FileName.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || f.Folder.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh()
    {
        View.Refresh();
        Raise(nameof(ListTitle)); Raise(nameof(VisibleCountText)); Raise(nameof(IsListEmpty));
    }

    private void SetFilter(CategoryCard? card)
    {
        if (card == null) return;
        _filter = card.Category;
        foreach (var c in Cards) c.IsActive = c == card;
        Refresh();
    }

    private void OnIncludeChanged(CategoryCard card)
    {
        _bulk = true;
        foreach (var item in _all.Where(i => i.Category == card.Category)) item.IsSelected = card.Include;
        _bulk = false;
        RecountSelection();
    }

    private void OnItemSelectionChanged(FontItem item)
    {
        if (_bulk) return;
        SelectedCount += item.IsSelected ? 1 : -1;
        SelectedBytes += item.IsSelected ? item.Size : -item.Size;
    }

    private void SelectVisible(bool value)
    {
        _bulk = true;
        foreach (FontItem item in View) if (item.CanSelect) item.IsSelected = value;
        _bulk = false;
        RecountSelection();
    }

    private void RecountSelection()
    {
        int n = 0; long b = 0;
        foreach (var i in _all) if (i.IsSelected) { n++; b += i.Size; }
        SelectedCount = n; SelectedBytes = b;
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where the extracted fonts are saved", Multiselect = false };
        if (Directory.Exists(DestFolder)) dlg.InitialDirectory = DestFolder;
        if (dlg.ShowDialog() == true) DestFolder = dlg.FolderName;
    }

    // ------------------------------------------------------------------------------------------ copy / remove

    private int _workDone, _workTotal = 1;
    private string _workCurrent = "";
    private string _workTitle = "";
    public int WorkDone { get => _workDone; private set { if (Set(ref _workDone, value)) Raise(nameof(WorkText)); } }
    public int WorkTotal { get => _workTotal; private set => Set(ref _workTotal, value); }
    public string WorkCurrent { get => _workCurrent; private set => Set(ref _workCurrent, value); }
    public string WorkTitle { get => _workTitle; private set => Set(ref _workTitle, value); }
    public string WorkText => $"{WorkDone:N0} of {WorkTotal:N0}";

    private CopyReport? _copy;
    private async void StartExtract()
    {
        var items = _all.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (items.Count == 0) return;

        try
        {
            Directory.CreateDirectory(DestFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Can't use that folder:\n{ex.Message}", "Miles Font Extractor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _work = new WorkState { Total = items.Count };
        WorkDone = 0; WorkTotal = items.Count; WorkCurrent = "";
        WorkTitle = "Copying and verifying fonts";
        _cts = new CancellationTokenSource();
        State = AppState.Copying;
        _poll.Start();

        try
        {
            _copy = await Extractor.CopyAsync(items, DestFolder, _work, _cts.Token);
            _job = _copy.Job;
            if (_copy.Job.Entries.Count == 0)
            {
                Extractor.ClearJob();
                ShowDone("Nothing was copied.", _copy.Failed);
                return;
            }
            StartCountdown();
        }
        catch (OperationCanceledException)
        {
            State = AppState.Review;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error);
            State = AppState.Review;
        }
        finally
        {
            _poll.Stop();
        }
    }

    private void Resume(PendingJob job)
    {
        _job = job;
        State = AppState.Countdown;
        StartCountdown();
        if (!job.AutoRun) CountdownHeadline = "Some originals couldn't be removed";
    }

    // ------------------------------------------------------------------------------------------ countdown

    private string _remainingText = "20:00";
    private double _remainingFraction = 1;
    private string _countdownHeadline = "Copies verified";
    private string _countdownBody = "";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }
    public double RemainingFraction { get => _remainingFraction; private set => Set(ref _remainingFraction, value); }
    public string CountdownHeadline { get => _countdownHeadline; private set => Set(ref _countdownHeadline, value); }
    public string CountdownBody { get => _countdownBody; private set => Set(ref _countdownBody, value); }

    public bool ShowAdminOnCountdown => _job is { AutoRun: false } && !IsAdmin;

    public RelayCommand KeepCommand { get; }
    public RelayCommand RemoveNowCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    private void StartCountdown()
    {
        var job = _job!;
        int copies = job.Entries.Count(e => e.Status is "Copied");
        int dupes = job.Entries.Count(e => e.Status is "Duplicate");
        int failed = job.Entries.Count(e => e.Status is "Failed");
        long bytes = job.Entries.Where(e => e.Status == "Copied" && File.Exists(e.Dest)).Sum(e => new FileInfo(e.Dest).Length);
        CountdownHeadline = job.AutoRun ? "Copies verified" : "Some originals couldn't be removed";
        CountdownBody = job.AutoRun
            ? $"{copies:N0} font{(copies == 1 ? "" : "s")} ({CategoryInfo.FormatSize(bytes)}) saved to {job.DestFolderName()}" +
              (dupes > 0 ? $" · {dupes:N0} duplicate{(dupes == 1 ? "" : "s")} merged" : "") +
              (_copy is { Failed.Count: > 0 } ? $" · {_copy.Failed.Count} could not be copied and stay untouched" : "")
            : $"{failed:N0} original{(failed == 1 ? "" : "s")} still waiting. Windows' font folder needs administrator rights to remove files.";
        State = AppState.Countdown;
        Raise(nameof(ShowAdminOnCountdown));
        Tick();
        _clock.Start();
    }

    private bool _removing;
    private void Tick()
    {
        if (_job == null || State != AppState.Countdown) return;
        var left = _job.DeleteAtUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            RemainingText = "00:00";
            RemainingFraction = 0;
            if (_job.AutoRun && !_removing) _ = RemoveAsync();
            return;
        }
        RemainingText = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        double total = Math.Max(1, (_job.DeleteAtUtc - _job.CreatedUtc).TotalSeconds);
        RemainingFraction = Math.Clamp(left.TotalSeconds / total, 0, 1);
    }

    private async Task RemoveAsync()
    {
        if (_job == null || _removing) return;
        _removing = true;
        _clock.Stop();
        _work = new WorkState();
        WorkDone = 0; WorkTotal = 1; WorkCurrent = "";
        WorkTitle = "Verifying and removing originals";
        _cts = new CancellationTokenSource();
        State = AppState.Removing;
        _poll.Start();
        try
        {
            var r = await Extractor.RemoveOriginalsAsync(_job, _work, _cts.Token);
            _poll.Stop();
            var lines = new List<string>();
            if (_copy is { Failed.Count: > 0 }) lines.AddRange(_copy.Failed.Select(f => $"Not copied: {Path.GetFileName(f.path)} ({f.reason})"));
            ResultKept = r.Kept;
            ResultRemoved = r.Removed;
            ResultFailed = r.Failed.Count;
            ResultMissing = r.Missing;
            ShowDone(null, r.Failed);
            if (r.Failed.Count > 0) _job = Extractor.LoadJob() ?? _job;
        }
        catch (Exception ex)
        {
            _poll.Stop();
            MessageBox.Show(ex.Message, "Removal failed", MessageBoxButton.OK, MessageBoxImage.Error);
            State = AppState.Countdown;
            _clock.Start();
        }
        finally
        {
            _removing = false;
        }
    }

    private void Keep()
    {
        if (_job == null) return;
        _clock.Stop();
        Extractor.KeepOriginals(_job);
        ResultRemoved = 0; ResultFailed = 0; ResultKept = _job.Entries.Count; ResultMissing = 0;
        ShowDone("Originals kept. Nothing was deleted.", null);
    }

    // ------------------------------------------------------------------------------------------ done

    private int _resRemoved, _resKept, _resFailed, _resMissing;
    public int ResultRemoved { get => _resRemoved; private set => Set(ref _resRemoved, value); }
    public int ResultKept { get => _resKept; private set => Set(ref _resKept, value); }
    public int ResultFailed { get => _resFailed; private set => Set(ref _resFailed, value); }
    public int ResultMissing { get => _resMissing; private set => Set(ref _resMissing, value); }
    private string _doneTitle = "All done";
    private string _doneBody = "";
    public string DoneTitle { get => _doneTitle; private set => Set(ref _doneTitle, value); }
    public string DoneBody { get => _doneBody; private set => Set(ref _doneBody, value); }
    public bool NeedsAdmin => ResultFailed > 0 && !IsAdmin;

    public RelayCommand RestartAdminCommand { get; }
    public RelayCommand AgainCommand { get; }

    private void ShowDone(string? message, IEnumerable<(string path, string reason)>? failed)
    {
        DoneTitle = ResultFailed > 0 ? "Finished with a few leftovers" : "All done";
        DoneBody = message ?? (ResultFailed > 0
            ? $"{ResultRemoved:N0} originals moved to the Recycle Bin. {ResultFailed:N0} couldn't be removed and are still safely in place."
            : $"{ResultRemoved:N0} original{(ResultRemoved == 1 ? "" : "s")} moved to the Recycle Bin. Your extracted fonts are in {_job?.DestFolderName() ?? DestFolder}.");
        Raise(nameof(NeedsAdmin));
        State = AppState.Done;
    }

    private void Reset()
    {
        _all = new List<FontItem>();
        View = new ListCollectionView(_all) { Filter = FilterItem };
        Raise(nameof(View));
        foreach (var c in Cards) { c.Count = 0; c.Bytes = 0; }
        SelectedCount = 0; SelectedBytes = 0;
        _job = null; _copy = null;
        State = AppState.Idle;
    }

    // ------------------------------------------------------------------------------------------ helpers

    private static void OpenInExplorer(string path, bool select = false)
    {
        try
        {
            if (select && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { }
    }

    private static void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch { /* user declined the UAC prompt */ }
    }
}

internal static class JobExtensions
{
    public static string DestFolderName(this PendingJob job) => job.DestDir;
}
