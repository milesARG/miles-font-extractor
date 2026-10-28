using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MilesFontExtractor.ViewModels;

namespace MilesFontExtractor.Core;

/// <summary>
/// Developer aid for the README and website screenshots. Only active when the MILES_SNAP_DIR environment
/// variable is set: it watches that folder for a <c>request.txt</c> holding "name" or "name|x|y|w|h" (in
/// device-independent pixels) and writes a native 2x PNG to <c>name.png</c>.
/// The picture comes from a second copy of the UI, bound to the same live view-model and hosted off-screen at a
/// fixed 1280x840. That keeps it sharp (rendered at 192 DPI, not upscaled) and independent of the screen size,
/// which Windows would otherwise use to shrink the real window.
/// </summary>
internal static class SnapshotService
{
    private const double Scale = 2;
    private static readonly Size Canvas = new(1280, 840);

    private static HwndSource? _host;
    private static FrameworkElement? _root;
    private static ListBox? _list;

    public static void StartIfRequested(MainViewModel vm)
    {
        var dir = Environment.GetEnvironmentVariable("MILES_SNAP_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            var req = Path.Combine(dir, "request.txt");
            if (!File.Exists(req)) return;
            string text;
            try { text = File.ReadAllText(req).Trim(); File.Delete(req); } catch { return; }
            try { Save(vm, dir, text); } catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); }
        };
        timer.Start();
    }

    private static void EnsureHost(MainViewModel vm)
    {
        if (_root != null) return;
        var shadow = new MainWindow(vm, shadow: true);
        _list = shadow.FontListBox;
        _root = shadow.DetachContent();
        _root.DataContext = vm;
        // The real window supplies the default text colour and font; this copy has no window, so set them here.
        TextElement.SetForeground(_root, (Brush)Application.Current.FindResource("TextBrush"));
        TextElement.SetFontFamily(_root, (FontFamily)Application.Current.FindResource("UiFont"));
        _root.Width = Canvas.Width;
        _root.Height = Canvas.Height;
        _host = new HwndSource(new HwndSourceParameters("MilesSnapshotHost")
        {
            Width = 3000, Height = 2000, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP: never shown, never taskbar
        }) { RootVisual = _root };
    }

    private static void Save(MainViewModel vm, string dir, string request)
    {
        EnsureHost(vm);
        var root = _root!;
        root.Measure(Canvas);
        root.Arrange(new Rect(Canvas));
        root.UpdateLayout();
        if (_list!.SelectedItem != null)
        {
            _list.ScrollIntoView(_list.SelectedItem);
            root.UpdateLayout();
        }

        var rtb = new RenderTargetBitmap((int)(Canvas.Width * Scale), (int)(Canvas.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        rtb.Render(root);

        var parts = request.Split('|');
        BitmapSource result = rtb;
        if (parts.Length == 5)
        {
            var n = parts.Skip(1).Select(v => (int)Math.Round(double.Parse(v) * Scale)).ToArray();
            result = new CroppedBitmap(rtb, new Int32Rect(n[0], n[1], n[2], n[3]));
        }

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(result));
        using var fs = File.Create(Path.Combine(dir, parts[0] + ".png"));
        enc.Save(fs);
    }
}

