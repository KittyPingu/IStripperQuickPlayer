using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal enum CustomShowQueueOperation { New, Append, Reprocess }
internal enum CustomShowQueueStatus { Pending, Running, Completed, Failed, NeedsAttention }

internal sealed class CustomShowQueueDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<CustomShowQueueJob> Jobs { get; set; } = [];
}

internal sealed class CustomShowQueueJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public CustomShowQueueOperation Operation { get; set; }
    public CustomShowQueueStatus Status { get; set; } = CustomShowQueueStatus.Pending;
    public CustomShowManifest Manifest { get; set; } = new();
    public CustomPerformerProfile Performer { get; set; } = new();
    public CustomShowClip[] Clips { get; set; } = [];
    public string SourcePath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public string? TargetShowId { get; set; }
    public string? TargetManifestSha256 { get; set; }
    public string? CoverAsset { get; set; }
    public Dictionary<string, string> InitialMaskAssets { get; set; } = [];
    public Dictionary<string, long> InitialMaskFrameMs { get; set; } = [];
    public bool KeepExistingMasks { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public double Percent { get; set; }
    public string Message { get; set; } = "Pending";
    public string? Error { get; set; }
    public string? PublishedShowId { get; set; }
}

internal sealed class CustomShowQueueStore
{
    readonly CustomShowStore shows;
    internal string Root => Path.Combine(shows.Root, "queue");
    internal string FilePath => Path.Combine(Root, "queue.json");
    internal string Assets(string id) => Path.Combine(Root, "jobs", id);
    internal string Work(string id) => Path.Combine(Root, "work", id);

    internal CustomShowQueueStore(CustomShowStore shows) => this.shows = shows;

    internal CustomShowQueueDocument Load()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(FilePath)) return new();
        CustomShowQueueDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CustomShowQueueDocument>(
                File.ReadAllText(FilePath), CustomShowStore.JsonOptions) ?? new();
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            string invalid = FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(FilePath, invalid, true); } catch { }
            return new();
        }
        if (document.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported custom-show queue version.");
        bool changed = false;
        foreach (CustomShowQueueJob job in document.Jobs.Where(value =>
            value.Status == CustomShowQueueStatus.Running))
        {
            job.Status = CustomShowQueueStatus.Pending;
            job.Percent = 0;
            job.Message = "Interrupted; ready to restart";
            job.StartedUtc = null;
            TryDelete(Work(job.Id));
            changed = true;
        }
        if (changed) Save(document);
        return document;
    }

    internal void Save(CustomShowQueueDocument document) =>
        CustomShowStore.WriteJsonAtomic(FilePath, document);

    internal void DeleteAssets(string id)
    {
        TryDelete(Assets(id));
        TryDelete(Work(id));
    }

    internal static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    internal static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed class CustomShowQueueAttentionException(string message) : Exception(message);

internal sealed class CustomShowQueueManager : IDisposable
{
    readonly object gate = new();
    readonly CustomShowStore shows;
    readonly CustomShowConfiguration configuration;
    readonly CustomShowQueueStore storage;
    readonly Action published;
    readonly CustomShowQueueDocument document;
    CancellationTokenSource? cancellation;
    Task? loop;
    bool pauseAfterCurrent;
    DateTime lastSavedUtc;
    internal event EventHandler? Changed;

    internal CustomShowQueueManager(CustomShowStore shows,
        CustomShowConfiguration configuration, Action published)
    {
        this.shows = shows;
        this.configuration = configuration;
        this.published = published;
        storage = new(shows);
        document = storage.Load();
    }

    internal IReadOnlyList<CustomShowQueueJob> Jobs
    {
        get { lock (gate) return document.Jobs.Select(Clone).ToArray(); }
    }

    internal bool IsRunning { get { lock (gate) return loop is { IsCompleted: false }; } }
    internal bool HasActiveJob { get { lock (gate) return document.Jobs.Any(
        job => job.Status == CustomShowQueueStatus.Running); } }

