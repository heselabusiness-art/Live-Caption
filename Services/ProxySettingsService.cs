using System.IO;
using System.Text.Json;
using LiveCaptionViewer.Models;

namespace LiveCaptionViewer.Services;

/// <summary>
/// Loads and saves proxy settings to a JSON file under LocalAppData.
/// </summary>
public static class ProxySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveCaptionViewer");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static ProxySettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new ProxySettings();

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ProxySettings>(json, JsonOptions)
                   ?? new ProxySettings();
        }
        catch
        {
            return new ProxySettings();
        }
    }

    public static void Save(ProxySettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
