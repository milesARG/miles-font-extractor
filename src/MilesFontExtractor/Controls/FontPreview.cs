using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MilesFontExtractor.Controls;

/// <summary>
/// Draws a specimen straight from a font file. The font is loaded from a throw-away temp copy so the
/// original is never locked by the preview (which would stop it from being removed later).
/// </summary>
public sealed class FontPreview : FrameworkElement
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(FontPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FontPreview)d).Load()));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(FontPreview),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(
        nameof(Muted), typeof(Brush), typeof(FontPreview),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? FilePath { get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush Muted { get => (Brush)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "FontExtractorPreview");
    private Typeface? _face;

    public static void CleanupTemp()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
    }

    private void Load()
    {
        _face = null;
        var path = FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf")) return;
            // One folder per preview, holding only this font, so WPF never enumerates a big directory.
            string dir = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, "font" + ext);
            File.Copy(path, copy, true);
            File.SetAttributes(copy, FileAttributes.Normal);
            var family = Fonts.GetFontFamilies(new Uri(dir + "\\")).FirstOrDefault();
            if (family != null) _face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }
        catch
        {
            _face = null;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (w < 10 || h < 10) return;

        if (_face == null)
        {
            dc.DrawText(Text(string.IsNullOrEmpty(FilePath) ? "Select a font to preview it" : "Preview not available for this format",
                new Typeface("Segoe UI"), 13, Muted, dip), new Point(0, 4));
            return;
        }

        double y = 0;
        y = Line(dc, "Aa Gg Qq 123", 44, y, w, dip, Foreground);
        y = Line(dc, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", 19, y + 4, w, dip, Foreground);
        y = Line(dc, "abcdefghijklmnopqrstuvwxyz", 19, y + 1, w, dip, Foreground);
        y = Line(dc, "The quick brown fox jumps over", 15, y + 6, w, dip, Muted);
        Line(dc, "the lazy dog. 0123456789 !?&@", 15, y + 1, w, dip, Muted);
    }

    private static FormattedText Text(string t, Typeface face, double size, Brush brush, double dip) =>
        new(t, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dip);

    private double Line(DrawingContext dc, string text, double size, double top, double maxWidth, double dip, Brush brush)
    {
        var ft = Text(text, _face!, size, brush, dip);
        if (ft.Width > maxWidth) ft = Text(text, _face!, size * maxWidth / ft.Width, brush, dip);
        dc.DrawText(ft, new Point(0, top));
        return top + ft.Height;
    }
}
