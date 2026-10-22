using System.Windows;
using System.Windows.Media;
using LiveCaptionViewer.Infrastructure;
using Serilog;
using Wpf.Ui.Appearance;

namespace LiveCaptionViewer;

public partial class App : Application
{
    // Held for the whole run. Two copies would fight over the global hotkeys (the second one
    // silently gets none), so the second copy stops with a message instead.
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Init();

        if (!AcquireSingleInstance())
        {
            MessageBox.Show(
                "Live Caption Viewer is already running.\n\nClose the other copy first — only one copy can own the Home / End / PageUp hotkeys.",
                "Live Caption Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Information("Live Caption Viewer starting (log folder: {Folder})", AppLog.LogDirectory);

        // Hotkey handlers and automation run fire-and-forget: make sure a failure there lands in the
        // log instead of disappearing.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Dark Fluent theme with a fixed brand accent (instead of the user's Windows accent),
        // so the overlay looks the same on every machine. Backdrop stays "None": the main
        // window is a see-through overlay (AllowsTransparency) and cannot use Mica/Acrylic.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(
            Color.FromRgb(0x4C, 0x9B, 0xE8),
            ApplicationTheme.Dark);

        base.OnStartup(e);
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstance = new Mutex(false, @"Local\LiveCaptionViewer.SingleInstance");
            // "Restart to apply proxy" starts the new copy before the old one has exited: wait briefly.
            return _singleInstance.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            return true;   // the previous copy crashed while holding it; we own it now
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Single-instance check failed; continuing");
            return true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Live Caption Viewer exiting");
        try { _singleInstance?.ReleaseMutex(); }
        catch { /* not owned (second copy) */ }
        AppLog.Shutdown();
        base.OnExit(e);
    }
}
