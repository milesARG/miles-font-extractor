using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MilesFontExtractor.Models;

namespace MilesFontExtractor.Core;

public sealed class PendingEntry
{
    public string Source { get; set; } = "";
    public string Dest { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Family { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>Copied | Duplicate | Removed | Failed | Kept | Missing</summary>
    public string Status { get; set; } = "Copied";
    public string Note { get; set; } = "";
}

public sealed class PendingJob
{
    public DateTime CreatedUtc { get; set; }
    public DateTime DeleteAtUtc { get; set; }
    public string DestDir { get; set; } = "";
    public bool AutoRun { get; set; } = true;
    public List<PendingEntry> Entries { get; set; } = new();
}

public sealed class WorkState
{
    private int _done;
    public int Done => _done;
    public int Total { get; set; }
    public volatile string Current = "";
    internal void Step() => Interlocked.Increment(ref _done);
}

public sealed record CopyReport(PendingJob Job, int Copied, int Duplicates, List<(string path, string reason)> Failed, long Bytes);
public sealed record RemoveReport(int Removed, int Missing, int Kept, List<(string path, string reason)> Failed);

public static class Extractor
{
    public static readonly TimeSpan RemoveDelay = TimeSpan.FromMinutes(20);

    public static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MilesFontExtractor");
    private static string PendingFile => Path.Combine(StateDir, "pending.json");

    // ---------------------------------------------------------------------------------------------
    // Copy phase

    public static Task<CopyReport> CopyAsync(IReadOnlyList<FontItem> items, string destDir, WorkState state, CancellationToken ct) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(destDir);
            string destPrefix = destDir.TrimEnd('\\') + "\\";

            var used = new HashSet<string>(
                Directory.EnumerateFiles(destDir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            var byHash = new Dictionary<string, string>();
            var job = new PendingJob { CreatedUtc = DateTime.UtcNow, DestDir = destDir };
            var failed = new List<(string, string)>();
            int copied = 0, dupes = 0;
            long bytes = 0;
            state.Total = items.Count;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                state.Current = item.FileName;
                try
                {
                    if (item.Path.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    string hash = HashFile(item.Path);
                    if (byHash.TryGetValue(hash, out var existing))
                    {
                        job.Entries.Add(Entry(item, existing, hash, "Duplicate"));
                        dupes++;
                        continue;
                    }

                    string target = UniquePath(destDir, item.FileName, used);
                    File.Copy(item.Path, target, overwrite: false);
                    if (HashFile(target) != hash)
                    {
                        File.Delete(target);
                        throw new IOException("Copy did not match the original");
                    }

                    byHash[hash] = target;
                    job.Entries.Add(Entry(item, target, hash, "Copied"));
                    copied++;
                    bytes += item.Size;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add((item.Path, ex.Message));
                }
                finally
                {
                    state.Step();
                }
            }

            job.DeleteAtUtc = DateTime.UtcNow + RemoveDelay;
            SaveJob(job);
            WriteLog(job);
            return new CopyReport(job, copied, dupes, failed, bytes);
        }, ct);

    private static PendingEntry Entry(FontItem i, string dest, string hash, string status) => new()
    {
        Source = i.Path, Dest = dest, Sha256 = hash, Family = i.Family, Category = i.CategoryLabel, Status = status
    };

    private static string UniquePath(string dir, string fileName, HashSet<string> used)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        string name = fileName;
        for (int n = 2; !used.Add(name); n++) name = $"{stem} ({n}){ext}";
        return Path.Combine(dir, name);
    }

    // ---------------------------------------------------------------------------------------------
    // Removal phase (only ever touches originals whose copy is re-verified byte for byte)

    public static Task<RemoveReport> RemoveOriginalsAsync(PendingJob job, WorkState state, CancellationToken ct) =>
        Task.Run(() =>
        {
            var work = job.Entries.Where(e => e.Status is "Copied" or "Duplicate" or "Failed").ToList();
            state.Total = work.Count;
            var destOk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int removed = 0, missing = 0, kept = 0;
            var failed = new List<(string, string)>();

            foreach (var e in work)
            {
                ct.ThrowIfCancellationRequested();
                state.Current = Path.GetFileName(e.Source);
                try
                {
                    if (!File.Exists(e.Source)) { e.Status = "Missing"; missing++; continue; }

                    if (!destOk.TryGetValue(e.Dest, out bool ok))
                        destOk[e.Dest] = ok = File.Exists(e.Dest) && HashFile(e.Dest) == e.Sha256;
                    if (!ok) { e.Status = "Kept"; e.Note = "Copy could not be verified"; kept++; continue; }

                    if (HashFile(e.Source) != e.Sha256) { e.Status = "Kept"; e.Note = "Original changed since copying"; kept++; continue; }

                    if (SendToRecycleBin(e.Source)) { e.Status = "Removed"; e.Note = ""; removed++; }
                    else
                    {
                        e.Status = "Failed";
                        e.Note = IsProtectedPath(e.Source) ? "Needs administrator rights" : "File is in use or protected";
                        failed.Add((e.Source, e.Note));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    e.Status = "Failed"; e.Note = ex.Message;
                    failed.Add((e.Source, ex.Message));
                }
                finally
                {
                    state.Step();
                }
            }

            WriteLog(job);
            if (failed.Count > 0)
            {
                job.Entries.RemoveAll(x => x.Status != "Failed");
                job.AutoRun = false;
                job.DeleteAtUtc = DateTime.UtcNow;
                SaveJob(job);
            }
            else
            {
                ClearJob();
            }
            return new RemoveReport(removed, missing, kept, failed);
        }, ct);

    public static void KeepOriginals(PendingJob job)
    {
        foreach (var e in job.Entries.Where(e => e.Status is "Copied" or "Duplicate")) e.Status = "Kept";
        WriteLog(job);
        ClearJob();
    }

    private static bool IsProtectedPath(string path) =>
        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------------
    // Persistence, so a closed app can pick the countdown up again

    public static void SaveJob(PendingJob job)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(PendingFile, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static PendingJob? LoadJob()
    {
        try
        {
            if (!File.Exists(PendingFile)) return null;
            var job = JsonSerializer.Deserialize<PendingJob>(File.ReadAllText(PendingFile));
            return job is { Entries.Count: > 0 } ? job : null;
        }
        catch { return null; }
    }

    public static void ClearJob()
    {
        try { File.Delete(PendingFile); } catch { }
    }

    public static string LogPath(PendingJob job) => Path.Combine(job.DestDir, "MilesFontExtractor-log.csv");

    public static void WriteLog(PendingJob job)
    {
        try
        {
            var sb = new StringBuilder("Family,Category,Status,Note,Original location,Saved as,SHA-256\r\n");
            foreach (var e in job.Entries)
                sb.AppendLine(string.Join(',', Csv(e.Family), Csv(e.Category), Csv(e.Status), Csv(e.Note),
                    Csv(e.Source), Csv(Path.GetFileName(e.Dest)), e.Sha256));
            File.WriteAllText(LogPath(job), sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* log is a convenience; never fail the job over it */ }
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // ---------------------------------------------------------------------------------------------

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    // Recycle Bin, silent: no dialogs, no UAC prompts. Success is judged by the file really being gone.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private static bool SendToRecycleBin(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI)
        };
        SHFileOperation(ref op);
        return !File.Exists(path);
    }
}
