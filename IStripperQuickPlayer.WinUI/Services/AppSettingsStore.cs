using System.Text.Json;
using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class AppSettingsStore
{
    private readonly IstripperPaths _paths;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public AppSettingsStore(IstripperPaths paths)
    {
        _paths = paths;
    }

    private string SettingsPath => Path.Combine(_paths.AppDataFolder, "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, _options));
    }
}
