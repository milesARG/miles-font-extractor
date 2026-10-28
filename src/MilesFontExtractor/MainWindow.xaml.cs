using System.Windows;
using MilesFontExtractor.Core;
using MilesFontExtractor.ViewModels;

namespace MilesFontExtractor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow() : this(new MainViewModel(), shadow: false) { }

    /// <param name="shadow">True for the off-screen copy used only by <see cref="SnapshotService"/>.</param>
    internal MainWindow(MainViewModel vm, bool shadow)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
        // Clearing a search leaves the selected font selected; bring it back into view.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SearchText))
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (FontList.SelectedItem != null) FontList.ScrollIntoView(FontList.SelectedItem);
                });
        };
        if (shadow) return;
        StateChanged += (_, _) => OnStateChanged();
        Closing += OnClosing;
        SnapshotService.StartIfRequested(_vm);
        ContentRendered += (_, _) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, MilesFontExtractor.Core.MemoryTrim.Trim);
    }

    internal System.Windows.Controls.ListBox FontListBox => FontList;

    /// <summary>Takes the UI out of this (never shown) window so it can be hosted elsewhere.</summary>
    internal FrameworkElement DetachContent()
    {
        var root = Root;
        Content = null;
        return root;
    }

    private void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxGlyph.Text = max ? "" : "";
        // A borderless maximized window overhangs the screen by the resize border; pull it back in.
        Root.Margin = max ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 2) : new Thickness(0);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsBusy)
        {
            var r = MessageBox.Show(this, "A task is still running. Quit anyway?", "Miles Font Extractor",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) e.Cancel = true;
        }
        else if (_vm.KeepsTimerRunning)
        {
            var r = MessageBox.Show(this,
                "Your originals are still waiting to be moved to the Recycle Bin.\n\n" +
                "If you quit now, nothing is lost: the clean-up resumes the next time you open Miles Font Extractor.\n\nQuit anyway?",
                "Miles Font Extractor", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) e.Cancel = true;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
