using System.Text.Json;
using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class FilterSettingsStore
{
    private readonly IstripperPaths _paths;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public FilterSettingsStore(IstripperPaths paths)
    {
        _paths = paths;
    }

    private string FiltersPath => Path.Combine(_paths.AppDataFolder, "filters.json");

    public Dictionary<string, FilterSettings> Load()
    {
        if (!File.Exists(FiltersPath))
        {
            return new Dictionary<string, FilterSettings> { ["Default"] = new() };
        }

        try
        {
            Dictionary<string, FilterSettings>? filters = JsonSerializer.Deserialize<Dictionary<string, FilterSettings>>(File.ReadAllText(FiltersPath));
            if (filters == null || filters.Count == 0)
            {
                return new Dictionary<string, FilterSettings> { ["Default"] = new() };
            }

            return filters;
        }
        catch
        {
            return new Dictionary<string, FilterSettings> { ["Default"] = new() };
        }
    }

    public void Save(Dictionary<string, FilterSettings> filters)
    {
        File.WriteAllText(FiltersPath, JsonSerializer.Serialize(filters, _options));
    }

    public void Export(string path, FilterSettings filter)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(filter, _options));
    }

    public FilterSettings Import(string path)
    {
        return JsonSerializer.Deserialize<FilterSettings>(File.ReadAllText(path)) ?? new FilterSettings();
    }
}
