using System.Windows;
using MilesFontExtractor.Controls;

namespace MilesFontExtractor;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        FontPreview.CleanupTemp();
        base.OnExit(e);
    }
}
