using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MilesFontExtractor.Models;

public enum FontCategory
{
    Loose,
    WindowsUser,
    Review,
    Program,
    System
}

public static class CategoryInfo
{
    public static string Label(FontCategory c) => c switch
    {
        FontCategory.Loose => "Loose",
        FontCategory.WindowsUser => "Installed by you",
        FontCategory.Review => "Needs review",
        FontCategory.Program => "Program",
        _ => "Windows"
    };

    public static bool IsExtractable(FontCategory c) => c is FontCategory.Loose or FontCategory.WindowsUser or FontCategory.Review;

    public static Color Color(FontCategory c) => c switch
    {
        FontCategory.Loose => System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99),
        FontCategory.WindowsUser => System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA),
        FontCategory.Review => System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24),
        FontCategory.Program => System.Windows.Media.Color.FromRgb(0xA7, 0x8B, 0xFA),
        _ => System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8)
    };

    private static readonly Dictionary<FontCategory, (Brush solid, Brush soft)> Cache = new();

    public static (Brush solid, Brush soft) Brushes(FontCategory c)
    {
        if (Cache.TryGetValue(c, out var b)) return b;
        var col = Color(c);
        var solid = new SolidColorBrush(col); solid.Freeze();
        var soft = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x28, col.R, col.G, col.B)); soft.Freeze();
        return Cache[c] = (solid, soft);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1048576.0:0.#} MB";
        return $"{bytes / 1073741824.0:0.##} GB";
    }
}

public sealed class FontItem : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required string Family { get; init; }
    public required string FullName { get; init; }
    public required string Format { get; init; }
    public required string Vendor { get; init; }
    public required string Copyright { get; init; }
    public required long Size { get; init; }
    public required FontCategory Category { get; init; }
    public required string Reason { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string SizeText => CategoryInfo.FormatSize(Size);
    public string CategoryLabel => CategoryInfo.Label(Category);
    public bool CanSelect => CategoryInfo.IsExtractable(Category);
    public Brush CategoryBrush => CategoryInfo.Brushes(Category).solid;
    public Brush CategorySoftBrush => CategoryInfo.Brushes(Category).soft;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke(this);
        }
    }

    public event Action<FontItem>? SelectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