    internal string AddOrUpdate(CustomShowQueueJob job, string? existingId,
        string? coverSource, IReadOnlyDictionary<string, string> masks)
    {
        lock (gate)
        {
            if (job.TargetShowId != null && document.Jobs.Any(value =>
                value.Id != existingId &&
                value.Status is CustomShowQueueStatus.Pending or CustomShowQueueStatus.Running &&
                string.Equals(value.TargetShowId, job.TargetShowId,
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "That show already has a pending or running queue job.");
            if (existingId != null)
            {
                int index = document.Jobs.FindIndex(value => value.Id == existingId);
                if (index < 0) throw new InvalidOperationException("The queue job no longer exists.");
                if (document.Jobs[index].Status == CustomShowQueueStatus.Running)
                    throw new InvalidOperationException("A running job cannot be edited.");
                job.Id = existingId;
                job.CreatedUtc = document.Jobs[index].CreatedUtc;
                document.Jobs[index] = job;
            }
            else document.Jobs.Add(job);
            string assets = storage.Assets(job.Id);
            Directory.CreateDirectory(assets);
            if (!string.IsNullOrWhiteSpace(coverSource))
            {
                string destination = Path.Combine(assets, "cover" + Path.GetExtension(coverSource));
                if (!string.Equals(Path.GetFullPath(coverSource), Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
                    File.Copy(coverSource, destination, true);
                job.CoverAsset = Path.GetRelativePath(assets, destination).Replace('\\', '/');
            }
            if (masks.Count > 0) job.InitialMaskAssets.Clear();
            foreach ((string clipId, string source) in masks)
            {
                string destination = Path.Combine(assets, "initial-masks", clipId + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
                    File.Copy(source, destination, true);
                job.InitialMaskAssets[clipId] = Path.GetRelativePath(assets, destination)
                    .Replace('\\', '/');
            }
            SaveLocked();
        }
        OnChanged();
        return job.Id;
    }

    internal CustomShowQueueJob? Find(string id)
    {
        lock (gate) return document.Jobs.FirstOrDefault(value => value.Id == id) is { } job
            ? Clone(job) : null;
    }

    internal string AssetFolder(string id) => storage.Assets(id);
    internal string PublishedShowFolder(string id) => Path.Combine(shows.ShowsFolder, id);
    internal string ProcessingLog(string id) => Path.Combine(storage.Assets(id),
        "processing.log");

    internal void Run()
    {
        lock (gate)
        {
            if (loop is { IsCompleted: false }) return;
            pauseAfterCurrent = false;
            cancellation = new();
            loop = Task.Run(() => RunLoop(cancellation.Token));
        }
        OnChanged();
    }

    async Task RunLoop(CancellationToken token)
    {
        while (true)
        {
            CustomShowQueueJob? job;
            lock (gate)
            {
                if (pauseAfterCurrent) break;
                job = document.Jobs.FirstOrDefault(value =>
                    value.Status == CustomShowQueueStatus.Pending);
                if (job == null) break;
                job.Status = CustomShowQueueStatus.Running;
                job.StartedUtc = DateTime.UtcNow;
                job.CompletedUtc = null;
                job.Percent = 0;
                job.Message = "Starting";
                job.Error = null;
                SaveLocked();
            }
            OnChanged();
            try
            {
                Progress<CustomShowProgress> progress = new(value => UpdateProgress(
                    job.Id, value.Percent, string.IsNullOrWhiteSpace(value.Message)
                        ? value.Stage : value.Message));
                string publishedId = await CustomShowJobRunner.RunAsync(job, shows,
                    configuration, storage, progress, token);
                lock (gate)
                {
                    job.Status = CustomShowQueueStatus.Completed;
                    job.Percent = 100;
                    job.Message = "Completed";
                    job.CompletedUtc = DateTime.UtcNow;
                    job.PublishedShowId = publishedId;
                    SaveLocked();
                }
                try { published(); } catch (Exception error)
                { Debug.WriteLine("Could not refresh published custom show: " + error); }
            }
            catch (OperationCanceledException)
            {
                lock (gate)
                {
                    job.Status = CustomShowQueueStatus.Pending;
                    job.Percent = 0;
                    job.Message = "Stopped; ready to restart";
                    job.StartedUtc = null;
                    SaveLocked();
                }
                CustomShowQueueStore.TryDelete(storage.Work(job.Id));
                OnChanged();
                break;
            }
            catch (CustomShowQueueAttentionException error)
            {
                FinishError(job, CustomShowQueueStatus.NeedsAttention, error);
            }
            catch (Exception error)
            {
                FinishError(job, CustomShowQueueStatus.Failed, error);
            }
            OnChanged();
        }
        lock (gate) { cancellation?.Dispose(); cancellation = null; }
        OnChanged();
    }

    void FinishError(CustomShowQueueJob job, CustomShowQueueStatus status,
        Exception error)
    {
        CustomShowJobRunner.PreserveLog(storage.Work(job.Id), storage.Assets(job.Id));
        CustomShowQueueStore.TryDelete(storage.Work(job.Id));
        lock (gate)
        {
            job.Status = status;
            job.Message = status == CustomShowQueueStatus.NeedsAttention
                ? "Needs attention" : "Failed";
            job.Error = error.Message;
            job.CompletedUtc = DateTime.UtcNow;
            SaveLocked();
        }
    }

    void UpdateProgress(string id, double percent, string message)
    {
        lock (gate)
        {
            CustomShowQueueJob? job = document.Jobs.FirstOrDefault(value => value.Id == id);
            if (job == null || job.Status != CustomShowQueueStatus.Running) return;
            job.Percent = Math.Clamp(percent, 0, 100);
            job.Message = message;
            if (DateTime.UtcNow - lastSavedUtc >= TimeSpan.FromSeconds(1)) SaveLocked();
        }
        OnChanged();
    }

    internal void PauseAfterCurrent()
    {
        lock (gate) pauseAfterCurrent = true;
        OnChanged();
    }

    internal async Task CancelAndPauseAsync()
    {
        Task? running;
        lock (gate) { pauseAfterCurrent = true; cancellation?.Cancel(); running = loop; }
        if (running != null) await running;
    }

    internal async Task StopAsync() => await CancelAndPauseAsync();

    internal void CancelForExit()
    {
        lock (gate)
        {
            pauseAfterCurrent = true;
            foreach (CustomShowQueueJob job in document.Jobs.Where(value =>
                value.Status == CustomShowQueueStatus.Running))
            {
                job.Status = CustomShowQueueStatus.Pending;
                job.Percent = 0;
                job.Message = "QuickPlayer closed; ready to restart";
                job.StartedUtc = null;
            }
            SaveLocked();
            cancellation?.Cancel();
        }
        OnChanged();
    }

    internal void Retry(string id)
    {
        lock (gate)
        {
            CustomShowQueueJob job = document.Jobs.First(value => value.Id == id);
            if (job.Status is not (CustomShowQueueStatus.Failed or
                CustomShowQueueStatus.NeedsAttention)) return;
            try
            {
                CustomShowJobRunner.Validate(job, shows, configuration, storage);
                job.Status = CustomShowQueueStatus.Pending;
                job.Percent = 0; job.Message = "Pending"; job.Error = null;
                job.CompletedUtc = null;
            }
            catch (CustomShowQueueAttentionException error)
            {
                job.Status = CustomShowQueueStatus.NeedsAttention;
                job.Message = "Needs attention";
                job.Error = error.Message;
            }
            SaveLocked();
        }
        OnChanged();
    }

    internal void Delete(string id)
    {
        lock (gate)
        {
            int index = document.Jobs.FindIndex(value => value.Id == id);
            if (index < 0 || document.Jobs[index].Status == CustomShowQueueStatus.Running) return;
            document.Jobs.RemoveAt(index);
            storage.DeleteAssets(id);
            SaveLocked();
        }
        OnChanged();
    }

    internal void Move(string id, int direction)
    {
        lock (gate)
        {
            int index = document.Jobs.FindIndex(value => value.Id == id);
            int other = index + direction;
            if (index < 0 || other < 0 || other >= document.Jobs.Count ||
                document.Jobs[index].Status is CustomShowQueueStatus.Running or
                    CustomShowQueueStatus.Completed ||
                document.Jobs[other].Status is CustomShowQueueStatus.Running or
                    CustomShowQueueStatus.Completed) return;
            (document.Jobs[index], document.Jobs[other]) =
                (document.Jobs[other], document.Jobs[index]);
            SaveLocked();
        }
        OnChanged();
    }

    void SaveLocked() { storage.Save(document); lastSavedUtc = DateTime.UtcNow; }
    void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    static CustomShowQueueJob Clone(CustomShowQueueJob value) =>
        JsonSerializer.Deserialize<CustomShowQueueJob>(JsonSerializer.Serialize(value,
            CustomShowStore.JsonOptions), CustomShowStore.JsonOptions)!;

    public void Dispose()
    {
        cancellation?.Cancel();
    }
}

internal static class CustomShowJobRunner
{
    internal static async Task<string> RunAsync(CustomShowQueueJob job,
        CustomShowStore store, CustomShowConfiguration configuration,
        CustomShowQueueStore queue, IProgress<CustomShowProgress> progress,
        CancellationToken token)
    {
        Validate(job, store, configuration, queue);
        string staging = queue.Work(job.Id);
        CustomShowQueueStore.TryDelete(staging);
        Directory.CreateDirectory(staging);
        CustomShowManifest show;
        CustomShowClip[] existing = [];
        long offset = 0;
        if (job.Operation == CustomShowQueueOperation.New)
            show = Clone(job.Manifest);
        else
        {
            show = store.LoadManifest(job.TargetShowId!);
            CopyDirectory(Path.Combine(store.ShowsFolder, show.Id), staging);
            if (job.Operation == CustomShowQueueOperation.Reprocess &&
                !job.KeepExistingMasks)
                CustomShowQueueStore.TryDelete(Path.Combine(staging, "masks"));
            existing = show.Clips;
            offset = job.Operation == CustomShowQueueOperation.Append
                ? show.Media.DurationMs : 0;
            ApplyMetadata(show, job.Manifest);
        }
        CustomShowClip[] clips = Clone(job.Clips);
        string log = Path.Combine(staging, "processing.log");
        Dictionary<string, string> generatedMasks = [];
        Dictionary<string, string> initialMasks = [];
        Dictionary<string, string> retainedMasks = [];
        if (job.Operation == CustomShowQueueOperation.Reprocess && job.KeepExistingMasks)
            LoadRetainedMasks(staging, clips, initialMasks, retainedMasks);
        CustomShowProcessResult result = await ProcessClips(job, clips, staging,
            configuration, queue, log, initialMasks, retainedMasks,
            generatedMasks, progress, token);
        if (generatedMasks.Count > 0 && job.Operation != CustomShowQueueOperation.Append)
        {
            FileInfo sourceInfo = new(job.SourcePath);
            CustomShowStore.WriteJsonAtomic(Path.Combine(staging, "masks",
                "retained-masks.json"), new CustomRetainedMasksManifest
                {
                    SourceLength = sourceInfo.Length,
                    SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
                    Clips = clips.Where(clip => clip.Included &&
                        generatedMasks.ContainsKey(clip.Id)).Select(clip =>
                        new CustomRetainedMaskClip
                        {
                            ClipId = clip.Id, StartMs = clip.StartMs,
                            EndMs = clip.EndMs, HasTrackedMasks = true,
                            TrackedMaskArchive = "tracked-masks.iqpmask"
                        }).ToArray()
                });
        }
        foreach (CustomShowClip clip in clips.Where(value => value.Included))
            clip.AlphaThreshold = 25;
        long duration = clips.Max(value => value.EndMs);
        if (job.Operation == CustomShowQueueOperation.Append)
        {
            show.Clips = [.. existing, .. OffsetClips(clips, offset,
                new CustomShowSource { Mode = "reference", Path = job.SourcePath })];
            show.Media.DurationMs = checked(offset + duration);
            show.ClipDetection = null;
        }
        else
        {
            show.Source = new() { Mode = "reference", Path = job.SourcePath };
            show.Clips = clips;
            show.Media.Width = result.Width;
            show.Media.Height = result.Height;
            show.Media.FrameRate = result.FrameRate;
            show.Media.DurationMs = duration;
        }
        show.Processing = Processing(job, result, show.Clips, offset);
        await SaveCover(job, show, staging, queue, token);
        store.SavePerformer(job.Performer);
        if (job.Operation == CustomShowQueueOperation.New) store.Publish(staging, show);
        else store.Replace(staging, show);
        return show.Id;
    }

    internal static void Validate(CustomShowQueueJob job, CustomShowStore store,
        CustomShowConfiguration configuration, CustomShowQueueStore queue)
    {
        if (job.Manifest.Processing?.Algorithm is not
            ("quality" or "fast" or "rvm-matanyone2" or
             "rvm-vitmatte-s" or "matanyone2"))
            throw new CustomShowQueueAttentionException(
                "This processing algorithm cannot run unattended.");
        bool installed = job.Manifest.Processing.Algorithm switch
        {
            "rvm-matanyone2" => CustomShowProcessor.IsRvmMatAnyone2Installed(configuration),
            "rvm-vitmatte-s" => CustomShowProcessor.IsRvmViTMatteSmallInstalled(configuration),
            "matanyone2" => CustomShowProcessor.IsMatAnyone2Installed(configuration),
            _ => File.Exists(CustomShowProcessor.WorkerPath)
        };
        if (!installed)
            throw new CustomShowQueueAttentionException(
                "The selected processing tools are not currently installed.");
        if (!File.Exists(job.SourcePath))
            throw new CustomShowQueueAttentionException("The source video no longer exists.");
        if (job.Clips.Length == 0 || !job.Clips.Any(clip => clip.Included) ||
            job.Clips.Any(clip => clip.StartMs < 0 || clip.EndMs <= clip.StartMs))
            throw new CustomShowQueueAttentionException(
                "The queued clip layout is missing or invalid. Edit the job to review it.");
        FileInfo source = new(job.SourcePath);
        if (source.Length != job.SourceLength ||
            source.LastWriteTimeUtc.Ticks != job.SourceLastWriteUtcTicks)
            throw new CustomShowQueueAttentionException(
                "The source video changed after this job was queued.");
        if (job.Operation != CustomShowQueueOperation.New)
        {
            string manifest = Path.Combine(store.ShowsFolder, job.TargetShowId!, "show.json");
            if (!File.Exists(manifest))
                throw new CustomShowQueueAttentionException("The target show no longer exists.");
            if (!string.Equals(CustomShowQueueStore.HashFile(manifest),
                    job.TargetManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new CustomShowQueueAttentionException(
                    "The target show changed after this job was queued. Edit the job to review it.");
        }
        if (job.CoverAsset != null && !File.Exists(Path.Combine(queue.Assets(job.Id),
                job.CoverAsset.Replace('/', Path.DirectorySeparatorChar))))
            throw new CustomShowQueueAttentionException(
                "The queued custom cover is missing. Edit the job to select it again.");
        if (job.Manifest.Processing?.Algorithm == "matanyone2")
            foreach (CustomShowClip clip in job.Clips.Where(value => value.Included))
                if (!job.InitialMaskAssets.TryGetValue(clip.Id, out string? mask) ||
                    !File.Exists(Path.Combine(queue.Assets(job.Id),
                        mask.Replace('/', Path.DirectorySeparatorChar))))
                    throw new CustomShowQueueAttentionException(
                        "A MatAnyone initial mask is missing. Edit and mask the job again.");
    }

    static async Task<CustomShowProcessResult> ProcessClips(CustomShowQueueJob job,
        CustomShowClip[] clips, string staging, CustomShowConfiguration configuration,
        CustomShowQueueStore queue, string log,
        IReadOnlyDictionary<string, string> retainedInitialMasks,
        IReadOnlyDictionary<string, string> retainedTrackedMasks,
        Dictionary<string, string> generatedMasks,
        IProgress<CustomShowProgress> progress, CancellationToken token)
    {
        CustomShowProcessing options = job.Manifest.Processing!;
        CustomShowClip[] included = clips.Where(value => value.Included).ToArray();
        long total = included.Sum(value => Math.Max(1, value.EndMs - value.StartMs));
        if (options.Algorithm is "quality" or "fast")
        {
            CustomShowProcessJob[] jobs = included.Select(clip => new CustomShowProcessJob(
                Path.Combine(staging, "clips", clip.Id), clip.StartMs, clip.EndMs)).ToArray();
            CustomShowProcessResult first = await CustomShowProcessor.RunAsync(configuration,
                job.SourcePath, staging, options.Algorithm, null, null,
                options.MattingDetailPx, options.BatchSize, 0, null, log, false,
                progress, token, jobs);
            for (int i = 0; i < included.Length; i++)
            {
                CustomShowProcessResult media = i == 0 ? first : JsonSerializer.Deserialize<
                    CustomShowProcessResult>(await File.ReadAllTextAsync(Path.Combine(
                        jobs[i].Output, "result.json"), token), CustomShowStore.JsonOptions)!;
                SetMedia(included[i], media);
            }
            return first;
        }
        long completed = 0;
        CustomShowProcessResult? firstResult = null;
        for (int i = 0; i < included.Length; i++)
        {
            CustomShowClip clip = included[i];
            long before = completed, clipDuration = clip.EndMs - clip.StartMs;
            Progress<CustomShowProgress> aggregate = new(value => progress.Report(value with
            {
                Percent = CustomShowProcessingForm.AggregateClipPercent(before,
                    clipDuration, total, value.Percent),
                Message = $"Clip {i + 1}/{included.Length}: {value.Message}"
            }));
            string output = Path.Combine(staging, "clips", clip.Id);
            string? mask = job.InitialMaskAssets.TryGetValue(clip.Id, out string? relative)
                ? Path.Combine(queue.Assets(job.Id), relative.Replace('/',
                    Path.DirectorySeparatorChar))
                : retainedInitialMasks.GetValueOrDefault(clip.Id);
            string? tracked = retainedTrackedMasks.GetValueOrDefault(clip.Id);
            CustomShowProcessResult media = await CustomShowProcessor.RunAsync(configuration,
                job.SourcePath, output, options.Algorithm, mask, tracked,
                options.MattingDetailPx, options.BatchSize, clip.StartMs, clip.EndMs,
                log, i > 0, aggregate, token, maskFrameMs:
                    job.InitialMaskFrameMs.GetValueOrDefault(clip.Id, clip.StartMs),
                rvmInitializerAlphaThresholdPercent:
                    options.RvmInitializerAlphaThresholdPercent ?? 40);
            if (options.Algorithm == "rvm-vitmatte-s")
            {
                string masks = Path.Combine(output, ".rvm-masks");
                if (Directory.Exists(masks)) generatedMasks[clip.Id] = masks;
            }
            SetMedia(clip, media);
            completed += clipDuration;
            firstResult ??= media;
        }
        if (options.Algorithm == "rvm-vitmatte-s")
            foreach ((string clipId, string masks) in generatedMasks)
            {
                string folder = Path.Combine(staging, "masks", clipId);
                Directory.CreateDirectory(folder);
                CustomMaskArchive.Create(masks, Path.Combine(folder,
                    "tracked-masks.iqpmask"));
                Directory.Delete(masks, true);
            }
        return firstResult!;
    }

    static void LoadRetainedMasks(string staging, CustomShowClip[] clips,
        Dictionary<string, string> initialMasks,
        Dictionary<string, string> trackedMasks)
    {
        string root = Path.Combine(staging, "masks");
        CustomRetainedMasksManifest? manifest = CustomShowMaskDraft.Load<
            CustomRetainedMasksManifest>(Path.Combine(root, "retained-masks.json"));
        if (manifest == null) return;
        Dictionary<string, CustomShowClip> current = clips.ToDictionary(
            clip => clip.Id, StringComparer.OrdinalIgnoreCase);
        foreach (CustomRetainedMaskClip saved in manifest.Clips)
        {
            if (!current.TryGetValue(saved.ClipId, out CustomShowClip? clip) ||
                clip.StartMs != saved.StartMs || clip.EndMs != saved.EndMs) continue;
            string folder = Path.Combine(root, saved.ClipId);
            string initial = Path.Combine(folder, "initial-mask.png");
            if (saved.HasInitialMask && File.Exists(initial))
                initialMasks[clip.Id] = initial;
            string archive = Path.Combine(folder,
                saved.TrackedMaskArchive ?? "tracked-masks.iqpmask");
            if (saved.HasTrackedMasks && File.Exists(archive))
            {
                string expanded = Path.Combine(staging, ".mask-work", clip.Id);
                CustomMaskArchive.Extract(archive, expanded);
                trackedMasks[clip.Id] = expanded;
            }
        }
    }

    static void SetMedia(CustomShowClip clip, CustomShowProcessResult result)
    {
        string relative = Path.Combine("clips", clip.Id);
        clip.Media = new()
        {
            Foreground = Path.Combine(relative, "foreground.mp4").Replace('\\', '/'),
            Alpha = Path.Combine(relative, "alpha.mkv").Replace('\\', '/'),
            Width = result.Width, Height = result.Height,
            FrameRate = result.FrameRate, DurationMs = result.DurationMs
        };
    }

    static CustomShowProcessing Processing(CustomShowQueueJob job,
        CustomShowProcessResult result, CustomShowClip[] clips, long appendedOffset)
    {
        CustomShowProcessing value = Clone(job.Manifest.Processing!);
        value.AutoAcceptedAlphaThreshold = 25;
        value.ProcessedUtc = DateTime.UtcNow;
        value.QuickPlayerVersion = Application.ProductVersion;
        value.ResolvedExecutionMode = result.ExecutionMode;
        value.EffectiveBatchSize = result.EffectiveSequenceChunk;
        value.PipelineDepth = result.PipelineDepth;
        value.Encoder = result.Encoder;
        value.EncoderPreset = result.EncoderPreset;
        value.Clips = clips.Where(clip => clip.Included).Select(clip => new
            CustomClipProcessing { ClipId = clip.Id,
                InitialMaskFrameMs = value.Algorithm == "matanyone2" &&
                    job.InitialMaskFrameMs.TryGetValue(clip.Id, out long frame)
                    ? checked(frame + appendedOffset) : null })
            .ToArray();
        return value;
    }

    static async Task SaveCover(CustomShowQueueJob job, CustomShowManifest show,
        string staging, CustomShowQueueStore queue, CancellationToken token)
    {
        string destination = Path.Combine(staging, show.Media.Cover);
        if (job.CoverAsset != null)
        {
            ResizeCover(Path.Combine(queue.Assets(job.Id), job.CoverAsset.Replace('/',
                Path.DirectorySeparatorChar)), destination);
            show.Media.AutoGeneratedCover = false;
        }
        else if (job.Operation == CustomShowQueueOperation.Append && File.Exists(destination))
            return;
        else if (job.Operation == CustomShowQueueOperation.Reprocess &&
            !show.Media.AutoGeneratedCover && File.Exists(destination))
            return;
        else
        {
            await CustomShowProcessor.GenerateCoverAsync(job.SourcePath, destination,
                show.Media.DurationMs / 2, job.Performer.ModelName, show.Title,
                ColorTranslator.FromHtml(show.Media.CoverTitleColor), token);
            show.Media.AutoGeneratedCover = true;
        }
    }

    static void ResizeCover(string source, string destination)
    {
        using Image image = Image.FromFile(source);
        using Bitmap copy = new(600, 900);
        using (Graphics graphics = Graphics.FromImage(copy))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Max(600f / image.Width, 900f / image.Height);
            graphics.DrawImage(image, (600 - image.Width * scale) / 2,
                (900 - image.Height * scale) / 2, image.Width * scale,
                image.Height * scale);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        copy.Save(destination, ImageFormat.Jpeg);
    }

    static void ApplyMetadata(CustomShowManifest target, CustomShowManifest source)
    {
        target.PerformerId = source.PerformerId; target.Title = source.Title;
        target.Description = source.Description; target.Tags = source.Tags;
        target.ReleaseDate = source.ReleaseDate; target.ShowDate = source.ShowDate;
        target.AgeAtReleaseOverride = source.AgeAtReleaseOverride;
        target.Gender = source.Gender;
        target.Hotness = source.Hotness; target.PerformerCount = source.PerformerCount;
        target.ClipTypes = source.ClipTypes;
        target.Media.CoverTitleColor = source.Media.CoverTitleColor;
    }

    static CustomShowClip[] OffsetClips(IEnumerable<CustomShowClip> clips, long offset,
        CustomShowSource source) => clips.Select(clip =>
        {
            CustomShowClip result = Clone(clip);
            result.Source = Clone(source); result.SourceStartMs = clip.StartMs;
            result.SourceEndMs = clip.EndMs; result.StartMs += offset; result.EndMs += offset;
            return result;
        }).ToArray();

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string folder in Directory.EnumerateDirectories(source))
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }

    internal static void PreserveLog(string work, string assets)
    {
        string source = Path.Combine(work, "processing.log");
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(assets);
        try { File.Copy(source, Path.Combine(assets, "processing.log"), true); } catch { }
    }

    static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(value, CustomShowStore.JsonOptions),
        CustomShowStore.JsonOptions)!;

    internal static bool VerifyQueuePersistence()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "iqp-queue-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            CustomShowStore shows = new(root);
            CustomShowQueueStore storage = new(shows);
            CustomShowQueueDocument document = new();
            CustomShowQueueJob pending = new() { Message = "first" };
            CustomShowQueueJob running = new()
            {
                Status = CustomShowQueueStatus.Running, Percent = 67,
                Message = "working"
            };
            CustomShowQueueJob completed = new()
            {
                Status = CustomShowQueueStatus.Completed,
                PublishedShowId = "published"
            };
            document.Jobs.AddRange([pending, running, completed]);
            Directory.CreateDirectory(storage.Work(running.Id));
            File.WriteAllText(Path.Combine(storage.Work(running.Id), "partial"), "x");
            string published = Path.Combine(shows.ShowsFolder, "published");
            Directory.CreateDirectory(published);
            File.WriteAllText(Path.Combine(published, "sentinel"), "x");
            storage.Save(document);
            CustomShowQueueDocument loaded = storage.Load();
            if (loaded.Jobs.Count != 3 || loaded.Jobs[0].Id != pending.Id ||
                loaded.Jobs[1].Status != CustomShowQueueStatus.Pending ||
                loaded.Jobs[1].Percent != 0 || Directory.Exists(storage.Work(running.Id)))
                return false;
            CustomShowQueueManager manager = new(shows,
                new CustomShowConfiguration { LibraryRoot = root }, () => { });
            manager.Move(running.Id, -1);
            if (manager.Jobs[0].Id != running.Id) return false;
            manager.Move(running.Id, 1);
            manager.Move(running.Id, 1);
            if (manager.Jobs[1].Id != running.Id) return false;
            manager.Delete(completed.Id);
            if (manager.Find(completed.Id) != null || !Directory.Exists(published)) return false;
            CustomShowQueueJob firstTarget = new() { TargetShowId = "target" };
            manager.AddOrUpdate(firstTarget, null, null,
                new Dictionary<string, string>());
            try
            {
                manager.AddOrUpdate(new() { TargetShowId = "target" }, null, null,
                    new Dictionary<string, string>());
                return false;
            }
            catch (InvalidOperationException) { }
            finally { manager.Dispose(); }
            return JsonSerializer.Deserialize<CustomShowQueueDocument>(
                File.ReadAllText(storage.FilePath), CustomShowStore.JsonOptions) != null;
        }
        finally { CustomShowQueueStore.TryDelete(root); }
    }
}

internal sealed class CustomShowQueueForm : Form
{
    readonly CustomShowQueueManager manager;
    readonly Action<CustomShowQueueJob> edit;
    readonly QueueGrid grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoGenerateColumns = false, RowHeadersVisible = false
    };
    readonly Button run = new() { Text = "Run", AutoSize = true };
    readonly Button pause = new() { Text = "Pause", AutoSize = true };
    readonly Button stop = new() { Text = "Stop", AutoSize = true };
    readonly Button up = new() { Text = "Move Up", AutoSize = true };
    readonly Button down = new() { Text = "Move Down", AutoSize = true };
    readonly Button editButton = new() { Text = "Edit", AutoSize = true };
    readonly Button delete = new() { Text = "Delete", AutoSize = true };
    readonly Button retry = new() { Text = "Retry", AutoSize = true };
    readonly Button open = new() { Text = "Open", AutoSize = true };
    readonly Label summary = new() { Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    volatile bool refreshPending = true;

    internal CustomShowQueueForm(CustomShowQueueManager manager,
        Action<CustomShowQueueJob> edit)
    {
        this.manager = manager; this.edit = edit;
        Text = "Automatic Custom-Show Queue";
        ClientSize = new Size(1180, 520);
        MinimumSize = new Size(820, 360);
        StartPosition = FormStartPosition.CenterParent;
        grid.Columns.AddRange(
            Column("Operation", "Operation", 75), Column("Title", "Title", 145),
            Column("Model", "Model", 125), Column("Algorithm", "Algorithm", 120),
            Column("Source", "Source", 180), Column("Status", "Status", 95),
            Column("Progress", "Progress", 70), Column("Current work", "Message", 250),
            Column("Elapsed / ETA", "Timing", 135), Column("Completed", "Completed", 130));
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.Controls.Add(grid, 0, 0); layout.Controls.Add(summary, 0, 1);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, Padding = new Padding(8, 5, 8, 5) };
        buttons.Controls.AddRange([run, pause, stop, up, down, editButton, retry, open, delete]);
        layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        run.Click += (_, _) => manager.Run();
        pause.Click += Pause;
        stop.Click += async (_, _) => await manager.StopAsync();
        up.Click += (_, _) => { if (SelectedId() is string id) manager.Move(id, -1); };
        down.Click += (_, _) => { if (SelectedId() is string id) manager.Move(id, 1); };
        retry.Click += (_, _) => { if (SelectedId() is string id) manager.Retry(id); };
        delete.Click += Delete;
        open.Click += OpenSelected;
        editButton.Click += (_, _) => { if (Selected() is { } job) edit(job); };
        grid.SelectionChanged += (_, _) => RefreshButtons();
        grid.CellDoubleClick += (_, _) => { if (Selected() is { } job) edit(job); };
        manager.Changed += ManagerChanged;
        timer.Tick += (_, _) =>
        {
            if (Visible && (refreshPending || manager.IsRunning)) RefreshRows();
        };
        timer.Start();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        RefreshRows();
        AppTheme.Apply(this);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) RefreshRows();
    }

