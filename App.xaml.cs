using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace LiveCaptionViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Dark Fluent theme with a fixed brand accent (instead of the user's Windows accent),
        // so the overlay looks the same on every machine. Backdrop stays "None": the main
        // window is a see-through overlay (AllowsTransparency) and cannot use Mica/Acrylic.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(
            Color.FromRgb(0x4C, 0x9B, 0xE8),
            ApplicationTheme.Dark);

        base.OnStartup(e);
    }
}
