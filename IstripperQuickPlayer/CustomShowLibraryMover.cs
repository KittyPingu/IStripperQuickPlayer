using System.Text.Json;
using System.Text.Json.Nodes;

namespace IStripperQuickPlayer;

internal sealed record CustomShowLibraryMoveResult(
    int FileCount, long Bytes, string? CleanupWarning);

internal static class CustomShowLibraryMover
{
    internal static (string Source, string Destination) ValidatePaths(
        string source, string destination)
    {
        string from = Normalize(source);
        string to = Normalize(destination);
        if (!Directory.Exists(from))
            throw new DirectoryNotFoundException(
                "The current custom-show library folder does not exist.");
        if (SamePath(from, to))
            throw new InvalidDataException(
                "Select a different folder for the custom-show library.");
        if (IsWithin(from, to) || IsWithin(to, from))
            throw new InvalidDataException(
                "The new library folder cannot contain, or be contained by, " +
                "the current library folder.");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new InvalidDataException(
                "The new custom-show library folder must be empty.");
        if (File.Exists(to))
            throw new InvalidDataException(
                "The selected custom-show library path is a file.");
        return (from, to);
    }

    internal static bool SamePath(string left, string right)
    {
        try { return string.Equals(Normalize(left), Normalize(right),
            StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    internal static CustomShowLibraryMoveResult Move(string source,
        string destination, IProgress<CustomShowProgress>? progress,
        CancellationToken token)
    {
        (string from, string to) = ValidatePaths(source, destination);
        progress?.Report(new CustomShowProgress("preparing", 0,
            "Inspecting the custom-show library…"));
        (int files, long bytes) = Inventory(from, token);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        bool sameVolume = string.Equals(Path.GetPathRoot(from),
            Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
            MoveOnSameVolume(from, to, progress, token);
        else
            CopyAcrossVolumes(from, to, files, progress, token);

        string? cleanupWarning = null;
        if (!sameVolume)
        {
            try { Directory.Delete(from, true); }
            catch (Exception error)
            {
                cleanupWarning = "The new library is active, but the old " +
                    $"folder could not be completely removed: {error.Message}";
            }
        }
        progress?.Report(new CustomShowProgress("moving", 100,
            "Custom-show library moved"));
        return new CustomShowLibraryMoveResult(files, bytes, cleanupWarning);
    }

    static void MoveOnSameVolume(string source, string destination,
        IProgress<CustomShowProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        progress?.Report(new CustomShowProgress("moving", 40,
            "Moving the library on this drive…"));
        if (Directory.Exists(destination)) Directory.Delete(destination);
        bool moved = false;
        try
        {
            Directory.Move(source, destination);
            moved = true;
            progress?.Report(new CustomShowProgress("moving", 90,
                "Updating saved library paths…"));
            RebaseJsonPaths(destination, source, destination, token);
        }
        catch
        {
            if (moved && Directory.Exists(destination) &&
                !Directory.Exists(source))
            {
                try
                {
                    RebaseJsonPaths(destination, destination, source,
                        CancellationToken.None);
                    Directory.Move(destination, source);
                }
                catch { }
            }
            throw;
        }
    }

    static void CopyAcrossVolumes(string source, string destination,
        int totalFiles, IProgress<CustomShowProgress>? progress,
        CancellationToken token)
    {
        string staging = destination + ".moving-" +
            Guid.NewGuid().ToString("N");
        try
        {
            int copied = 0;
            CopyDirectory(source, staging);
            token.ThrowIfCancellationRequested();
            progress?.Report(new CustomShowProgress("moving", 92,
                "Updating saved library paths…"));
            RebaseJsonPaths(staging, source, destination, token);
            if (Directory.Exists(destination)) Directory.Delete(destination);
            Directory.Move(staging, destination);
            return;

            void CopyDirectory(string from, string to)
            {
                token.ThrowIfCancellationRequested();
                DirectoryInfo sourceFolder = new(from);
                if ((sourceFolder.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "The custom-show library contains a linked folder, " +
                        $"which cannot be moved safely: {from}");
                Directory.CreateDirectory(to);
                foreach (string file in Directory.EnumerateFiles(from))
                {
                    token.ThrowIfCancellationRequested();
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)),
                        false);
                    copied++;
                    int percent = totalFiles == 0 ? 90 :
                        5 + (int)Math.Round(copied * 85d / totalFiles);
                    progress?.Report(new CustomShowProgress("moving", percent,
                        $"Copied {copied:N0} of {totalFiles:N0} files…"));
                }
                foreach (string folder in Directory.EnumerateDirectories(from))
                    CopyDirectory(folder,
                        Path.Combine(to, Path.GetFileName(folder)));
            }
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch { }
            throw;
        }
    }

    static (int Files, long Bytes) Inventory(string root,
        CancellationToken token)
    {
        int files = 0;
        long bytes = 0;
        Count(root);
        return (files, bytes);

        void Count(string folder)
        {
            token.ThrowIfCancellationRequested();
            DirectoryInfo directory = new(folder);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "The custom-show library contains a linked folder, " +
                    $"which cannot be moved safely: {folder}");
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                token.ThrowIfCancellationRequested();
                files++;
                bytes = checked(bytes + new FileInfo(file).Length);
            }
            foreach (string child in Directory.EnumerateDirectories(folder))
                Count(child);
        }
    }

