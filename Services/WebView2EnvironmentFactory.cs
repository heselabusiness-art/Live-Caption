using System.IO;
using LiveCaptionViewer.Models;
using Microsoft.Web.WebView2.Core;

namespace LiveCaptionViewer.Services;

/// <summary>
/// Creates a stable CoreWebView2Environment with writable user-data paths and
/// fallbacks so WebView2 does not fail with access-denied or class-not-registered errors.
/// </summary>
public static class WebView2EnvironmentFactory
{
    private static string? _resolvedUserDataFolder;

    /// <summary>User-data folder selected during the last successful resolve attempt.</summary>
    public static string UserDataFolder =>
        _resolvedUserDataFolder ??= ResolveUserDataFolder();

    public static bool IsRuntimeInstalled()
    {
        try
        {
            string? version = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            return !string.IsNullOrWhiteSpace(version);
        }
        catch
        {
            return false;
        }
    }

    public static string? GetBrowserExecutableFolder()
    {
        string basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft",
            "EdgeWebView",
            "Application");

        if (!Directory.Exists(basePath))
            return null;

        string? versionFolder = Directory
            .GetDirectories(basePath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && char.IsDigit(name![0]))
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return versionFolder is null ? basePath : Path.Combine(basePath, versionFolder);
    }

    /// <summary>
    /// Picks the first user-data directory the process can create and write to.
    /// </summary>
    public static string ResolveUserDataFolder()
    {
        foreach (string candidate in GetUserDataFolderCandidates())
        {
            if (TryPrepareUserDataFolder(candidate))
                return candidate;
        }

        throw new UnauthorizedAccessException(
            "WebView2 could not create a writable profile folder. " +
            "Check folder permissions or run the app from a user-writable location.");
    }

    public static async Task<CoreWebView2Environment> CreateAsync(ProxySettings proxySettings)
    {
        string? explicitFolder = GetBrowserExecutableFolder();
        Exception? lastError = null;

        foreach (string userDataFolder in GetUserDataFolderCandidates())
        {
            if (!TryPrepareUserDataFolder(userDataFolder))
                continue;

            foreach (string? browserFolder in new string?[] { null, explicitFolder })
            {
                foreach (bool useTransparencyFlags in new[] { true, false })
                {
                    try
                    {
                        string browserArgs = BuildBrowserArguments(proxySettings, useTransparencyFlags);
                        var options = new CoreWebView2EnvironmentOptions
                        {
                            AdditionalBrowserArguments = string.IsNullOrWhiteSpace(browserArgs)
                                ? null
                                : browserArgs
                        };

                        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                            browserExecutableFolder: browserFolder,
                            userDataFolder: userDataFolder,
                            options: options);

                        _resolvedUserDataFolder = userDataFolder;
                        return env;
                    }
                    catch (Exception ex) when (IsAccessDenied(ex))
                    {
                        lastError = ex;
                        break; // try next user-data folder
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }
            }
        }

        throw lastError ?? new InvalidOperationException("WebView2 environment could not be created.");
    }

    private static IEnumerable<string> GetUserDataFolderCandidates()
    {
        string localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveCaptionViewer",
            "WebView2");

        string tempData = Path.Combine(
            Path.GetTempPath(),
            "LiveCaptionViewer",
            "WebView2");

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LiveCaptionViewer",
            "WebView2");

        string besideExe = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "WebView2Data");

        // Prefer LocalAppData, then Temp (almost always writable), then Roaming, then exe folder.
        yield return localAppData;
        yield return tempData;
        yield return appData;
        yield return besideExe;
    }

    private static bool TryPrepareUserDataFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            string probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
                return true;

            if (current.HResult == unchecked((int)0x80070005)) // E_ACCESSDENIED
                return true;

            if (current.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string BuildBrowserArguments(ProxySettings proxySettings, bool useTransparencyFlags)
    {
        if (!useTransparencyFlags)
            return proxySettings.AppendToBrowserArguments(string.Empty).Trim();

        const string transparencyArgs =
            "--disable-features=CalculateNativeWinOcclusion,DirectComposition " +
            "--disable-gpu " +
            "--disable-gpu-compositing";

        return proxySettings.AppendToBrowserArguments(transparencyArgs);
    }
}
