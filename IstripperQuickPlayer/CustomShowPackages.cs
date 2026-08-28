using System.IO.Compression;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomShowPackageDescriptor
{
    public int SchemaVersion { get; set; } = 1;
    public string ShowId { get; set; } = "";
    public string PerformerId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed record CustomShowPackageImportResult(
    string ShowId, string ShowTitle, string ModelName, bool CreatedModel);

internal sealed partial class CustomShowStore
{
    const string PackageDescriptorName = "package.json";
    const string PackagePerformerName = "performer.json";
    const string PackageShowFolder = "show";

    internal void ExportPackage(string showId, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("An export filename is required.");
        CustomShowManifest show = LoadManifest(showId);
        CustomPerformerProfile performer = LoadPerformer(show.PerformerId);
        ValidateLink(show, performer);
        string showFolder = Path.Combine(ShowsFolder, show.Id);
        string output = Path.GetFullPath(destination);
        if (IsInside(showFolder, output))
            throw new InvalidOperationException(
                "Save the exported package outside the custom show's folder.");

        CustomShowManifest portableShow = ClonePackageValue(show);
        MakeReferenceSourcesPortable(portableShow);
        string[] showFiles = EnumeratePortableFiles(showFolder);
        HashSet<string> showRelativePaths = showFiles.Select(path =>
            NormalizeArchivePath(Path.GetRelativePath(showFolder, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        showRelativePaths.Remove("show.json");

        string[] photoFiles = [];
        string photos = portableShow.Media.PhotosFolder;
        if (!string.IsNullOrWhiteSpace(photos))
        {
            string photoFolder = Path.GetFullPath(photos);
            if (!Directory.Exists(photoFolder))
                portableShow.Media.PhotosFolder = "";
            else if (TryRelativeFolder(showFolder, photoFolder,
                         out string relativePhotos))
                portableShow.Media.PhotosFolder = relativePhotos;
            else
            {
                string packagePhotos = "__portable_photos";
                int suffix = 2;
                while (showRelativePaths.Any(path => path.Equals(packagePhotos,
                           StringComparison.OrdinalIgnoreCase) || path.StartsWith(
                           packagePhotos + "/", StringComparison.OrdinalIgnoreCase)))
                    packagePhotos = "__portable_photos_" + suffix++;
                photoFiles = EnumeratePortableFiles(photoFolder);
                portableShow.Media.PhotosFolder = photoFiles.Length == 0
                    ? "" : packagePhotos;
            }
        }

        CustomShowPackageDescriptor descriptor = new()
        {
            ShowId = show.Id,
            PerformerId = performer.Id
        };
        string? parent = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("The export folder is invalid.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent,
            "." + Path.GetFileName(output) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (ZipArchive archive = ZipFile.Open(temporary,
                       ZipArchiveMode.Create))
            {
                WriteJsonEntry(archive, PackageDescriptorName, descriptor);
                WriteJsonEntry(archive, PackagePerformerName, performer);
                WriteJsonEntry(archive, PackageShowFolder + "/show.json",
                    portableShow);
                foreach (string file in showFiles)
                {
                    string relative = NormalizeArchivePath(
                        Path.GetRelativePath(showFolder, file));
                    if (relative.Equals("show.json",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    AddFile(archive, file, PackageShowFolder + "/" + relative);
                }
                if (photoFiles.Length > 0)
                {
                    string photoFolder = Path.GetFullPath(photos);
                    foreach (string file in photoFiles)
                    {
                        string relative = NormalizeArchivePath(
                            Path.GetRelativePath(photoFolder, file));
                        AddFile(archive, file, PackageShowFolder + "/" +
                            portableShow.Media.PhotosFolder + "/" + relative);
                    }
                }
            }
            File.Move(temporary, output, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal CustomShowPackageImportResult ImportPackage(string packagePath)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("The custom-show package was not found.",
                packagePath);
        EnsureCreated();
        string importsRoot = Path.Combine(root, ".package-imports");
        Directory.CreateDirectory(importsRoot);
        string staging = Path.Combine(importsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string? createdProfilePath = null;
        try
        {
            ExtractPackage(packagePath, staging);
            CustomShowPackageDescriptor descriptor =
                ReadJson<CustomShowPackageDescriptor>(Path.Combine(staging,
                    PackageDescriptorName));
            if (descriptor.SchemaVersion != 1)
                throw new InvalidDataException(
                    "This custom-show package version is not supported.");
            CustomPerformerProfile packagedPerformer =
                ReadJson<CustomPerformerProfile>(Path.Combine(staging,
                    PackagePerformerName));
            ValidateProfile(packagedPerformer);
            string extractedShowFolder = Path.Combine(staging, PackageShowFolder);
            CustomShowManifest show = ReadManifest(Path.Combine(
                extractedShowFolder, "show.json"));
            if (!string.Equals(descriptor.ShowId, show.Id,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(descriptor.PerformerId, packagedPerformer.Id,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(show.PerformerId, packagedPerformer.Id,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The package contains mismatched show or model identifiers.");
            ValidateManifest(show, extractedShowFolder);
            ValidateLink(show, packagedPerformer);

            string destination = Path.Combine(ShowsFolder, show.Id);
            if (Directory.Exists(destination))
                throw new IOException(
                    $"'{show.Title}' is already in the custom-show library.");

            IReadOnlyList<CustomPerformerProfile> existing = LoadPerformers();
            CustomPerformerProfile? performer = existing.FirstOrDefault(value =>
                    string.Equals(value.Id, packagedPerformer.Id,
                        StringComparison.OrdinalIgnoreCase)) ??
                existing.FirstOrDefault(value =>
                    !string.IsNullOrWhiteSpace(packagedPerformer.IstripperModelId) &&
                    string.Equals(value.IstripperModelId,
                        packagedPerformer.IstripperModelId,
                        StringComparison.OrdinalIgnoreCase)) ??
                existing.FirstOrDefault(value => string.Equals(
                    value.ModelName.Trim(), packagedPerformer.ModelName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            bool createdModel = performer == null;
            if (performer == null)
            {
                performer = packagedPerformer;
                string performerPath = Path.Combine(PerformersFolder,
                    performer.Id + ".json");
                if (File.Exists(performerPath))
                {
                    performer.Id = Guid.NewGuid().ToString("N");
                    performerPath = Path.Combine(PerformersFolder,
                        performer.Id + ".json");
                }
                createdProfilePath = performerPath;
            }
            show.PerformerId = performer.Id;
            ValidateLink(show, performer);

            if (!string.IsNullOrWhiteSpace(show.Media.PhotosFolder))
            {
                string extractedPhotos = ResolveRelative(extractedShowFolder,
                    show.Media.PhotosFolder);
                show.Media.PhotosFolder = Directory.Exists(extractedPhotos)
                    ? ResolveRelative(destination, show.Media.PhotosFolder)
                    : "";
            }
            WriteJsonAtomic(Path.Combine(extractedShowFolder, "show.json"), show);
            if (createdModel) SavePerformer(performer);
            Publish(extractedShowFolder, show);
            createdProfilePath = null;
            return new(show.Id, show.Title, performer.ModelName, createdModel);
        }
        catch
        {
            if (createdProfilePath != null && File.Exists(createdProfilePath))
                File.Delete(createdProfilePath);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch { }
        }
    }

    static void ExtractPackage(string packagePath, string destination)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > 100_000)
            throw new InvalidDataException("The package contains too many files.");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        string rootPath = Path.GetFullPath(destination).TrimEnd(
            Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = NormalizeArchivePath(entry.FullName);
            if (string.IsNullOrWhiteSpace(name)) continue;
            string[] segments = name.Split('/');
            if (Path.IsPathRooted(name) || name.Contains(':') ||
                segments.Any(segment => segment is "" or "." or ".."))
                throw new InvalidDataException(
                    "The package contains an unsafe file path.");
            if (!names.Add(name))
                throw new InvalidDataException(
                    "The package contains duplicate file paths.");
            string target = Path.GetFullPath(Path.Combine(destination,
                name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The package contains a path outside its show folder.");
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = entry.Open();
            using FileStream output = new(target, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        if (!File.Exists(Path.Combine(destination, PackageDescriptorName)) ||
            !File.Exists(Path.Combine(destination, PackagePerformerName)) ||
            !File.Exists(Path.Combine(destination, PackageShowFolder, "show.json")))
            throw new InvalidDataException(
                "The ZIP is not a complete QuickPlayer custom-show package.");
    }

    static void MakeReferenceSourcesPortable(CustomShowManifest show)
    {
        MakeReferenceSourcePortable(show.Source);
        foreach (CustomShowClip clip in show.Clips)
            if (clip.Source != null) MakeReferenceSourcePortable(clip.Source);
    }

    static void MakeReferenceSourcePortable(CustomShowSource source)
    {
        if (source.Mode != "reference") return;
        string filename = Path.GetFileName(source.Path);
        source.Path = string.IsNullOrWhiteSpace(filename)
            ? "source-video-not-included" : filename;
    }

    static bool TryRelativeFolder(string parent, string child,
        out string relative)
    {
        relative = "";
        string parentRoot = Path.GetFullPath(parent).TrimEnd(
            Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string childPath = Path.GetFullPath(child).TrimEnd(
            Path.DirectorySeparatorChar);
        if (!childPath.StartsWith(parentRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        relative = NormalizeArchivePath(Path.GetRelativePath(parent, childPath));
        return relative.Length > 0 && relative != ".";
    }

    static bool IsInside(string parent, string path)
    {
        string rootPath = Path.GetFullPath(parent).TrimEnd(
            Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootPath,
            StringComparison.OrdinalIgnoreCase);
    }

    static string[] EnumeratePortableFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).ToArray();

    static string NormalizeArchivePath(string path) => path.Replace('\\', '/');

    static T ClonePackageValue<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidDataException(
                "The package metadata could not be copied.");

    static void WriteJsonEntry<T>(ZipArchive archive, string name, T value)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name,
            CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    static void AddFile(ZipArchive archive, string source, string name)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name,
            CompressionLevel.NoCompression);
        using Stream input = File.OpenRead(source);
        using Stream output = entry.Open();
        input.CopyTo(output);
    }

    internal static bool VerifyPackageContracts()
    {
        string testRoot = Path.Combine(Path.GetTempPath(),
            "iqp-package-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sourceRoot = Path.Combine(testRoot, "source");
            string destinationRoot = Path.Combine(testRoot, "destination");
            string photos = Path.Combine(testRoot, "external-photos");
            Directory.CreateDirectory(photos);
            File.WriteAllBytes(Path.Combine(photos, "photo.jpg"), [4, 5, 6]);
            CustomShowStore source = new(sourceRoot);
            CustomPerformerProfile profile = new()
            {
                ModelName = "Portable Model", Gender = "Female"
            };
            source.SavePerformer(profile);
            CustomShowManifest show = new()
            {
                PerformerId = profile.Id,
                Title = "Portable Show",
                Source = new()
                {
                    Mode = "reference",
                    Path = Path.Combine("C:\\sender-private", "source.mp4")
                },
                Media = new()
                {
                    Width = 64, Height = 64, FrameRate = "10/1",
                    DurationMs = 1000, PhotosFolder = photos
                },
                Clips =
                [
                    new CustomShowClip
                    {
                        StartMs = 0, EndMs = 1000,
                        Media = new()
                        {
                            Foreground = "clips/foreground.mp4",
                            Alpha = "clips/alpha.mkv",
                            Width = 64, Height = 64, FrameRate = "10/1",
                            DurationMs = 1000
                        }
                    }
                ]
            };
            string showFolder = Path.Combine(source.ShowsFolder, show.Id);
            Directory.CreateDirectory(Path.Combine(showFolder, "clips"));
            File.WriteAllBytes(Path.Combine(showFolder, show.Media.Cover), [1]);
            File.WriteAllBytes(Path.Combine(showFolder, "clips", "foreground.mp4"),
                [2]);
            File.WriteAllBytes(Path.Combine(showFolder, "clips", "alpha.mkv"),
                [3]);
            source.SaveManifest(show);

            string package = Path.Combine(testRoot, "show.iqpshow.zip");
            source.ExportPackage(show.Id, package);
            CustomShowStore destination = new(destinationRoot);
            CustomShowPackageImportResult imported =
                destination.ImportPackage(package);
            CustomShowManifest importedShow = destination.LoadManifest(show.Id);
            bool roundTrip = imported.CreatedModel &&
                imported.ShowId == show.Id &&
                destination.LoadPerformer(importedShow.PerformerId).ModelName ==
                    profile.ModelName &&
                importedShow.Source.Path == "source.mp4" &&
                Path.IsPathFullyQualified(importedShow.Media.PhotosFolder) &&
                File.Exists(Path.Combine(importedShow.Media.PhotosFolder,
                    "photo.jpg")) &&
                File.Exists(Path.Combine(destination.ShowsFolder, show.Id,
                    "clips", "foreground.mp4"));
            bool duplicateRejected;
            try
            {
                destination.ImportPackage(package);
                duplicateRejected = false;
            }
            catch (IOException) { duplicateRejected = true; }

            CustomShowStore reuseDestination = new(Path.Combine(testRoot,
                "reuse-destination"));
            CustomPerformerProfile localProfile = new()
            {
                ModelName = profile.ModelName, Gender = "Female"
            };
            reuseDestination.SavePerformer(localProfile);
            CustomShowPackageImportResult reused =
                reuseDestination.ImportPackage(package);
            bool existingModelReused = !reused.CreatedModel &&
                reuseDestination.LoadManifest(show.Id).PerformerId ==
                    localProfile.Id;

            string traversal = Path.Combine(testRoot, "traversal.zip");
            using (ZipArchive archive = ZipFile.Open(traversal,
                       ZipArchiveMode.Create))
                archive.CreateEntry("../escape.txt");
            bool traversalRejected;
            try
            {
                destination.ImportPackage(traversal);
                traversalRejected = false;
            }
            catch (InvalidDataException) { traversalRejected = true; }
            return roundTrip && duplicateRejected && existingModelReused &&
                traversalRejected &&
                !File.Exists(Path.Combine(testRoot, "escape.txt"));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Custom-show package check failed: " + error);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
            catch { }
        }
    }
}