    static DataGridViewTextBoxColumn Column(string title, string property, int width) =>
        new() { HeaderText = title, DataPropertyName = property, Width = width,
            AutoSizeMode = property == "Message" ? DataGridViewAutoSizeColumnMode.Fill :
                DataGridViewAutoSizeColumnMode.None };

    void ManagerChanged(object? sender, EventArgs e)
    {
        // Progress originates on worker threads. Let the UI timer coalesce bursts
        // instead of rebuilding and repainting the grid for every processed batch.
        refreshPending = true;
    }

    void RefreshRows()
    {
        if (IsDisposed) return;
        refreshPending = false;
        string? selected = SelectedId();
        CustomShowQueueJob[] jobs = manager.Jobs.ToArray();
        QueueRow[] values = jobs.Select(job => new QueueRow(job)).ToArray();
        bool structureChanged = grid.Rows.Count != values.Length ||
            values.Where((value, index) => index >= grid.Rows.Count ||
                !string.Equals(grid.Rows[index].Tag as string, value.Id,
                    StringComparison.Ordinal)).Any();
        if (structureChanged)
        {
            grid.Rows.Clear();
            foreach (QueueRow value in values)
            {
                int index = grid.Rows.Add(value.Values());
                grid.Rows[index].Tag = value.Id;
            }
        }
        else
            for (int rowIndex = 0; rowIndex < values.Length; rowIndex++)
            {
                object[] rowValues = values[rowIndex].Values();
                DataGridViewRow row = grid.Rows[rowIndex];
                for (int column = 0; column < rowValues.Length; column++)
                    if (!Equals(row.Cells[column].Value, rowValues[column]))
                        row.Cells[column].Value = rowValues[column];
            }
        if (selected != null && structureChanged)
            foreach (DataGridViewRow row in grid.Rows)
                if (string.Equals(row.Tag as string, selected, StringComparison.Ordinal))
                { row.Selected = true; grid.CurrentCell = row.Cells[0]; break; }
        int pending = jobs.Count(job => job.Status == CustomShowQueueStatus.Pending);
        summary.Text = manager.IsRunning
            ? $"Queue running • {pending} pending" : $"Queue stopped • {pending} pending";
        RefreshButtons();
    }

