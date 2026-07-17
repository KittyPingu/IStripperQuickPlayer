using Microsoft.Win32;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class IstripperPaths
{
    private const string SystemKeyPath = @"Software\Totem\vghd\System";

    public string DataPath => GetStringValue("DataPath");

    public string ModelsPath => GetStringValue("ModelsPath");

    public string[] ModelsMultiPath => GetArrayValue("ModelsMultiPath");

    public string ModelsListPath => Path.Combine(DataPath, "models.lst");

    public string AppDataFolder
    {
        get
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IStripperQuickPlayer.WinUI");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public string FindCardMetadataFolder(string cardId)
    {
        string folder = Path.Combine(DataPath, cardId);
        return Directory.Exists(folder) ? folder : string.Empty;
    }

    public string FindCardFolder(string cardId)
    {
        string primary = Path.Combine(ModelsPath, cardId);
        if (Directory.Exists(primary))
        {
            return primary;
        }

        foreach (string folder in ModelsMultiPath)
        {
            string candidate = Path.Combine(folder, cardId);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string GetStringValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SystemKeyPath, false);
        return key?.GetValue(name, string.Empty)?.ToString() ?? string.Empty;
    }

    private static string[] GetArrayValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SystemKeyPath, false);
        return key?.GetValue(name, Array.Empty<string>()) as string[] ?? [];
    }
}