    static void RebaseJsonPaths(string root, string oldRoot, string newRoot,
        CancellationToken token)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*.json",
            SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            JsonNode? json;
            try { json = JsonNode.Parse(File.ReadAllText(path)); }
            catch (JsonException) { continue; }
            if (json == null || !RebaseNode(json, oldRoot, newRoot)) continue;
            CustomShowStore.WriteJsonAtomic(path, json);
        }
    }

    static bool RebaseNode(JsonNode node, string oldRoot, string newRoot)
    {
        bool changed = false;
        if (node is JsonObject item)
        {
            foreach (string name in item.Select(value => value.Key).ToArray())
            {
                JsonNode? child = item[name];
                if (child is JsonValue value &&
                    value.TryGetValue(out string? text) && text != null)
                {
                    string rebased = RebasePath(text, oldRoot, newRoot);
                    if (rebased != text) { item[name] = rebased; changed = true; }
                }
                else if (child != null)
                    changed |= RebaseNode(child, oldRoot, newRoot);
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                JsonNode? child = array[index];
                if (child is JsonValue value &&
                    value.TryGetValue(out string? text) && text != null)
                {
                    string rebased = RebasePath(text, oldRoot, newRoot);
                    if (rebased != text) { array[index] = rebased; changed = true; }
                }
                else if (child != null)
                    changed |= RebaseNode(child, oldRoot, newRoot);
            }
        }
        return changed;
    }

    static string RebasePath(string value, string oldRoot, string newRoot)
    {
        if (!Path.IsPathFullyQualified(value)) return value;
        try
        {
            string full = Normalize(value);
            if (SamePath(full, oldRoot)) return newRoot;
            if (!IsWithin(oldRoot, full)) return value;
            return Path.Combine(newRoot, Path.GetRelativePath(oldRoot, full));
        }
        catch { return value; }
    }

    static bool IsWithin(string parent, string child)
    {
        string prefix = Normalize(parent) + Path.DirectorySeparatorChar;
        return Normalize(child).StartsWith(prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(path));

    internal static bool VerifyContracts()
    {
        string parent = Path.Combine(Path.GetTempPath(),
            "iqp-library-move-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(parent, "source");
        string destination = Path.Combine(parent, "destination");
        string external = Path.Combine(parent, "external.mp4");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "queue"));
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(Path.Combine(source, "video.mp4"), [1, 2, 3]);
            CustomShowStore.WriteJsonAtomic(Path.Combine(source, "queue",
                "paths.json"), new JsonObject
                {
                    ["sourcePath"] = Path.Combine(source, "video.mp4"),
                    ["externalPath"] = external,
                    ["relativePath"] = "clips/video.mp4"
                });
            CustomShowLibraryMoveResult result = Move(source, destination,
                null, CancellationToken.None);
            JsonNode moved = JsonNode.Parse(File.ReadAllText(Path.Combine(
                destination, "queue", "paths.json")))!;
            string? movedSource = moved["sourcePath"]?.GetValue<string>();
            string? movedExternal = moved["externalPath"]?.GetValue<string>();
            string? relative = moved["relativePath"]?.GetValue<string>();
            if (result.FileCount != 2 || result.Bytes <= 3 ||
                Directory.Exists(source) || !File.Exists(Path.Combine(
                    destination, "video.mp4")) ||
                !SamePath(movedSource ?? "", Path.Combine(destination,
                    "video.mp4")) || movedExternal != external ||
                relative != "clips/video.mp4")
                return false;

            string nonempty = Path.Combine(parent, "nonempty");
            Directory.CreateDirectory(nonempty);
            File.WriteAllText(Path.Combine(nonempty, "file.txt"), "x");
            try { ValidatePaths(destination, nonempty); return false; }
            catch (InvalidDataException) { }
            try
            {
                ValidatePaths(destination,
                    Path.Combine(destination, "nested"));
                return false;
            }
            catch (InvalidDataException) { }
            return true;
        }
        finally
        {
            try { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
            catch { }
        }
    }
}
