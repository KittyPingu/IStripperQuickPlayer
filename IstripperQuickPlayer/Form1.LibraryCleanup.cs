using Microsoft.Win32;
using System.Diagnostics;

namespace IStripperQuickPlayer;

public partial class Form1
{
    private enum LibraryCleanupKind
    {
        NonPurchasedPreviews,
        RegularDemos,
        AllDemos,
        InactiveClips,
        InactiveCards,
        DeletedCards,
        IntermediateFiles
    }

    private sealed record LibraryRoots(string Data, string[] Models);

    private sealed class CleanupPlan
    {
        internal HashSet<string> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Directories { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> Backups { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal List<(string Source, string Destination)> Renames { get; } = [];

        internal bool IsEmpty =>
            Files.Count == 0 && Directories.Count == 0 && Renames.Count == 0;

        internal void AddFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) &&
                !Directories.Any(directory => IsInside(fullPath, directory)))
                Files.Add(fullPath);
        }

        internal void AddDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath) || !Directories.Add(fullPath))
                return;
            Files.RemoveWhere(file => IsInside(file, fullPath));
        }

        internal (int Files, long Bytes) Measure()
        {
            HashSet<string> paths = new(Files,
                StringComparer.OrdinalIgnoreCase);
            foreach (string directory in Directories)
            {
                paths.UnionWith(Directory.EnumerateFiles(directory, "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }));
            }
            return (paths.Count, paths.Sum(path => new FileInfo(path).Length));
        }
    }

    private sealed record CleanupResult(
        int RemovedFiles, int RemovedDirectories, List<string> Errors);

    private readonly ToolStripMenuItem cleanLibraryToolStripMenuItem =
        new("Clean Library");

    private void SetupLibraryCleaner()
    {
        AddCleanupItem("Remove previews of non-purchased cards",
            LibraryCleanupKind.NonPurchasedPreviews,
            "Remove preview-only card folders that contain no purchased clips.");
        AddCleanupItem("Remove regular demo files",
            LibraryCleanupKind.RegularDemos,
            "Remove regular demos while retaining interactive transition demos.");
        AddCleanupItem("Remove all demo files",
            LibraryCleanupKind.AllDemos,
            "Remove regular and interactive demo clips.");
        cleanLibraryToolStripMenuItem.DropDownItems.Add(
            new ToolStripSeparator());
        AddCleanupItem("Remove inactive clips",
            LibraryCleanupKind.InactiveClips,
            "Remove clips listed by iStripper as inactive.");
        AddCleanupItem("Remove inactive cards",
            LibraryCleanupKind.InactiveCards,
            "Remove card folders marked as disabled by iStripper.");
        AddCleanupItem("Remove deleted cards",
            LibraryCleanupKind.DeletedCards,
            "Remove remaining card folders marked as deleted by iStripper.");
        cleanLibraryToolStripMenuItem.DropDownItems.Add(
            new ToolStripSeparator());
        AddCleanupItem("Remove intermediate download files",
            LibraryCleanupKind.IntermediateFiles,
            "Remove incomplete download and request files.");

        int position = fileToolStripMenuItem.DropDownItems.IndexOf(
            libraryHealthCheckToolStripMenuItem) + 1;
        fileToolStripMenuItem.DropDownItems.Insert(
            position, cleanLibraryToolStripMenuItem);
    }

    private void AddCleanupItem(string text, LibraryCleanupKind kind,
        string tooltip)
    {
        ToolStripMenuItem item = new(text) { ToolTipText = tooltip };
        item.Click += async (_, _) => await CleanLibraryAsync(kind, text);
        cleanLibraryToolStripMenuItem.DropDownItems.Add(item);
    }

    private async Task CleanLibraryAsync(LibraryCleanupKind kind, string title)
    {
        if (IsIStripperRunning())
        {
            MessageBox.Show(this,
                "Exit iStripper from its tray icon before cleaning the library.",
                "Clean Library", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        cleanLibraryToolStripMenuItem.Enabled = false;
        Cursor previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            LibraryRoots roots = ReadLibraryRoots();
            CleanupPlan plan = await Task.Run(() => BuildCleanupPlan(
                roots, kind));
            if (plan.IsEmpty)
            {
                MessageBox.Show(this, "Nothing matched this cleanup.",
                    "Clean Library", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            (int fileCount, long bytes) = await Task.Run(plan.Measure);
            DialogResult confirm = MessageBox.Show(this,
                $"{title}?\r\n\r\n" +
                $"{fileCount:N0} files ({FormatBytes(bytes)}) and " +
                $"{plan.Directories.Count:N0} folders will be permanently " +
                $"removed.\r\n\r\nThis cannot be undone.",
                "Clean Library", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
                return;
            if (IsIStripperRunning())
                throw new InvalidOperationException(
                    "iStripper was started again. Exit it before cleaning.");

            CleanupResult result = await Task.Run(() => ExecuteCleanup(plan));
            if (result.Errors.Count > 0)
            {
                MessageBox.Show(this,
                    $"Cleanup completed with {result.Errors.Count:N0} " +
                    $"error(s).\r\n\r\n" +
                    string.Join("\r\n", result.Errors.Take(5)),
                    "Clean Library", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            MessageBox.Show(this,
                $"Removed {result.RemovedFiles:N0} files and " +
                $"{result.RemovedDirectories:N0} folders.\r\n\r\n" +
                "Restart iStripper before reloading the library.",
                "Clean Library", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                "The library could not be cleaned.\r\n" + exception.Message,
                "Clean Library", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = previousCursor;
            cleanLibraryToolStripMenuItem.Enabled = true;
        }
    }

    private static LibraryRoots ReadLibraryRoots()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Totem\vghd\System", false);
        string data = NormalizeRoot(
            key?.GetValue("DataPath", "")?.ToString() ?? "");
        string primary = NormalizeRoot(
            key?.GetValue("ModelsPath", "")?.ToString() ?? "");
        object? multiValue = key?.GetValue("ModelsMultiPath");
        IEnumerable<string> additional = multiValue switch
        {
            string[] paths => paths,
            string paths => paths.Split('\0',
                StringSplitOptions.RemoveEmptyEntries),
            _ => []
        };
        string[] models = new[] { primary }.Concat(additional)
            .Select(NormalizeRoot)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!Directory.Exists(data))
            throw new DirectoryNotFoundException(
                "The iStripper data folder was not found.");
        if (models.Length == 0 || models.Any(path => !Directory.Exists(path)))
            throw new DirectoryNotFoundException(
                "One or more iStripper model folders were not found.");
        return new LibraryRoots(data, models);
    }

    private static string NormalizeRoot(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" :
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path.Trim().Trim('"')));

    private static CleanupPlan BuildCleanupPlan(LibraryRoots roots,
        LibraryCleanupKind kind)
    {
        CleanupPlan plan = new();
        switch (kind)
        {
            case LibraryCleanupKind.NonPurchasedPreviews:
                foreach (string folder in CardFoldersIn(roots.Models))
                    if (!Files(folder, "*.vghd").Any())
                        plan.AddDirectory(folder);
                break;

            case LibraryCleanupKind.RegularDemos:
                foreach (string folder in CardFoldersIn(roots.Models))
                {
                    string[] interactive = Files(folder, "*.demo")
                        .Where(path => Path.GetFileName(path).Contains(
                            "it", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (!Files(folder, "*.vghd").Any() &&
                        interactive.Length == 0)
                    {
                        plan.AddDirectory(folder);
                        continue;
                    }
                    foreach (string file in Files(folder, "*.demo")
                        .Where(path => !interactive.Contains(path,
                            StringComparer.OrdinalIgnoreCase))
                        .Concat(Files(folder, "*.vhdtrailers"))
                        .Concat(Files(folder, "*.vhdp2p")))
                        plan.AddFile(file);
                }
                break;

            case LibraryCleanupKind.AllDemos:
                foreach (string folder in CardFoldersIn(roots.Models))
                {
                    if (!Files(folder, "*.vghd").Any())
                    {
                        plan.AddDirectory(folder);
                        continue;
                    }
                    foreach (string file in Files(folder, "*.demo")
                        .Concat(Files(folder, "*.vhdtrailers"))
                        .Concat(Files(folder, "*.vhdp2p")))
                        plan.AddFile(file);
                }
                break;

            case LibraryCleanupKind.InactiveClips:
                AddInactiveClips(roots, plan);
                break;

            case LibraryCleanupKind.InactiveCards:
                AddMarkedCards(roots, plan, ".vhddisabled");
                break;

            case LibraryCleanupKind.DeletedCards:
                AddMarkedCards(roots, plan, ".vhddel");
                break;

            case LibraryCleanupKind.IntermediateFiles:
                foreach (string file in RecursiveFiles(
                    roots.Data, "*.dlmcdf", "*.vhdaskshows")
                    .Concat(roots.Models.SelectMany(root =>
                        RecursiveFiles(root, "*.dlmcdf"))))
                    plan.AddFile(file);
                break;
        }

        if (!plan.IsEmpty && kind is
            LibraryCleanupKind.NonPurchasedPreviews or
            LibraryCleanupKind.RegularDemos or
            LibraryCleanupKind.AllDemos or
            LibraryCleanupKind.InactiveClips or
            LibraryCleanupKind.InactiveCards)
        {
            string modelsList = Path.Combine(roots.Data, "models.lst");
            plan.AddFile(modelsList);
            if (File.Exists(modelsList))
                plan.Backups[Path.GetFullPath(modelsList)] =
                    Path.GetFullPath(modelsList + ".backup");
        }
        return plan;
    }

    private static void AddInactiveClips(LibraryRoots roots, CleanupPlan plan)
    {
        foreach (string dataFolder in CardFoldersIn([roots.Data]))
        {
            string inactive = Path.Combine(dataFolder, "inactive.txt");
            if (!File.Exists(inactive))
                continue;
            string line = File.ReadLines(inactive).FirstOrDefault() ?? "";
            string[] clips = line.Split('#',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            string tag = Path.GetFileName(dataFolder);
            foreach (string clip in clips)
            {
                if (!string.Equals(clip, Path.GetFileName(clip),
                    StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Invalid inactive clip name in {inactive}.");
                foreach (string root in roots.Models)
                {
                    string cardFolder = Path.Combine(root, tag);
                    plan.AddFile(Path.Combine(cardFolder, clip));
                    if (Directory.Exists(cardFolder) &&
                        !Files(cardFolder, "*.vghd")
                            .Concat(Files(cardFolder, "*.demo"))
                            .Any(path => !plan.Files.Contains(
                                Path.GetFullPath(path))))
                        plan.AddDirectory(cardFolder);
                }
            }
            plan.Renames.Add((Path.GetFullPath(inactive),
                Path.GetFullPath(inactive + ".backup")));
        }
    }

    private static void AddMarkedCards(LibraryRoots roots, CleanupPlan plan,
        string markerExtension)
    {
        foreach (string dataFolder in CardFoldersIn([roots.Data]))
        {
            string tag = Path.GetFileName(dataFolder);
            if (!File.Exists(Path.Combine(
                dataFolder, tag + markerExtension)))
                continue;
            foreach (string root in roots.Models)
                plan.AddDirectory(Path.Combine(root, tag));
        }
    }

    private static IEnumerable<string> CardFoldersIn(
        IEnumerable<string> roots) =>
        roots.SelectMany(root => Directory.EnumerateDirectories(root)
            .Where(directory => IsCardTag(Path.GetFileName(directory))));

    private static bool IsCardTag(string name) =>
        name.Length == 5 &&
        char.ToLowerInvariant(name[0]) is >= 'a' and <= 'i' &&
        name.AsSpan(1).ToString().All(char.IsDigit);

    private static IEnumerable<string> Files(string folder, string pattern) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(
                folder, pattern, SearchOption.TopDirectoryOnly)
            : [];

    private static IEnumerable<string> RecursiveFiles(
        string root, params string[] patterns)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return patterns.SelectMany(pattern =>
            Directory.EnumerateFiles(root, pattern, options));
    }

    private static CleanupResult ExecuteCleanup(CleanupPlan plan)
    {
        int removedFiles = 0;
        int removedDirectories = 0;
        List<string> errors = [];
        foreach (string file in plan.Files)
        {
            try
            {
                if (plan.Backups.TryGetValue(file, out string? backup))
                    File.Copy(file, backup, true);
                File.Delete(file);
                removedFiles++;
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }
        foreach (string directory in plan.Directories)
        {
            try
            {
                Directory.Delete(directory, true);
                removedDirectories++;
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(directory)}: " +
                    exception.Message);
            }
        }
        if (errors.Count == 0)
        {
            foreach ((string source, string destination) in plan.Renames)
            {
                try
                {
                    File.Move(source, destination, true);
                }
                catch (Exception exception)
                {
                    errors.Add($"{Path.GetFileName(source)}: " +
                        exception.Message);
                }
            }
        }
        return new CleanupResult(
            removedFiles, removedDirectories, errors);
    }

    private static bool IsInside(string path, string directory)
    {
        string prefix = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(
            prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIStripperRunning()
    {
        Process[] processes = Process.GetProcessesByName("vghd");
        foreach (Process process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    internal static bool VerifyLibraryCleaner()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "IstripperQuickPlayer-cleaner-" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Directory.CreateDirectory(
                Path.Combine(root, "data")).FullName;
            string models = Directory.CreateDirectory(
                Path.Combine(root, "models")).FullName;
            string purchased = Directory.CreateDirectory(
                Path.Combine(models, "a0001")).FullName;
            File.WriteAllText(Path.Combine(purchased, "clip.vghd"), "clip");
            File.WriteAllText(Path.Combine(purchased, "regular.demo"), "demo");
            File.WriteAllText(Path.Combine(
                purchased, "transitionit.demo"), "interactive");
            File.WriteAllText(Path.Combine(
                purchased, "clip.vhdtrailers"), "index");

            CleanupPlan regular = BuildCleanupPlan(
                new LibraryRoots(data, [models]),
                LibraryCleanupKind.RegularDemos);
            CleanupResult result = ExecuteCleanup(regular);
            if (result.Errors.Count != 0 ||
                File.Exists(Path.Combine(purchased, "regular.demo")) ||
                File.Exists(Path.Combine(purchased, "clip.vhdtrailers")) ||
                !File.Exists(Path.Combine(
                    purchased, "transitionit.demo")) ||
                !File.Exists(Path.Combine(purchased, "clip.vghd")))
                return false;

            string preview = Directory.CreateDirectory(
                Path.Combine(models, "b0002")).FullName;
            File.WriteAllText(Path.Combine(preview, "preview.demo"), "demo");
            CleanupPlan previews = BuildCleanupPlan(
                new LibraryRoots(data, [models]),
                LibraryCleanupKind.NonPurchasedPreviews);
            result = ExecuteCleanup(previews);
            return result.Errors.Count == 0 &&
                !Directory.Exists(preview);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