    async void Pause(object? sender, EventArgs e)
    {
        if (!manager.HasActiveJob) { manager.PauseAfterCurrent(); return; }
        DialogResult answer = MessageBox.Show(this,
            "Choose Yes to let the current show finish and pause before the next.\n\n" +
            "Choose No to cancel it now. It will return to Pending and restart from " +
            "the beginning next time.", "Pause Queue", MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) manager.PauseAfterCurrent();
        else if (answer == DialogResult.No) await manager.CancelAndPauseAsync();
    }

    void Delete(object? sender, EventArgs e)
    {
        if (Selected() is not { } job || job.Status == CustomShowQueueStatus.Running) return;
        string text = job.Status == CustomShowQueueStatus.Completed
            ? "Remove this completed entry from queue history? The published show will not be deleted."
            : "Delete this queued job and its queue-owned assets?";
        if (MessageBox.Show(this, text, "Delete Queue Job", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) == DialogResult.Yes) manager.Delete(job.Id);
    }

    void OpenSelected(object? sender, EventArgs e)
    {
        if (Selected() is not { } job) return;
        string path = job.Status == CustomShowQueueStatus.Completed &&
            !string.IsNullOrWhiteSpace(job.PublishedShowId)
            ? manager.PublishedShowFolder(job.PublishedShowId)
            : manager.ProcessingLog(job.Id);
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                { UseShellExecute = true });
        else if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show(this, "No published show or retained log is available.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    CustomShowQueueJob? Selected() => SelectedId() is string id ? manager.Find(id) : null;
    string? SelectedId() => grid.CurrentRow?.Tag as string;

    void RefreshButtons()
    {
        CustomShowQueueJob? job = Selected();
        bool editable = job != null && job.Status != CustomShowQueueStatus.Running;
        up.Enabled = down.Enabled = editable && job!.Status != CustomShowQueueStatus.Completed;
        editButton.Enabled = editable;
        delete.Enabled = editable;
        retry.Enabled = job?.Status is CustomShowQueueStatus.Failed or
            CustomShowQueueStatus.NeedsAttention;
        open.Enabled = job?.Status is CustomShowQueueStatus.Completed or
            CustomShowQueueStatus.Failed or CustomShowQueueStatus.NeedsAttention;
        run.Enabled = !manager.IsRunning && manager.Jobs.Any(value =>
            value.Status == CustomShowQueueStatus.Pending);
        pause.Enabled = manager.IsRunning;
        stop.Enabled = manager.HasActiveJob;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            manager.Changed -= ManagerChanged;
            timer.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class QueueRow
    {
        readonly CustomShowQueueJob job;
        internal string Id => job.Id;
        public string Operation => job.Operation.ToString();
        public string Title => job.Manifest.Title;
        public string Model => job.Performer.ModelName;
        public string Algorithm => job.Manifest.Processing?.Algorithm ?? "";
        public string Source => Path.GetFileName(job.SourcePath);
        public string Status => job.Status == CustomShowQueueStatus.NeedsAttention
            ? "Needs attention" : job.Status.ToString();
        public string Progress => $"{job.Percent:0}%";
        public string Message => job.Error ?? job.Message;
        public string Timing
        {
            get
            {
                if (job.StartedUtc is not DateTime started) return "";
                TimeSpan elapsed = (job.CompletedUtc ?? DateTime.UtcNow) - started;
                if (job.Status != CustomShowQueueStatus.Running || job.Percent < 1)
                    return Format(elapsed);
                TimeSpan remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds *
                    (100 - job.Percent) / job.Percent);
                return $"{Format(elapsed)} / ~{Format(remaining)}";
            }
        }
        public string Completed => job.CompletedUtc?.ToLocalTime().ToString("g") ?? "";
        internal QueueRow(CustomShowQueueJob job) => this.job = job;
        internal object[] Values() => [Operation, Title, Model, Algorithm, Source,
            Status, Progress, Message, Timing, Completed];
        static string Format(TimeSpan value) => value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    sealed class QueueGrid : DataGridView
    {
        internal QueueGrid() => DoubleBuffered = true;
    }
}
