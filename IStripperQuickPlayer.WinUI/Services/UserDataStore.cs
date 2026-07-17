using System.Text.Json;
using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class UserDataStore
{
    private readonly IstripperPaths _paths;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public UserDataStore(IstripperPaths paths)
    {
        _paths = paths;
    }

    private string UserDataPath => Path.Combine(_paths.AppDataFolder, "mydata.json");

    public UserData Load()
    {
        if (!File.Exists(UserDataPath))
        {
            return new UserData();
        }

        try
        {
            return JsonSerializer.Deserialize<UserData>(File.ReadAllText(UserDataPath)) ?? new UserData();
        }
        catch
        {
            return new UserData();
        }
    }

    public void Save(UserData data)
    {
        File.WriteAllText(UserDataPath, JsonSerializer.Serialize(data, _options));
    }
}
