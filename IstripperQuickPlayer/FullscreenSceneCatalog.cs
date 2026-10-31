using System.Text.Json.Serialization;

namespace IStripperQuickPlayer;

internal static class FullscreenSceneCatalog
{
    internal sealed record Scene(string Id, string Name,
        [property: JsonIgnore] string Path);

    internal static Scene[] Read(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.scn"))
            .Select(path => new Scene(System.IO.Path.GetRelativePath(root, path)
                .Replace('\\', '/'), System.IO.Path.GetFileNameWithoutExtension(path),
                System.IO.Path.GetFullPath(path)))
            .OrderBy(scene => scene.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool Verify()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "QuickPlayer-scenes-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Read(root).Length != 0) return false;
            Directory.CreateDirectory(System.IO.Path.Combine(root, "A"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "B"));
            File.WriteAllText(System.IO.Path.Combine(root, "A", "Scène.scn"), "clip {}");
            File.WriteAllText(System.IO.Path.Combine(root, "B", "Scène.scn"), "clip {}");
            File.WriteAllText(System.IO.Path.Combine(root, "A", "preview.cache"), "");
            Scene[] scenes = Read(root);
            return scenes.Length == 2 && scenes[0].Id == "A/Scène.scn" &&
                scenes[1].Id == "B/Scène.scn" && scenes[0].Name == "Scène" &&
                File.Exists(scenes[0].Path) && !System.Text.Json.JsonSerializer
                    .Serialize(scenes[0]).Contains(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
