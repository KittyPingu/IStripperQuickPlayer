using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class DatasetStore
{
    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".ts", ".mts", ".m2ts" };
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { WriteIndented = true };
    readonly object gate = new();
    readonly Random random = new();

    internal string Root { get; }
    internal TrainingDataset Dataset { get; private set; }
    string ManifestPath => Path.Combine(Root, "dataset.json");

    internal DatasetStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        RecoverManifest(ManifestPath);
        Dataset = File.Exists(ManifestPath)
            ? JsonSerializer.Deserialize<TrainingDataset>(File.ReadAllText(ManifestPath), JsonOptions)
                ?? throw new InvalidDataException("dataset.json is invalid.")
            : new TrainingDataset();
        if (Dataset.SchemaVersion != 1) throw new InvalidDataException("Unsupported dataset schema.");
        Save();
    }

    internal async Task<int> IndexFolderAsync(string folder, IProgress<string>? progress, CancellationToken token)
    {
        folder = Path.GetFullPath(folder);
        List<string> files = [];
        foreach (string path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
            if (VideoExtensions.Contains(Path.GetExtension(path))) files.Add(path);

        int added = 0, index = 0;
        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"Probing {++index:N0}/{files.Count:N0}: {Path.GetFileName(path)}");
            FileInfo file = new(path);
            string id = SourceId(file);
            if (Dataset.Sources.Any(value => value.Id == id)) continue;
            VideoSource source = new()
            {
                Id = id, Path = file.FullName, Length = file.Length,
                LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks, Split = SplitFor(id)
            };
            try
            {
                MediaInfo media = await ProbeAsync(path, token);
                source.DurationMs = media.DurationMs; source.Width = media.Width;
                source.Height = media.Height; source.FramesPerSecond = media.FramesPerSecond;
            }
            catch (Exception error) { source.ProbeError = error.Message; }
            lock (gate) { Dataset.Sources.Add(source); added++; SaveLocked(); }
        }
        lock (gate)
        {
            Dataset.SourceFolders = Dataset.SourceFolders.Append(folder)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            SaveLocked();
        }
        return added;
    }

    internal TrainingSample? NextCandidate()
    {
        lock (gate)
        {
            TrainingSample? queued = Dataset.Samples.FirstOrDefault(value => value.Decision == "draft" &&
                value.FramePath is not null && File.Exists(Resolve(value.FramePath)));
            if (queued != null) return queued;
            List<VideoSource> eligible = Dataset.Sources.Where(value => value.ProbeError == null &&
                value.DurationMs > 60_000 && File.Exists(value.Path)).ToList();
            for (int attempt = 0; attempt < 2000 && eligible.Count > 0; attempt++)
            {
                VideoSource source = eligible[random.Next(eligible.Count)];
                long timestamp = random.NextInt64(30_000, source.DurationMs - 30_000 + 1);
                timestamp = FrameTimestamp(timestamp, source.FramesPerSecond);
                bool near = Dataset.Samples.Any(value => value.SourceId == source.Id &&
                    Math.Abs(value.TimestampMs - timestamp) <= 10_000);
                if (near) continue;
                TrainingSample sample = new()
                {
                    SourceId = source.Id, TimestampMs = timestamp, Split = source.Split
                };
                Dataset.Samples.Add(sample); SaveLocked(); return sample;
            }
            return null;
        }
    }

    internal async Task ExtractDraftFrameAsync(TrainingSample sample, CancellationToken token)
    {
        VideoSource source = Source(sample.SourceId);
        string relative = Path.Combine("drafts", sample.Id, "frame.png");
        string destination = Resolve(relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await RunAsync(FfmpegPath(), ["-y", "-v", "error", "-ss",
            (sample.TimestampMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture),
            "-i", source.Path, "-frames:v", "1", "-vf", "setsar=1", destination], token);
        using Image image = Image.FromFile(destination);
        lock (gate)
        {
            sample.FramePath = Relative(destination); sample.Width = image.Width; sample.Height = image.Height;
            SaveLocked();
        }
    }

    internal async Task<IReadOnlyList<TrainingSample>> CreateBurstAsync(TrainingSample anchor,
        CancellationToken token)
    {
        if (anchor.BurstId is string existingId)
        {
            TrainingSample[] existing = Dataset.Samples.Where(value => value.BurstId == existingId)
                .OrderBy(value => value.BurstIndex).ToArray();
            if (existing.Length == 9) return existing;
        }
        VideoSource source = Source(anchor.SourceId);
        string burstId = anchor.BurstId ?? Guid.NewGuid().ToString("N");
        anchor.BurstId = burstId; anchor.BurstIndex = 4;
        double frameMs = 1000 / Math.Max(1, source.FramesPerSecond);
        List<TrainingSample> result = [anchor];
        for (int offset = -4; offset <= 4; offset++)
        {
            if (offset == 0) continue;
            long timestamp = Math.Clamp((long)Math.Round(anchor.TimestampMs + offset * frameMs),
                30_000, source.DurationMs - 30_000);
            TrainingSample sample = new()
            {
                SourceId = source.Id, TimestampMs = timestamp, Split = source.Split,
                BurstId = burstId, BurstIndex = offset + 4
            };
            lock (gate) { Dataset.Samples.Add(sample); SaveLocked(); }
            await ExtractDraftFrameAsync(sample, token); result.Add(sample);
        }
        lock (gate) SaveLocked();
        return result.OrderBy(value => value.BurstIndex).ToArray();
    }

    internal void Accept(TrainingSample sample, int[] objectIds,
        IReadOnlyList<AnnotationObject> objects, string provenance)
    {
        if (sample.FramePath == null) throw new InvalidOperationException("The draft frame is missing.");
        if (objectIds.Length != sample.Width * sample.Height || objectIds.Any(value => value is < 0 or > 65535))
            throw new InvalidDataException("The instance mask dimensions or object IDs are invalid.");
        bool positive = objectIds.Any(value => value > 0);
        string folder = Path.Combine("samples", sample.SourceId, sample.Id);
        string sourceFrame = Resolve(sample.FramePath);
        string frame = Resolve(Path.Combine(folder, "frame.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(frame)!); File.Move(sourceFrame, frame, true);
        string editable = Resolve(Path.Combine(folder, "instance-mask.i32.gz"));
        WriteEditableMask(editable, objectIds);
        string instance = Resolve(Path.Combine(folder, "instance-mask.png"));
        string prop = Resolve(Path.Combine(folder, "prop-mask.png"));
        PngMaskWriter.SaveGray16(instance, sample.Width, sample.Height, objectIds);
        byte[] binary = objectIds.Select(value => value > 0 ? (byte)255 : (byte)0).ToArray();
        PngMaskWriter.SaveGray8(prop, sample.Width, sample.Height, binary);
        string annotation = Resolve(Path.Combine(folder, "annotation.json"));
        WriteJsonAtomic(annotation, new SampleAnnotation
        {
            Objects = objects.Where(value => objectIds.Contains(value.Id)).ToList(),
            Provenance = provenance
        });
        lock (gate)
        {
            sample.Decision = positive ? "positive" : "negative";
            sample.ObjectCount = objects.Count(value => objectIds.Contains(value.Id));
            sample.FramePath = Relative(frame); sample.EditableMaskPath = Relative(editable);
            sample.InstanceMaskPath = Relative(instance); sample.PropMaskPath = Relative(prop);
            sample.AnnotationPath = Relative(annotation); sample.ReviewedUtc = DateTime.UtcNow;
            sample.DerivationStatus = "pending"; SaveLocked();
        }
        TryDeleteDraft(sample.Id);
    }

    internal void SaveDraftAnnotation(TrainingSample sample, int[] objectIds,
        IReadOnlyList<AnnotationObject> objects, string provenance)
    {
        string folder = Path.Combine("drafts", sample.Id);
        string editable = Resolve(Path.Combine(folder, "instance-mask.i32.gz"));
        string instance = Resolve(Path.Combine(folder, "instance-mask.png"));
        string annotation = Resolve(Path.Combine(folder, "annotation.json"));
        WriteEditableMask(editable, objectIds);
        PngMaskWriter.SaveGray16(instance, sample.Width, sample.Height, objectIds);
        WriteJsonAtomic(annotation, new SampleAnnotation
        {
            Objects = objects.Where(value => objectIds.Contains(value.Id)).ToList(),
            Provenance = provenance
        });
        lock (gate)
        {
            sample.EditableMaskPath = Relative(editable);
            sample.InstanceMaskPath = Relative(instance);
            sample.AnnotationPath = Relative(annotation);
            sample.ObjectCount = objects.Count(value => objectIds.Contains(value.Id));
            SaveLocked();
        }
    }

    internal (int[]? Ids, AnnotationObject[] Objects, string Provenance) LoadDraftAnnotation(
        TrainingSample sample)
    {
        if (sample.EditableMaskPath == null || sample.AnnotationPath == null) return (null, [], "manual");
        string editable = Resolve(sample.EditableMaskPath), annotation = Resolve(sample.AnnotationPath);
        if (!File.Exists(editable) || !File.Exists(annotation)) return (null, [], "manual");
        int[] ids = new int[sample.Width * sample.Height];
        using (FileStream file = File.OpenRead(editable))
        using (GZipStream gzip = new(file, CompressionMode.Decompress))
        using (BinaryReader reader = new(gzip))
            for (int index = 0; index < ids.Length; index++) ids[index] = reader.ReadInt32();
        SampleAnnotation value = JsonSerializer.Deserialize<SampleAnnotation>(
            File.ReadAllText(annotation), JsonOptions) ?? new();
        return (ids, value.Objects.ToArray(), value.Provenance);
    }

    internal void Reject(TrainingSample sample)
    {
        lock (gate)
        {
            sample.Decision = "rejected"; sample.ReviewedUtc = DateTime.UtcNow;
            sample.FramePath = null; SaveLocked();
        }
        TryDeleteDraft(sample.Id);
    }

    internal void SetDerivation(TrainingSample sample, string status, string? personMask,
        string? desiredMask, string? error, string? rvmRevision, double rvmThreshold)
    {
        lock (gate)
        {
            sample.DerivationStatus = status; sample.RvmPersonMaskPath = personMask;
            sample.DesiredForegroundPath = desiredMask; sample.DerivationError = error;
            sample.RvmRevision = rvmRevision; sample.RvmThreshold = rvmThreshold;
            SaveLocked();
        }
    }

    internal DatasetStatistics Statistics()
    {
        lock (gate)
        {
            List<TrainingSample> accepted = Dataset.Samples.Where(value => value.Accepted).ToList();
            return new(
                accepted.Count(value => value.Decision == "positive"),
                accepted.Count(value => value.Decision == "negative"),
                Dataset.Samples.Count(value => value.Decision == "rejected"),
                Dataset.Samples.Count(value => value.Decision == "draft"),
                accepted.Sum(value => value.ObjectCount), Dataset.Sources.Count,
                accepted.Select(value => value.SourceId).Distinct().Count(),
                accepted.Count(value => value.Split == "train"),
                accepted.Count(value => value.Split == "validation"),
                accepted.Count(value => value.Split == "test"));
        }
    }

    internal VideoSource Source(string id) => Dataset.Sources.Single(value => value.Id == id);
    internal string Resolve(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
    internal string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    internal void Save() { lock (gate) SaveLocked(); }

    void SaveLocked()
    {
        Dataset.UpdatedUtc = DateTime.UtcNow; WriteJsonAtomic(ManifestPath, Dataset);
    }

    internal static string SourceId(FileInfo file)
    {
        string identity = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
    }

    internal static string SplitFor(string sourceId)
    {
        uint bucket = Convert.ToUInt32(sourceId[..8], 16) % 100;
        return bucket < 70 ? "train" : bucket < 85 ? "validation" : "test";
    }

    internal static long FrameTimestamp(long timestamp, double fps) => fps <= 0
        ? timestamp : (long)Math.Round(Math.Round(timestamp * fps / 1000) * 1000 / fps);

    static async Task<MediaInfo> ProbeAsync(string path, CancellationToken token)
    {
        string output = await RunCaptureAsync(FfprobePath(), ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,avg_frame_rate:format=duration", "-of", "json", path], token);
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement stream = json.RootElement.GetProperty("streams")[0];
        int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
        string[] rate = stream.GetProperty("avg_frame_rate").GetString()!.Split('/');
        double fps = double.Parse(rate[0], CultureInfo.InvariantCulture) /
                     Math.Max(1, double.Parse(rate[1], CultureInfo.InvariantCulture));
        double duration = double.Parse(json.RootElement.GetProperty("format")
            .GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return new((long)Math.Round(duration * 1000), width, height, fps);
    }

    internal static async Task RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken token) => _ = await RunCaptureAsync(executable, arguments, token);

    internal static async Task<string> RunCaptureAsync(string executable,
        IEnumerable<string> arguments, CancellationToken token)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        return await stdout;
    }

    static string FfmpegPath() => ToolPath("ffmpeg.exe");
    static string FfprobePath() => ToolPath("ffprobe.exe");
    static string ToolPath(string name)
    {
        string local = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(local) ? local : name;
    }

    void TryDeleteDraft(string id)
    {
        try { Directory.Delete(Path.Combine(Root, "drafts", id), true); } catch { }
    }

    static void WriteEditableMask(string path, int[] objectIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream file = File.Create(path);
        using GZipStream gzip = new(file, CompressionLevel.Fastest);
        using BinaryWriter writer = new(gzip);
        foreach (int value in objectIds) writer.Write(value);
    }

    internal static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    static void RecoverManifest(string path)
    {
        if (File.Exists(path)) return;
        string? folder = Path.GetDirectoryName(path);
        if (folder == null || !Directory.Exists(folder)) return;
        foreach (string candidate in Directory.EnumerateFiles(folder,
                     Path.GetFileName(path) + ".tmp-*").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                TrainingDataset? dataset = JsonSerializer.Deserialize<TrainingDataset>(
                    File.ReadAllText(candidate), JsonOptions);
                if (dataset?.SchemaVersion != 1) continue;
                File.Move(candidate, path); return;
            }
            catch { }
        }
    }
}
