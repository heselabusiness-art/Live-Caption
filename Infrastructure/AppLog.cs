using System.IO;
using Serilog;

namespace LiveCaptionViewer.Infrastructure;

/// <summary>
/// Application-wide file logging (Serilog). Logs live next to the other per-user data in
/// <c>%LocalAppData%\LiveCaptionViewer\logs</c> and roll daily, keeping the last 7 days.
/// Logging must never break the app, so every entry point swallows its own failures.
/// </summary>
public static class AppLog
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveCaptionViewer",
        "logs");

    public static void Init()
    {
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(LogDirectory, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}{Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch
        {
            // Read-only profile etc.: fall back to the silent logger.
        }
    }

    /// <summary>Logger tagged with the given component name (shown as the prefix of each line).</summary>
    public static ILogger For(string component) => Log.Logger.ForContext("SourceContext", component + ": ");

    public static void Shutdown()
    {
        try { Log.CloseAndFlush(); }
        catch { /* shutting down */ }
    }
}
