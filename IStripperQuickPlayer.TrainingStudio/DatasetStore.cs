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
    internal static readonly long[] CandidateSpacingMs = [10_000, 5_000, 2_000];
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { WriteIndented = true };
    readonly object gate = new();
    readonly Random random = new();

    internal string Root { get; }
    internal TrainingDataset Dataset { get; private set; }
    string ManifestPath => Path.Combine(Root, "dataset.json");
    string LegacySamplesManifestPath => Path.Combine(Root, "samples.json");
    string RecordsRoot => Path.Combine(Root, "records");

    internal DatasetStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        RecoverManifest(ManifestPath);
        RecoverManifest(LegacySamplesManifestPath);
        RecoverRecordManifests();
        bool manifestExists = File.Exists(ManifestPath);
        List<TrainingSample> embeddedSamples = manifestExists ? ReadLegacySamples(ManifestPath) : [];
        Dataset = manifestExists
            ? JsonSerializer.Deserialize<TrainingDataset>(File.ReadAllText(ManifestPath), JsonOptions)
                ?? throw new InvalidDataException("dataset.json is invalid.")
            : new TrainingDataset();
        if (Dataset.SchemaVersion != 1) throw new InvalidDataException("Unsupported dataset schema.");
        Dictionary<string, TrainingSample> samples = embeddedSamples.ToDictionary(value => value.Id);
        if (File.Exists(LegacySamplesManifestPath))
        {
            TrainingSampleLedger ledger = JsonSerializer.Deserialize<TrainingSampleLedger>(
                File.ReadAllText(LegacySamplesManifestPath), JsonOptions)
                ?? throw new InvalidDataException("samples.json is invalid.");
            if (ledger.SchemaVersion != 1 || ledger.DatasetId != Dataset.DatasetId)
                throw new InvalidDataException("samples.json does not belong to this dataset.");
            foreach (TrainingSample sample in ledger.Samples) samples[sample.Id] = sample;
        }
        HashSet<string> recordedIds = [];
        foreach (TrainingSample sample in ReadSampleRecords())
        {
            samples[sample.Id] = sample; recordedIds.Add(sample.Id);
        }
        Dataset.Samples = samples.Values.OrderBy(value => value.PresentedUtc).ToList();
        foreach (TrainingSample sample in Dataset.Samples)
            if (!recordedIds.Contains(sample.Id)) SaveSampleLocked(sample);
        if (!manifestExists || embeddedSamples.Count > 0) SaveSourcesLocked();
        ArchiveLegacySampleLedger();
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
            lock (gate) { Dataset.Sources.Add(source); added++; SaveSourcesLocked(); }
        }
        lock (gate)
        {
            Dataset.SourceFolders = Dataset.SourceFolders.Append(folder)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Dataset.ActiveSourceFolder = folder;
            SaveSourcesLocked();
        }
        return added;
    }

    internal TrainingSample? NextCandidate(string? preferredBurstId = null, int minimumResolution = 0,
        IReadOnlySet<string>? excludedSampleIds = null)
    {
        lock (gate)
        {
            if (preferredBurstId != null)
            {
                TrainingSample? burst = Dataset.Samples.Where(value =>
                        value.BurstId == preferredBurstId &&
                        !(excludedSampleIds?.Contains(value.Id) ?? false) &&
                        value.Decision == "draft" && value.FramePath is not null &&
                        File.Exists(Resolve(value.FramePath)))
                    .OrderBy(value => value.BurstIndex).FirstOrDefault();
                if (burst != null) return burst;
            }
            List<TrainingSample> queued = Dataset.Samples.Where(value => value.Decision == "draft" &&
                    !(excludedSampleIds?.Contains(value.Id) ?? false) &&
                    value.FramePath is not null && File.Exists(Resolve(value.FramePath)) &&
                    SampleIsInActiveFolder(value) && SampleMeetsResolution(value, minimumResolution))
                .ToList();
            if (queued.Count > 0) return queued[random.Next(queued.Count)];
            List<VideoSource> eligible = Dataset.Sources.Where(value => value.ProbeError == null &&
                value.DurationMs > 60_000 &&
                (minimumResolution <= 0 || Math.Min(value.Width, value.Height) >= minimumResolution) &&
                (Dataset.ActiveSourceFolder == null ||
                    IsSourceInFolder(value.Path, Dataset.ActiveSourceFolder))).ToList();
            foreach (long spacingMs in CandidateSpacingMs)
            {
                foreach (VideoSource source in eligible.OrderBy(_ => random.Next()))
                {
                    if (!TryCandidateTimestamp(source, spacingMs, out long timestamp)) continue;
                    TrainingSample sample = new()
                    {
                        SourceId = source.Id, TimestampMs = timestamp, Split = source.Split
                    };
                    Dataset.Samples.Add(sample); SaveSampleLocked(sample); return sample;
                }
            }
            return null;
        }
    }

    bool TryCandidateTimestamp(VideoSource source, long spacingMs, out long timestamp)
    {
        long start = 30_000, end = source.DurationMs - 30_000;
        List<(long Start, long End)> available = [];
        long cursor = start;
        foreach (long existing in Dataset.Samples.Where(value => value.SourceId == source.Id)
                     .Select(value => value.TimestampMs).Order())
        {
            long before = existing - spacingMs - 1;
            if (cursor <= before && cursor <= end) available.Add((cursor, Math.Min(before, end)));
            cursor = Math.Max(cursor, existing + spacingMs + 1);
            if (cursor > end) break;
        }
        if (cursor <= end) available.Add((cursor, end));

        while (available.Count > 0)
        {
            int index = random.Next(available.Count);
            (long windowStart, long windowEnd) = available[index];
            available.RemoveAt(index);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                long candidate = FrameTimestamp(random.NextInt64(windowStart, windowEnd + 1),
                    source.FramesPerSecond);
                if (candidate < start || candidate > end || Dataset.Samples.Any(value =>
                        value.SourceId == source.Id &&
                        Math.Abs(value.TimestampMs - candidate) <= spacingMs)) continue;
                timestamp = candidate; return true;
            }
        }
        timestamp = 0; return false;
    }

    internal static bool IsSourceInFolder(string source, string folder)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(source));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    bool SampleIsInActiveFolder(TrainingSample sample)
    {
        if (Dataset.ActiveSourceFolder == null) return true;
        VideoSource? source = Dataset.Sources.FirstOrDefault(value => value.Id == sample.SourceId);
        return source != null && IsSourceInFolder(source.Path, Dataset.ActiveSourceFolder);
    }

    bool SampleMeetsResolution(TrainingSample sample, int minimumResolution)
    {
        if (minimumResolution <= 0) return true;
        int width = sample.Width, height = sample.Height;
        if (width <= 0 || height <= 0)
        {
            VideoSource? source = Dataset.Sources.FirstOrDefault(value => value.Id == sample.SourceId);
            width = source?.Width ?? 0; height = source?.Height ?? 0;
        }
        return Math.Min(width, height) >= minimumResolution;
    }

    internal void SetActiveSourceFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        lock (gate)
        {
            if (!Dataset.SourceFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("That folder has not been indexed.");
            Dataset.ActiveSourceFolder = folder; SaveSourcesLocked();
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
            SaveSampleLocked(sample);
        }
    }

    internal async Task<IReadOnlyList<TrainingSample>> CreateBurstAsync(TrainingSample anchor,
        CancellationToken token, bool forceNew = false)
    {
        if (!forceNew && anchor.BurstId is string existingId)
        {
            TrainingSample[] existing = Dataset.Samples.Where(value => value.BurstId == existingId)
                .OrderBy(value => value.BurstIndex).ToArray();
            if (existing.Length is 5 or 9) return existing;
        }
        VideoSource source = Source(anchor.SourceId);
        string burstId = forceNew ? Guid.NewGuid().ToString("N") :
            anchor.BurstId ?? Guid.NewGuid().ToString("N");
        long[] offsets = BurstOffsetsMs();
        anchor.BurstId = burstId; anchor.BurstIndex = 2;
        List<TrainingSample> result = [anchor];
        for (int index = 0; index < offsets.Length; index++)
        {
            long offset = offsets[index];
            if (offset == 0) continue;
            long timestamp = FrameTimestamp(Math.Clamp(anchor.TimestampMs + offset,
                30_000, source.DurationMs - 30_000), source.FramesPerSecond);
            TrainingSample sample = new()
            {
                SourceId = source.Id, TimestampMs = timestamp, Split = source.Split,
                BurstId = burstId, BurstIndex = index, FeedbackPriority = anchor.FeedbackPriority
            };
            lock (gate) { Dataset.Samples.Add(sample); SaveSampleLocked(sample); }
            await ExtractDraftFrameAsync(sample, token); result.Add(sample);
        }
        lock (gate) SaveSampleLocked(anchor);
        return result.OrderBy(value => value.BurstIndex).ToArray();
    }

    internal static long[] BurstOffsetsMs() => [-3_000, -1_000, 0, 1_000, 3_000];

    internal void MarkFeedbackPriority(TrainingSample sample)
    {
        lock (gate)
        {
            sample.FeedbackPriority = true; SaveSampleLocked(sample);
        }
    }

    internal void Accept(TrainingSample sample, int[] objectIds,
        IReadOnlyList<AnnotationObject> objects, string provenance)
    {
        if (sample.FramePath == null) throw new InvalidOperationException("The draft frame is missing.");
        if (objectIds.Length != sample.Width * sample.Height || objectIds.Any(value => value is < 0 or > 65535))
            throw new InvalidDataException("The instance mask dimensions or object IDs are invalid.");
        bool positive = objectIds.Any(value => value > 0);
        string relativeFolder = Path.Combine("samples", sample.SourceId, sample.Id);
        string folder = Resolve(relativeFolder);
        string temporary = folder + ".accepting-" + Guid.NewGuid().ToString("N");
        string sourceFrame = Resolve(sample.FramePath);
        string frame = Path.Combine(temporary, "frame.png");
        string editable = Path.Combine(temporary, "instance-mask.i32.gz");
        string instance = Path.Combine(temporary, "instance-mask.png");
        string prop = Path.Combine(temporary, "prop-mask.png");
        string annotation = Path.Combine(temporary, "annotation.json");
        try
        {
            Directory.CreateDirectory(temporary); File.Copy(sourceFrame, frame, true);
            byte[] binary = objectIds.Select(value => value > 0 ? (byte)255 : (byte)0).ToArray();
            PngMaskWriter.SaveGray8(prop, sample.Width, sample.Height, binary);
            if (positive)
            {
                WriteEditableMask(editable, objectIds);
                PngMaskWriter.SaveGray16(instance, sample.Width, sample.Height, objectIds);
            }
            WriteJsonAtomic(annotation, new SampleAnnotation
            {
                Objects = objects.Where(value => objectIds.Contains(value.Id)).ToList(),
                Provenance = provenance
            });
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            if (Directory.Exists(folder))
            {
                if (Directory.EnumerateFileSystemEntries(folder).Any())
                    throw new IOException("The accepted sample folder already exists and is not empty.");
                Directory.Delete(folder);
            }
            Directory.Move(temporary, folder);
        }
        catch
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            throw;
        }
        lock (gate)
        {
            sample.Decision = positive ? "positive" : "negative";
            sample.ObjectCount = objects.Count(value => objectIds.Contains(value.Id));
            sample.FramePath = Relative(Path.Combine(folder, "frame.png"));
            sample.EditableMaskPath = positive
                ? Relative(Path.Combine(folder, "instance-mask.i32.gz")) : null;
            sample.InstanceMaskPath = positive
                ? Relative(Path.Combine(folder, "instance-mask.png")) : null;
            sample.PropMaskPath = Relative(Path.Combine(folder, "prop-mask.png"));
            sample.AnnotationPath = Relative(Path.Combine(folder, "annotation.json"));
            sample.ReviewedUtc = DateTime.UtcNow;
            sample.DerivationStatus = "pending"; SaveSampleLocked(sample);
        }
        TryDeleteDraft(sample.Id);
    }

    internal void SaveDraftAnnotation(TrainingSample sample, int[] objectIds,
        IReadOnlyList<AnnotationObject> objects, string provenance)
    {
        lock (gate) if (sample.Decision != "draft") return;
        string folder = Path.Combine("drafts", sample.Id);
        string editable = Resolve(Path.Combine(folder, "instance-mask.i32.gz"));
        string instance = Resolve(Path.Combine(folder, "instance-mask.png"));
        string annotation = Resolve(Path.Combine(folder, "annotation.json"));
        string suffix = ".saving-" + Guid.NewGuid().ToString("N");
        string editableTemporary = editable + suffix, instanceTemporary = instance + suffix;
        try
        {
            WriteEditableMask(editableTemporary, objectIds);
            PngMaskWriter.SaveGray16(instanceTemporary, sample.Width, sample.Height, objectIds);
            File.Move(editableTemporary, editable, true); File.Move(instanceTemporary, instance, true);
            WriteJsonAtomic(annotation, new SampleAnnotation
            {
                Objects = objects.Where(value => objectIds.Contains(value.Id)).ToList(),
                Provenance = provenance
            });
        }
        finally
        {
            try { File.Delete(editableTemporary); } catch { }
            try { File.Delete(instanceTemporary); } catch { }
        }
        lock (gate)
        {
            if (sample.Decision != "draft")
            {
                TryDeleteDraft(sample.Id); return;
            }
            string editablePath = Relative(editable), instancePath = Relative(instance);
            string annotationPath = Relative(annotation);
            int objectCount = objects.Count(value => objectIds.Contains(value.Id));
            bool ledgerChanged = sample.EditableMaskPath != editablePath ||
                sample.InstanceMaskPath != instancePath || sample.AnnotationPath != annotationPath ||
                sample.ObjectCount != objectCount;
            sample.EditableMaskPath = editablePath; sample.InstanceMaskPath = instancePath;
            sample.AnnotationPath = annotationPath; sample.ObjectCount = objectCount;
            if (ledgerChanged) SaveSampleLocked(sample);
        }
    }

    internal (int[]? Ids, AnnotationObject[] Objects, string Provenance) LoadDraftAnnotation(
        TrainingSample sample)
    {
        if (sample.EditableMaskPath == null || sample.AnnotationPath == null)
            return (sample.Width > 0 && sample.Height > 0 ? new int[sample.Width * sample.Height] : null,
                [], sample.ObjectCount == 0 ? "confirmed-negative" : "manual");
        string editable = Resolve(sample.EditableMaskPath), annotation = Resolve(sample.AnnotationPath);
        if (!File.Exists(editable) || !File.Exists(annotation))
            return (sample.Width > 0 && sample.Height > 0 ? new int[sample.Width * sample.Height] : null,
                [], sample.ObjectCount == 0 ? "confirmed-negative" : "manual");
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
            sample.FramePath = null; SaveSampleLocked(sample);
        }
        _ = Task.Run(() => TryDeleteDraft(sample.Id));
    }

    internal TrainingSample? MostRecentDecision() => Dataset.Samples
        .Where(value => value.Decision is "positive" or "negative" or "rejected" &&
            value.ReviewedUtc != null)
        .OrderByDescending(value => value.ReviewedUtc).FirstOrDefault();

    internal IReadOnlyList<TrainingSample> AcceptedHistory()
    {
        lock (gate)
            return Dataset.Samples.Where(value => value.Accepted)
                .OrderByDescending(value => value.ReviewedUtc ?? value.PresentedUtc)
                .ThenByDescending(value => value.PresentedUtc).ToArray();
    }

    internal void DeleteAcceptedSample(TrainingSample sample)
    {
        if (!sample.Accepted || sample.FramePath == null)
            throw new InvalidOperationException("Only an accepted sample can be deleted from training data.");
        string acceptedFolder = Path.GetDirectoryName(Resolve(sample.FramePath))!;
        string expectedFolder = Path.GetFullPath(Path.Combine(Root, "samples", sample.SourceId, sample.Id));
        if (!string.Equals(acceptedFolder, expectedFolder, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(acceptedFolder))
            throw new InvalidDataException("The accepted sample folder is missing or unsafe.");
        string stagedFolder = acceptedFolder + ".deleting-" + Guid.NewGuid().ToString("N");
        Directory.Move(acceptedFolder, stagedFolder);

        string decision = sample.Decision;
        int objectCount = sample.ObjectCount;
        string? framePath = sample.FramePath, annotationPath = sample.AnnotationPath;
        string? editableMaskPath = sample.EditableMaskPath, instanceMaskPath = sample.InstanceMaskPath;
        string? propMaskPath = sample.PropMaskPath, personMaskPath = sample.RvmPersonMaskPath;
        string? desiredPath = sample.DesiredForegroundPath, derivationStatus = sample.DerivationStatus;
        string? derivationError = sample.DerivationError, rvmRevision = sample.RvmRevision;
        double? rvmThreshold = sample.RvmThreshold;
        DateTime? reviewedUtc = sample.ReviewedUtc;
        try
        {
            lock (gate)
            {
                sample.Decision = "rejected"; sample.ObjectCount = 0;
                sample.FramePath = null; sample.AnnotationPath = null; sample.EditableMaskPath = null;
                sample.InstanceMaskPath = null; sample.PropMaskPath = null; sample.RvmPersonMaskPath = null;
                sample.DesiredForegroundPath = null; sample.DerivationStatus = null;
                sample.DerivationError = null; sample.RvmRevision = null; sample.RvmThreshold = null;
                sample.ReviewedUtc = DateTime.UtcNow; SaveSampleLocked(sample);
            }
        }
        catch
        {
            lock (gate)
            {
                sample.Decision = decision; sample.ObjectCount = objectCount;
                sample.FramePath = framePath; sample.AnnotationPath = annotationPath;
                sample.EditableMaskPath = editableMaskPath; sample.InstanceMaskPath = instanceMaskPath;
                sample.PropMaskPath = propMaskPath; sample.RvmPersonMaskPath = personMaskPath;
                sample.DesiredForegroundPath = desiredPath; sample.DerivationStatus = derivationStatus;
                sample.DerivationError = derivationError; sample.RvmRevision = rvmRevision;
                sample.RvmThreshold = rvmThreshold; sample.ReviewedUtc = reviewedUtc;
            }
            Directory.Move(stagedFolder, acceptedFolder);
            throw;
        }
        try { Directory.Delete(stagedFolder, true); } catch { }
    }

    internal void UndoDecision(TrainingSample sample)
    {
        if (sample.Decision is not ("positive" or "negative"))
            throw new InvalidOperationException("Only accepted positive/negative decisions can currently be restored.");
        if (sample.FramePath == null) throw new InvalidOperationException("The accepted frame is missing.");
        string acceptedFolder = Path.GetDirectoryName(Resolve(sample.FramePath))!;
        string draftFolder = Path.Combine(Root, "drafts", sample.Id);
        if (!Directory.Exists(acceptedFolder)) throw new DirectoryNotFoundException(acceptedFolder);
        if (Directory.Exists(draftFolder))
            throw new IOException("The draft restore folder already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(draftFolder)!);
        Directory.Move(acceptedFolder, draftFolder);
        lock (gate)
        {
            sample.Decision = "draft"; sample.ReviewedUtc = null;
            sample.FramePath = Relative(Path.Combine(draftFolder, "frame.png"));
            sample.EditableMaskPath = Relative(Path.Combine(draftFolder, "instance-mask.i32.gz"));
            sample.InstanceMaskPath = Relative(Path.Combine(draftFolder, "instance-mask.png"));
            sample.AnnotationPath = Relative(Path.Combine(draftFolder, "annotation.json"));
            sample.PropMaskPath = null; sample.RvmPersonMaskPath = null;
            sample.DesiredForegroundPath = null; sample.DerivationStatus = "pending";
            sample.DerivationError = null; sample.RvmRevision = null; sample.RvmThreshold = 0;
            SaveSampleLocked(sample);
        }
    }

    internal void SetDerivation(TrainingSample sample, string status, string? personMask,
        string? desiredMask, string? error, string? rvmRevision, double rvmThreshold)
    {
        lock (gate)
        {
            sample.DerivationStatus = status; sample.RvmPersonMaskPath = personMask;
            sample.DesiredForegroundPath = desiredMask; sample.DerivationError = error;
            sample.RvmRevision = rvmRevision; sample.RvmThreshold = rvmThreshold;
            SaveSampleLocked(sample);
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
    internal void Save()
    {
        lock (gate)
        {
            SaveSourcesLocked();
            foreach (TrainingSample sample in Dataset.Samples) SaveSampleLocked(sample);
        }
    }

    internal void SaveSample(TrainingSample sample) { lock (gate) SaveSampleLocked(sample); }
    internal string RecordPath(string sampleId) => SampleRecordPath(sampleId);

    void SaveSourcesLocked()
    {
        Dataset.UpdatedUtc = DateTime.UtcNow; WriteJsonAtomic(ManifestPath, Dataset);
    }

    void SaveSampleLocked(TrainingSample sample) => WriteJsonAtomic(SampleRecordPath(sample.Id),
        new TrainingSampleRecord
    {
        DatasetId = Dataset.DatasetId, UpdatedUtc = DateTime.UtcNow, Sample = sample
    });

    string SampleRecordPath(string sampleId)
    {
        if (sampleId.Length < 2 || sampleId.Any(value => !char.IsAsciiLetterOrDigit(value)))
            throw new InvalidDataException("A sample has an unsafe ID.");
        return Path.Combine(RecordsRoot, sampleId[..2], sampleId + ".json");
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
        using CancellationTokenRegistration cancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
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
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(candidate));
                if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) ||
                    schema.GetInt32() != 1) continue;
                File.Move(candidate, path); return;
            }
            catch { }
        }
    }

    static List<TrainingSample> ReadLegacySamples(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("samples", out JsonElement samples)
                ? JsonSerializer.Deserialize<List<TrainingSample>>(samples.GetRawText(), JsonOptions) ?? []
                : [];
        }
        catch { return []; }
    }

    IReadOnlyList<TrainingSample> ReadSampleRecords()
    {
        if (!Directory.Exists(RecordsRoot)) return [];
        List<TrainingSample> result = [];
        foreach (string path in Directory.EnumerateFiles(RecordsRoot, "*.json",
                     SearchOption.AllDirectories))
        {
            TrainingSampleRecord record = JsonSerializer.Deserialize<TrainingSampleRecord>(
                File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Invalid sample record: {path}");
            if (record.SchemaVersion != 1 || record.DatasetId != Dataset.DatasetId ||
                !Path.GetFileNameWithoutExtension(path).Equals(record.Sample.Id,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Sample record does not belong to this dataset: {path}");
            result.Add(record.Sample);
        }
        return result;
    }

    void RecoverRecordManifests()
    {
        if (!Directory.Exists(RecordsRoot)) return;
        foreach (string candidate in Directory.EnumerateFiles(RecordsRoot, "*.json.tmp-*",
                     SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            int marker = candidate.LastIndexOf(".tmp-", StringComparison.Ordinal);
            if (marker < 0) continue;
            string destination = candidate[..marker];
            if (File.Exists(destination)) continue;
            try
            {
                TrainingSampleRecord? record = JsonSerializer.Deserialize<TrainingSampleRecord>(
                    File.ReadAllText(candidate), JsonOptions);
                if (record?.SchemaVersion != 1) continue;
                File.Move(candidate, destination);
            }
            catch { }
        }
    }

    void ArchiveLegacySampleLedger()
    {
        if (!File.Exists(LegacySamplesManifestPath)) return;
        string destination = LegacySamplesManifestPath + ".migrated";
        if (File.Exists(destination)) destination += "-" + Guid.NewGuid().ToString("N");
        File.Move(LegacySamplesManifestPath, destination);
    }
}
