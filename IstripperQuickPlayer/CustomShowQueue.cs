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
    public int SchemaVersion { get; set; } = 2;
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
    public string RequestedOutputPath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public string? TargetShowId { get; set; }
    public string? TargetManifestSha256 { get; set; }
    public string? DependsOnQueueJobId { get; set; }
    public string? CoverAsset { get; set; }
    public Dictionary<string, string> InitialMaskAssets { get; set; } = [];
    public Dictionary<string, long> InitialMaskFrameMs { get; set; } = [];
    public CustomShowScenePrompt[] ScenePrompts { get; set; } = [];
    public bool KeepExistingMasks { get; set; }
    public string[] ReprocessClipIds { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public double Percent { get; set; }
    public string Message { get; set; } = "Pending";
    public string? Error { get; set; }
    public string? PublishedShowId { get; set; }
    public bool ReadyToPublish { get; set; }
}

internal sealed class CustomShowScenePrompt
{
    public string SceneId { get; set; } = "";
    public long PromptFrame { get; set; }
    public long PromptFrameMs { get; set; }
    public string InitialMaskAsset { get; set; } = "";
}

internal sealed record CustomShowQueueBatchEntry(CustomShowQueueJob Job,
    string? CoverSource, IReadOnlyDictionary<string, string> SceneMasks);

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
        if (document.SchemaVersion is not (1 or 2))
            throw new InvalidDataException("Unsupported custom-show queue version.");
        bool changed = document.SchemaVersion == 1;
        if (changed) document.SchemaVersion = 2;
        foreach (CustomShowQueueJob job in document.Jobs)
        {
            if (job.Manifest.SchemaVersion == 2)
            {
                job.Manifest.SchemaVersion = 3;
                changed = true;
            }
            if (job.ScenePrompts == null)
            {
                job.ScenePrompts = [];
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(job.RequestedOutputPath))
            {
                job.RequestedOutputPath = job.Operation == CustomShowQueueOperation.New
                    ? Path.Combine(shows.ShowsFolder, job.Manifest.Id)
                    : Path.Combine(shows.ShowsFolder, job.TargetShowId ?? job.Manifest.Id);
                changed = true;
            }
        }
        foreach (CustomShowQueueJob job in document.Jobs.Where(value =>
            value.Status == CustomShowQueueStatus.Running))
        {
            job.ReadyToPublish |= File.Exists(Path.Combine(
                Work(job.Id), "show.json"));
            job.Status = CustomShowQueueStatus.Pending;
            job.Percent = job.ReadyToPublish ? 100 : 0;
            job.Message = job.ReadyToPublish
                ? "Interrupted; ready to retry publication"
                : "Interrupted; ready to restart";
            job.StartedUtc = null;
            if (!job.ReadyToPublish) TryDelete(Work(job.Id));
            changed = true;
        }
        foreach (CustomShowQueueJob job in document.Jobs.Where(value =>
            value.Status == CustomShowQueueStatus.Pending &&
            (value.StartedUtc != null || value.CompletedUtc != null)))
        {
            job.StartedUtc = null;
            job.CompletedUtc = null;
            changed = true;
        }
        foreach (CustomShowQueueJob job in document.Jobs.Where(value =>
            value.Status == CustomShowQueueStatus.Failed &&
            value.Error?.StartsWith("Foreground processing failed",
                StringComparison.Ordinal) == true))
        {
            string log = Path.Combine(Assets(job.Id), "processing.log");
            string? workerError = CustomShowProcessor.WorkerErrorFromLog(log);
            if (workerError == null) continue;
            job.Error = workerError;
            changed = true;
        }
        foreach (CustomShowQueueJob job in document.Jobs.Where(value =>
            value.Status == CustomShowQueueStatus.Failed &&
            value.ReadyToPublish &&
            !string.Equals(value.Message, "Processed; publication failed",
                StringComparison.Ordinal)))
        {
            job.ReadyToPublish = false;
            job.Message = "Failed";
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
internal sealed class CustomShowPublicationException(string message, Exception inner) :
    IOException(message, inner);

internal sealed class CustomShowQueueManager : IDisposable
{
    const string WaitingForEarlierAppend =
        "Waiting for an earlier append to this show";
    readonly object gate = new();
    readonly CustomShowStore shows;
    readonly CustomShowConfiguration configuration;
    readonly CustomShowQueueStore storage;
    readonly Action published;
    readonly Func<string?, Task> preparePublication;
    readonly CustomShowQueueDocument document;
    CancellationTokenSource? cancellation;
    Task? loop;
    bool pauseAfterCurrent;
    DateTime lastSavedUtc;
    internal event EventHandler? Changed;

    internal CustomShowQueueManager(CustomShowStore shows,
        CustomShowConfiguration configuration, Action published,
        Func<string?, Task>? preparePublication = null)
    {
        this.shows = shows;
        this.configuration = configuration;
        this.published = published;
        this.preparePublication = preparePublication ?? (_ => Task.CompletedTask);
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

    void PrepareTargetDependencyLocked(CustomShowQueueJob job,
        string? existingId)
    {
        if (job.TargetShowId == null) return;
        CustomShowQueueJob[] active = document.Jobs.Where(value =>
            value.Id != existingId &&
            value.Status is CustomShowQueueStatus.Pending or
                CustomShowQueueStatus.Running &&
            string.Equals(value.TargetShowId, job.TargetShowId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Length == 0) return;
        if (job.Operation != CustomShowQueueOperation.Append ||
            active.Any(value => value.Operation != CustomShowQueueOperation.Append))
            throw new InvalidOperationException(
                "That show already has a pending or running queue job.");

        bool alreadyPrecedesChain = active.Any(value => string.Equals(
            value.DependsOnQueueJobId, job.Id,
            StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(job.DependsOnQueueJobId) &&
            !alreadyPrecedesChain)
            job.DependsOnQueueJobId = active[^1].Id;
        if (!string.IsNullOrWhiteSpace(job.DependsOnQueueJobId))
            job.Message = WaitingForEarlierAppend;
    }

    bool DependencyIncompleteLocked(CustomShowQueueJob job)
    {
        if (string.IsNullOrWhiteSpace(job.DependsOnQueueJobId)) return false;
        CustomShowQueueJob? dependency = document.Jobs.FirstOrDefault(value =>
            string.Equals(value.Id, job.DependsOnQueueJobId,
                StringComparison.OrdinalIgnoreCase));
        return dependency != null &&
            dependency.Status != CustomShowQueueStatus.Completed;
    }

    internal string AddOrUpdate(CustomShowQueueJob job, string? existingId,
        string? coverSource, IReadOnlyDictionary<string, string> masks,
        IReadOnlyDictionary<string, string>? sceneMasks = null)
    {
        lock (gate)
        {
            PrepareTargetDependencyLocked(job, existingId);
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
            if (sceneMasks != null)
            {
                Dictionary<string, CustomShowScenePrompt> prompts = job.ScenePrompts
                    .ToDictionary(value => value.SceneId,
                        StringComparer.OrdinalIgnoreCase);
                foreach ((string sceneId, string source) in sceneMasks)
                {
                    if (!prompts.TryGetValue(sceneId,
                            out CustomShowScenePrompt? prompt))
                        throw new InvalidDataException(
                            "A SAM2Matting mask does not match a queued scene.");
                    string destination = Path.Combine(assets, "scene-masks",
                        sceneId + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!string.Equals(Path.GetFullPath(source),
                            Path.GetFullPath(destination),
                            StringComparison.OrdinalIgnoreCase))
                        File.Copy(source, destination, true);
                    prompt.InitialMaskAsset = Path.GetRelativePath(assets, destination)
                        .Replace('\\', '/');
                }
            }
            SaveLocked();
        }
        OnChanged();
        return job.Id;
    }

    internal string[] AddBatch(IReadOnlyList<CustomShowQueueBatchEntry> entries)
    {
        if (entries.Count == 0) return [];
        string[] ids = entries.Select(entry => entry.Job.Id).ToArray();
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new InvalidDataException("Batch queue jobs require unique identities.");
        lock (gate)
        {
            if (entries.Any(entry =>
                    entry.Job.Operation != CustomShowQueueOperation.New) ||
                document.Jobs.Any(existing => ids.Contains(existing.Id,
                    StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "Only distinct new custom shows can be committed as a batch.");
            try
            {
                foreach (CustomShowQueueBatchEntry entry in entries)
                {
                    CustomShowQueueJob job = entry.Job;
                    string assets = storage.Assets(job.Id);
                    Directory.CreateDirectory(assets);
                    if (!string.IsNullOrWhiteSpace(entry.CoverSource))
                    {
                        string destination = Path.Combine(assets,
                            "cover" + Path.GetExtension(entry.CoverSource));
                        File.Copy(entry.CoverSource, destination, true);
                        job.CoverAsset = Path.GetRelativePath(assets, destination)
                            .Replace('\\', '/');
                    }
                    Dictionary<string, CustomShowScenePrompt> prompts = job.ScenePrompts
                        .ToDictionary(prompt => prompt.SceneId,
                            StringComparer.OrdinalIgnoreCase);
                    foreach ((string sceneId, string source) in entry.SceneMasks)
                    {
                        if (!prompts.TryGetValue(sceneId,
                                out CustomShowScenePrompt? prompt))
                            throw new InvalidDataException(
                                "A batch scene mask does not match its saved scene.");
                        string destination = Path.Combine(assets, "scene-masks",
                            sceneId + ".png");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination, true);
                        prompt.InitialMaskAsset = Path.GetRelativePath(assets, destination)
                            .Replace('\\', '/');
                    }
                    CustomShowJobRunner.Validate(job, shows, configuration, storage);
                    document.Jobs.Add(job);
                }
                SaveLocked();
            }
            catch
            {
                document.Jobs.RemoveAll(job => ids.Contains(job.Id,
                    StringComparer.OrdinalIgnoreCase));
                foreach (string id in ids) storage.DeleteAssets(id);
                throw;
            }
        }
        OnChanged();
        return ids;
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

    internal async Task<string> RunNowAsync(string id,
        IProgress<CustomShowProgress> progress,
        Func<string, CustomShowManifest, Task<bool>> reviewBeforePublication,
        CancellationToken token)
    {
        CustomShowQueueJob job;
        lock (gate)
        {
            if (loop is { IsCompleted: false })
                throw new InvalidOperationException(
                    "Pause queued processing before using Process and Preview.");
            job = document.Jobs.FirstOrDefault(value => value.Id == id) ??
                throw new InvalidOperationException("The processing job no longer exists.");
            if (job.Status != CustomShowQueueStatus.Pending)
                throw new InvalidOperationException(
                    "Only a pending job can be processed immediately.");
            if (DependencyIncompleteLocked(job))
                throw new InvalidOperationException(
                    "An earlier queued append to this show must complete first.");
            job.Status = CustomShowQueueStatus.Running;
            job.StartedUtc = DateTime.UtcNow;
            job.Percent = 0;
            job.Message = "Starting";
            job.Error = null;
            SaveLocked();
        }
        OnChanged();
        try
        {
            Progress<CustomShowProgress> trackedProgress = new(value =>
            {
                UpdateProgress(id, value.Percent, string.IsNullOrWhiteSpace(value.Message)
                    ? value.Stage : value.Message);
                progress.Report(value);
            });
            await EnsureRvmScenePromptsAsync(job, trackedProgress, token);
            string publishedId = await CustomShowJobRunner.RunAsync(job, shows,
                configuration, storage, trackedProgress, preparePublication, token,
                reviewBeforePublication, generatePreviews: true);
            lock (gate)
            {
                CompleteJobLocked(job, publishedId);
                SaveLocked();
            }
            try { published(); } catch (Exception error)
            { Debug.WriteLine("Could not refresh published custom show: " + error); }
            OnChanged();
            return publishedId;
        }
        catch
        {
            lock (gate)
            {
                job.Status = CustomShowQueueStatus.Failed;
                job.Message = "Process and Preview did not publish";
                job.CompletedUtc = DateTime.UtcNow;
                job.ReadyToPublish = false;
                SaveLocked();
            }
            CustomShowQueueStore.TryDelete(storage.Work(job.Id));
            OnChanged();
            throw;
        }
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
                    value.Status == CustomShowQueueStatus.Pending &&
                    !DependencyIncompleteLocked(value));
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
                await EnsureRvmScenePromptsAsync(job, progress, token);
                string publishedId = await CustomShowJobRunner.RunAsync(job, shows,
                    configuration, storage, progress, preparePublication, token);
                lock (gate)
                {
                    CompleteJobLocked(job, publishedId);
                    SaveLocked();
                }
                try { published(); } catch (Exception error)
                { Debug.WriteLine("Could not refresh published custom show: " + error); }
            }
            catch (OperationCanceledException)
            {
                lock (gate)
                {
                    job.ReadyToPublish |= File.Exists(Path.Combine(
                        storage.Work(job.Id), "show.json"));
                    job.Status = CustomShowQueueStatus.Pending;
                    job.Percent = job.ReadyToPublish ? 100 : 0;
                    job.Message = job.ReadyToPublish
                        ? "Stopped; ready to retry publication"
                        : "Stopped; ready to restart";
                    job.StartedUtc = null;
                    job.CompletedUtc = null;
                    SaveLocked();
                }
                if (!job.ReadyToPublish)
                    CustomShowQueueStore.TryDelete(storage.Work(job.Id));
                OnChanged();
                break;
            }
            catch (CustomShowQueueAttentionException error)
            {
                FinishError(job, CustomShowQueueStatus.NeedsAttention, error);
            }
            catch (CustomShowPublicationException error)
            {
                CustomShowJobRunner.PreserveLog(storage.Work(job.Id), storage.Assets(job.Id));
                lock (gate)
                {
                    job.Status = CustomShowQueueStatus.Failed;
                    job.Percent = 100;
                    job.Message = "Processed; publication failed";
                    job.Error = error.Message;
                    job.CompletedUtc = DateTime.UtcNow;
                    job.ReadyToPublish = true;
                    SaveLocked();
                }
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
        string work = storage.Work(job.Id);
        CustomShowJobRunner.PreserveLog(work, storage.Assets(job.Id));
        bool completedOutput = job.ReadyToPublish;
        lock (gate)
        {
            job.Status = status;
            job.ReadyToPublish = completedOutput;
            job.Percent = completedOutput ? 100 : job.Percent;
            job.Message = completedOutput
                ? "Processed; ready to retry publication"
                : status == CustomShowQueueStatus.NeedsAttention
                    ? "Needs attention" : "Failed";
            job.Error = error.Message;
            job.CompletedUtc = DateTime.UtcNow;
            SaveLocked();
        }
        if (!completedOutput)
            CustomShowQueueStore.TryDelete(work);
    }

    void CompleteJobLocked(CustomShowQueueJob job, string publishedId)
    {
        job.Status = CustomShowQueueStatus.Completed;
        job.Percent = 100;
        job.Message = "Completed";
        job.CompletedUtc = DateTime.UtcNow;
        job.PublishedShowId = publishedId;
        job.ReadyToPublish = false;
        if (job.Operation != CustomShowQueueOperation.Append ||
            string.IsNullOrWhiteSpace(job.TargetShowId)) return;

        CustomShowQueueJob? dependent = document.Jobs.FirstOrDefault(value =>
            string.Equals(value.DependsOnQueueJobId, job.Id,
                StringComparison.OrdinalIgnoreCase));
        if (dependent == null) return;
        dependent.DependsOnQueueJobId = null;
        try
        {
            dependent.TargetManifestSha256 = CustomShowQueueStore.HashFile(
                Path.Combine(shows.ShowsFolder, job.TargetShowId, "show.json"));
            if (dependent.Status == CustomShowQueueStatus.Pending &&
                string.Equals(dependent.Message, WaitingForEarlierAppend,
                    StringComparison.Ordinal))
                dependent.Message = "Pending";
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException)
        {
            dependent.Status = CustomShowQueueStatus.NeedsAttention;
            dependent.Message = "Needs attention";
            dependent.Error = "The preceding append completed, but the next " +
                "queued video could not be rebased onto the updated show.";
        }
    }

    async Task EnsureRvmScenePromptsAsync(CustomShowQueueJob job,
        IProgress<CustomShowProgress> progress, CancellationToken token)
    {
        CustomShowProcessing? options = job.Manifest.Processing;
        if (options?.Algorithm != Sam2MattingSupport.Algorithm ||
            options.PromptMode != "rvm-initial-mask")
            return;
        if (!CustomShowProcessor.IsRvmInitialMaskInstalled(configuration))
            throw new CustomShowQueueAttentionException(
                "Setup required: install or repair the Robust Video Matting processing tools.");
        HashSet<string> selectedClipIds = job.Clips.Where(clip => clip.Included)
            .Select(clip => clip.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (job.Operation == CustomShowQueueOperation.Reprocess &&
            job.ReprocessClipIds.Length > 0)
            selectedClipIds.IntersectWith(job.ReprocessClipIds);
        CustomShowProcessingScene[] scenes = options.Scenes.Where(scene =>
            selectedClipIds.Contains(scene.ClipId)).ToArray();
        if (scenes.Length == 0)
            throw new CustomShowQueueAttentionException(
                "The queued RVM/SAM2.1 processing scenes are missing. Edit the job to review it.");
        string assets = storage.Assets(job.Id);
        bool promptsReady = job.ScenePrompts.Length == scenes.Length &&
            job.ScenePrompts.All(prompt =>
                scenes.Any(scene => scene.Id.Equals(prompt.SceneId,
                    StringComparison.OrdinalIgnoreCase) &&
                    prompt.PromptFrame == scene.StartFrame &&
                    prompt.PromptFrameMs == scene.StartMs) &&
                !string.IsNullOrWhiteSpace(prompt.InitialMaskAsset) &&
                File.Exists(Path.Combine(assets, prompt.InitialMaskAsset.Replace('/',
                    Path.DirectorySeparatorChar))));
        if (promptsReady) return;

        string temporary = Path.Combine(assets,
            ".rvm-scene-masks-" + Guid.NewGuid().ToString("N"));
        try
        {
            RvmInitialMaskTarget[] targets = scenes.Select(scene =>
                new RvmInitialMaskTarget(Path.Combine(temporary, scene.Id + ".png"),
                    scene.StartMs)).ToArray();
            Progress<CustomShowProgress> initializerProgress = new(value =>
                progress.Report(value with
                {
                    Message = $"RVM initialization: {value.Message}"
                }));
            await CustomShowProcessor.GenerateRvmInitialMasksAsync(configuration,
                job.SourcePath, targets,
                options.RvmInitializerAlphaThresholdPercent ?? 40,
                initializerProgress, token,
                CustomShowJobRunner.PropSegmenterPath(options));

            string destinationFolder = Path.Combine(assets, "scene-masks");
            Directory.CreateDirectory(destinationFolder);
            CustomShowScenePrompt[] prompts = new CustomShowScenePrompt[scenes.Length];
            for (int index = 0; index < scenes.Length; index++)
            {
                CustomShowProcessingScene scene = scenes[index];
                string destination = Path.Combine(destinationFolder, scene.Id + ".png");
                File.Copy(targets[index].Destination, destination, true);
                prompts[index] = new CustomShowScenePrompt
                {
                    SceneId = scene.Id,
                    PromptFrame = scene.StartFrame,
                    PromptFrameMs = scene.StartMs,
                    InitialMaskAsset = Path.GetRelativePath(assets, destination)
                        .Replace('\\', '/')
                };
            }
            lock (gate)
            {
                job.ScenePrompts = prompts;
                job.Message = $"Created {prompts.Length} automatic RVM scene masks";
                SaveLocked();
            }
        }
        finally
        {
            CustomShowQueueStore.TryDelete(temporary);
        }
    }

    void UpdateProgress(string id, double percent, string message)
    {
        lock (gate)
        {
            CustomShowQueueJob? job = document.Jobs.FirstOrDefault(value => value.Id == id);
            if (job == null || job.Status != CustomShowQueueStatus.Running) return;
            double next = Math.Clamp(percent, 0, 100);
            // A worker stage with no percentage must not erase valid global
            // progress from a prior stage. Older SAM2Matting workers emitted
            // zero during tracking even though the job was still advancing.
            if (next > 0 || job.Percent <= 0)
                job.Percent = next;
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
                job.ReadyToPublish |= File.Exists(Path.Combine(
                    storage.Work(job.Id), "show.json"));
                job.Status = CustomShowQueueStatus.Pending;
                job.Percent = job.ReadyToPublish ? 100 : 0;
                job.Message = job.ReadyToPublish
                    ? "QuickPlayer closed; ready to retry publication"
                    : "QuickPlayer closed; ready to restart";
                job.StartedUtc = null;
                job.CompletedUtc = null;
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
                PrepareTargetDependencyLocked(job, job.Id);
                CustomShowJobRunner.Validate(job, shows, configuration, storage);
                job.Status = CustomShowQueueStatus.Pending;
                job.Percent = job.ReadyToPublish ? 100 : 0;
                job.Message = !string.IsNullOrWhiteSpace(job.DependsOnQueueJobId)
                    ? WaitingForEarlierAppend
                    : job.ReadyToPublish ? "Ready to retry publication" : "Pending";
                job.Error = null;
                job.StartedUtc = null;
                job.CompletedUtc = null;
            }
            catch (Exception error) when (error is
                CustomShowQueueAttentionException or InvalidOperationException)
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
            RewireDependentsLocked(document.Jobs[index]);
            document.Jobs.RemoveAt(index);
            storage.DeleteAssets(id);
            SaveLocked();
        }
        OnChanged();
    }

    internal bool DeleteFailed(string id)
    {
        lock (gate)
        {
            int index = document.Jobs.FindIndex(value => value.Id == id);
            if (index < 0 ||
                document.Jobs[index].Status != CustomShowQueueStatus.Failed)
                return false;
            RewireDependentsLocked(document.Jobs[index]);
            document.Jobs.RemoveAt(index);
            storage.DeleteAssets(id);
            SaveLocked();
        }
        OnChanged();
        return true;
    }

    internal int RemoveCompleted()
    {
        int removed;
        lock (gate)
        {
            string[] ids = document.Jobs
                .Where(job => job.Status == CustomShowQueueStatus.Completed)
                .Select(job => job.Id).ToArray();
            removed = ids.Length;
            if (removed == 0) return 0;
            document.Jobs.RemoveAll(job =>
                job.Status == CustomShowQueueStatus.Completed);
            foreach (string id in ids) storage.DeleteAssets(id);
            SaveLocked();
        }
        OnChanged();
        return removed;
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
            if (string.Equals(document.Jobs[index].DependsOnQueueJobId,
                    document.Jobs[other].Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(document.Jobs[other].DependsOnQueueJobId,
                    document.Jobs[index].Id, StringComparison.OrdinalIgnoreCase))
                return;
            (document.Jobs[index], document.Jobs[other]) =
                (document.Jobs[other], document.Jobs[index]);
            SaveLocked();
        }
        OnChanged();
    }

    void RewireDependentsLocked(CustomShowQueueJob removed)
    {
        foreach (CustomShowQueueJob dependent in document.Jobs.Where(value =>
            string.Equals(value.DependsOnQueueJobId, removed.Id,
                StringComparison.OrdinalIgnoreCase)))
        {
            dependent.DependsOnQueueJobId = removed.DependsOnQueueJobId;
            dependent.Message = string.IsNullOrWhiteSpace(
                dependent.DependsOnQueueJobId) ? "Pending" :
                WaitingForEarlierAppend;
        }
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
        Func<string?, Task> preparePublication, CancellationToken token,
        Func<string, CustomShowManifest, Task<bool>>? reviewBeforePublication = null,
        bool generatePreviews = false)
    {
        ResolvePerformer(job, store);
        Validate(job, store, configuration, queue);
        string staging = queue.Work(job.Id);
        if (job.ReadyToPublish && Directory.Exists(staging))
        {
            CustomShowManifest ready = JsonSerializer.Deserialize<CustomShowManifest>(
                await File.ReadAllTextAsync(Path.Combine(staging, "show.json"), token),
                CustomShowStore.JsonOptions) ?? throw new InvalidDataException(
                    "The completed queue staging manifest is invalid.");
            await PublishAsync(job, ready, staging, store, progress,
                preparePublication, token);
            return ready.Id;
        }
        CustomShowQueueStore.TryDelete(staging);
        Directory.CreateDirectory(staging);
        CustomShowManifest show;
        CustomShowProcessing? previousProcessing = null;
        CustomShowClip[] existing = [];
        long offset = 0;
        if (job.Operation == CustomShowQueueOperation.New)
            show = Clone(job.Manifest);
        else
        {
            show = store.LoadManifest(job.TargetShowId!);
            previousProcessing = show.Processing == null ? null : Clone(show.Processing);
            CopyDirectory(Path.Combine(store.ShowsFolder, show.Id), staging);
            if (job.Operation == CustomShowQueueOperation.Reprocess &&
                !job.KeepExistingMasks)
            {
                string masks = Path.Combine(staging, "masks");
                string[] replacedIds = (job.ReprocessClipIds.Length == 0
                    ? job.Clips.Where(clip => clip.Included).Select(clip => clip.Id)
                    : job.ReprocessClipIds).ToArray();
                foreach (string clipId in replacedIds)
                    CustomShowQueueStore.TryDelete(Path.Combine(masks, clipId));
                string retainedPath = Path.Combine(masks, "retained-masks.json");
                CustomRetainedMasksManifest? retained = CustomShowMaskDraft.Load<
                    CustomRetainedMasksManifest>(retainedPath);
                if (retained != null)
                {
                    HashSet<string> replaced = replacedIds.ToHashSet(
                        StringComparer.OrdinalIgnoreCase);
                    retained.Clips = retained.Clips.Where(clip =>
                        !replaced.Contains(clip.ClipId)).ToArray();
                    CustomShowStore.WriteJsonAtomic(retainedPath, retained);
                }
            }
            existing = show.Clips;
            offset = job.Operation == CustomShowQueueOperation.Append
                ? show.Media.DurationMs : 0;
            ApplyMetadata(show, job.Manifest);
        }
        CustomShowClip[] clips = Clone(job.Clips);
        CustomShowClip[] clipsToProcess = ClipsToProcess(job, clips);
        string log = Path.Combine(staging, "processing.log");
        Dictionary<string, string> generatedMasks = [];
        Dictionary<string, string> initialMasks = [];
        Dictionary<string, string> retainedMasks = [];
        if (job.Operation == CustomShowQueueOperation.Reprocess && job.KeepExistingMasks)
            LoadRetainedMasks(staging, clipsToProcess, initialMasks, retainedMasks);
        CustomShowProcessResult result = await ProcessClips(job, clipsToProcess, staging,
            configuration, queue, log, initialMasks, retainedMasks,
            generatedMasks, progress, token, generatePreviews);
        if (generatedMasks.Count > 0 && job.Operation != CustomShowQueueOperation.Append)
        {
            FileInfo sourceInfo = new(job.SourcePath);
            string masksRoot = Path.Combine(staging, "masks");
            CustomRetainedMasksManifest? retained = CustomShowMaskDraft.Load<
                CustomRetainedMasksManifest>(Path.Combine(masksRoot,
                    "retained-masks.json"));
            Dictionary<string, CustomRetainedMaskClip> retainedClips =
                retained?.Clips.ToDictionary(clip => clip.ClipId,
                    StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (CustomShowClip clip in clips.Where(clip => clip.Included &&
                generatedMasks.ContainsKey(clip.Id)))
                retainedClips[clip.Id] = new CustomRetainedMaskClip
                {
                    ClipId = clip.Id, StartMs = clip.StartMs,
                    EndMs = clip.EndMs, HasTrackedMasks = true,
                    TrackedMaskArchive = "tracked-masks.iqpmask"
                };
            CustomShowStore.WriteJsonAtomic(Path.Combine(staging, "masks",
                "retained-masks.json"), new CustomRetainedMasksManifest
                {
                    SourceLength = sourceInfo.Length,
                    SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
                    Clips = retainedClips.Values.ToArray()
                });
        }
        int alphaThreshold = job.Manifest.Processing?.AutoAcceptedAlphaThreshold
            ?? configuration.DefaultAlphaThreshold;
        foreach (CustomShowClip clip in clipsToProcess)
            clip.AlphaThreshold = alphaThreshold;
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
        show.Processing = Processing(job, result, show.Clips, clipsToProcess,
            previousProcessing, offset, configuration.DefaultAlphaThreshold);
        await SaveCover(job, show, staging, queue, token);
        CustomShowStore.WriteJsonAtomic(Path.Combine(staging, "show.json"), show);
        job.ReadyToPublish = true;
        if (reviewBeforePublication != null &&
            !await reviewBeforePublication(staging, show))
        {
            job.ReadyToPublish = false;
            throw new OperationCanceledException(
                "Process and Preview was cancelled before publication.");
        }
        await PublishAsync(job, show, staging, store, progress,
            preparePublication, token);
        return show.Id;
    }

    static void ResolvePerformer(CustomShowQueueJob job, CustomShowStore store)
    {
        CustomPerformerProfile? existing = store.LoadPerformers().FirstOrDefault(value =>
            string.Equals(value.ModelName.Trim(), job.Performer.ModelName.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (existing == null || string.Equals(existing.Id, job.Performer.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            store.SavePerformer(job.Performer);
            return;
        }
        job.Performer = existing;
        job.Manifest.PerformerId = existing.Id;
    }

    static async Task PublishAsync(CustomShowQueueJob job, CustomShowManifest show,
        string staging, CustomShowStore store, IProgress<CustomShowProgress> progress,
        Func<string?, Task> preparePublication, CancellationToken token)
    {
        progress.Report(new CustomShowProgress("Waiting to publish", 100,
            "Processing complete; preparing the library update"));
        try
        {
            await preparePublication(job.Operation == CustomShowQueueOperation.New
                ? null : job.TargetShowId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new CustomShowPublicationException(
                "Processing completed, but QuickPlayer could not release the " +
                "currently playing target show for publication. Retry this queue " +
                "entry to publish the completed output without processing again.", error);
        }
        const int attempts = 40;
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (job.Operation == CustomShowQueueOperation.New)
                    store.Publish(staging, show);
                else store.Replace(staging, show);
                return;
            }
            catch (Exception error) when (error is IOException or
                UnauthorizedAccessException)
            {
                if (attempt >= attempts)
                    throw new CustomShowPublicationException(
                        "Processing completed, but QuickPlayer could not replace the " +
                        "existing show because its folder remained in use. Retry this " +
                        "queue entry to publish the completed output without processing again.",
                        error);
                progress.Report(new CustomShowProgress("Waiting to publish", 100,
                    $"Waiting for the existing show files to be released ({attempt}/{attempts})"));
                await Task.Delay(250, token);
            }
        }
    }

    internal static void Validate(CustomShowQueueJob job, CustomShowStore store,
        CustomShowConfiguration configuration, CustomShowQueueStore queue)
    {
        if (job.Manifest.Processing?.Algorithm is not
            ("quality" or "fast" or "rvm-matanyone2" or
             "rvm-vitmatte-s" or "rvm-vitmatte-b" or "matanyone2" or "sam2matting"))
            throw new CustomShowQueueAttentionException(
                "This processing algorithm cannot run unattended.");
        bool installed = job.Manifest.Processing.Algorithm switch
        {
            "rvm-matanyone2" => CustomShowProcessor.IsRvmMatAnyone2Installed(configuration),
            "rvm-vitmatte-s" => CustomShowProcessor.IsRvmViTMatteSmallInstalled(configuration),
            "rvm-vitmatte-b" => CustomShowProcessor.IsRvmViTMatteBaseInstalled(configuration),
            "matanyone2" => CustomShowProcessor.IsMatAnyone2Installed(configuration),
            "sam2matting" => Sam2MattingSupport.IsInstalled(configuration,
                job.Manifest.Processing.Tracker),
            _ => File.Exists(CustomShowProcessor.WorkerPath)
        };
        if (!installed)
            throw new CustomShowQueueAttentionException(
                job.Manifest.Processing.Algorithm == "sam2matting"
                    ? "Setup required: install or repair the SAM2Matting environment and checkpoints."
                    : "The selected processing tools are not currently installed.");
        if (job.Manifest.Processing.Algorithm == "sam2matting" &&
            job.Manifest.Processing.PromptMode == "rvm-initial-mask" &&
            !CustomShowProcessor.IsRvmInitialMaskInstalled(configuration))
            throw new CustomShowQueueAttentionException(
                "Setup required: install or repair the Robust Video Matting processing tools.");
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
        if (job.Operation == CustomShowQueueOperation.Reprocess &&
            job.ReprocessClipIds.Length > 0)
        {
            HashSet<string> included = job.Clips.Where(clip => clip.Included)
                .Select(clip => clip.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (job.ReprocessClipIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    job.ReprocessClipIds.Length ||
                job.ReprocessClipIds.Any(id => !included.Contains(id)))
                throw new CustomShowQueueAttentionException(
                    "The selected clips to reprocess no longer match the show.");
        }
        if (job.CoverAsset != null && !File.Exists(Path.Combine(queue.Assets(job.Id),
                job.CoverAsset.Replace('/', Path.DirectorySeparatorChar))))
            throw new CustomShowQueueAttentionException(
                "The queued custom cover is missing. Edit the job to select it again.");
        if (job.Manifest.Processing?.Algorithm == "matanyone2")
            foreach (CustomShowClip clip in ClipsToProcess(job, job.Clips))
                if (!job.InitialMaskAssets.TryGetValue(clip.Id, out string? mask) ||
                    !File.Exists(Path.Combine(queue.Assets(job.Id),
                        mask.Replace('/', Path.DirectorySeparatorChar))))
                    throw new CustomShowQueueAttentionException(
                        "A MatAnyone initial mask is missing. Edit and mask the job again.");
        if (job.Manifest.Processing?.Algorithm == "sam2matting")
        {
            CustomShowProcessing options = job.Manifest.Processing;
            try
            {
                Sam2MattingScenePlanner.Validate(options.Scenes, job.Clips);
                using FfmpegCpuDecoder decoder = new(job.SourcePath,
                    fastDecode: true);
                if (!CustomShowStore.TryFrameRate(decoder.FrameRate,
                        out double sceneFps))
                    throw new InvalidDataException(
                        "The source frame rate is invalid.");
                Sam2MattingScenePlanner.ValidateCoverage(options.Scenes,
                    job.Clips, sceneFps);
            }
            catch (InvalidDataException error)
            {
                throw new CustomShowQueueAttentionException(error.Message);
            }
            if (string.IsNullOrWhiteSpace(job.RequestedOutputPath))
                throw new CustomShowQueueAttentionException(
                    "The queued output target is missing. Edit the job to review it.");
            if (options.PromptMode != "rvm-initial-mask")
            {
                CustomShowProcessingScene[] scenes = options.Scenes.Where(scene =>
                    ClipsToProcess(job, job.Clips).Any(clip =>
                        clip.Id.Equals(scene.ClipId,
                            StringComparison.OrdinalIgnoreCase))).ToArray();
                if (job.ScenePrompts.Length != scenes.Length)
                    throw new CustomShowQueueAttentionException(
                        "Every SAM2.1 processing scene requires exactly one saved initial mask.");
                Dictionary<string, CustomShowProcessingScene> byId = scenes.ToDictionary(
                    scene => scene.Id, StringComparer.OrdinalIgnoreCase);
                foreach (CustomShowScenePrompt prompt in job.ScenePrompts)
                    if (!byId.TryGetValue(prompt.SceneId,
                            out CustomShowProcessingScene? scene) ||
                        prompt.PromptFrame < scene.StartFrame ||
                        prompt.PromptFrame >= scene.EndFrameExclusive ||
                        string.IsNullOrWhiteSpace(prompt.InitialMaskAsset) ||
                        !File.Exists(Path.Combine(queue.Assets(job.Id),
                            prompt.InitialMaskAsset.Replace('/',
                                Path.DirectorySeparatorChar))))
                        throw new CustomShowQueueAttentionException(
                            "A SAM2.1 scene mask is missing or does not match its saved prompt frame.");
            }
        }
    }

    static async Task<CustomShowProcessResult> ProcessClips(CustomShowQueueJob job,
        CustomShowClip[] clips, string staging, CustomShowConfiguration configuration,
        CustomShowQueueStore queue, string log,
        IReadOnlyDictionary<string, string> retainedInitialMasks,
        IReadOnlyDictionary<string, string> retainedTrackedMasks,
        Dictionary<string, string> generatedMasks,
        IProgress<CustomShowProgress> progress, CancellationToken token,
        bool generatePreviews)
    {
        CustomShowProcessing options = job.Manifest.Processing!;
        string? propSegmenterPath = PropSegmenterPath(options);
        CustomShowClip[] included = clips.Where(value => value.Included).ToArray();
        long total = included.Sum(value => Math.Max(1, value.EndMs - value.StartMs));
        bool cleanup = options.TemporalAlphaCleanup;
        async Task CleanupAlpha(string output,
            IProgress<CustomShowProgress> cleanupProgress) =>
            await CustomShowProcessor.RunTemporalAlphaCleanupAsync(configuration,
                output, options.TemporalAlphaCleanupWindowFrames,
                options.TemporalAlphaCleanupStrengthPercent,
                options.TemporalAlphaTrackingStrengthPercent,
                options.TemporalAlphaCleanupAlphaThreshold,
                log, cleanupProgress, token);
        if (options.Algorithm == "sam2matting")
        {
            IProgress<CustomShowProgress> processingProgress = cleanup
                ? new Progress<CustomShowProgress>(value => progress.Report(value with
                    { Percent = value.Percent * .9 }))
                : progress;
            CustomShowProcessResult result = await CustomShowProcessor.RunSam2MattingAsync(
                configuration, job, included, staging, queue.Assets(job.Id), log,
                processingProgress, token, generatePreviews);
            if (result.Clips == null || result.Clips.Length != included.Length)
                throw new InvalidDataException(
                    "SAM2Matting returned an incomplete clip result contract.");
            for (int index = 0; index < included.Length; index++)
            {
                int clipIndex = index;
                if (cleanup)
                    await CleanupAlpha(Path.Combine(staging, "clips", included[index].Id),
                        new Progress<CustomShowProgress>(value => progress.Report(value with
                        {
                            Percent = 90 + 10d * (clipIndex + value.Percent / 100) /
                                included.Length,
                            Message = $"Clip {clipIndex + 1}/{included.Length}: {value.Message}"
                        })));
                SetMedia(included[index], result.Clips[index]);
            }
            return result;
        }
        if (options.Algorithm is "quality" or "fast")
        {
            CustomShowProcessJob[] jobs = included.Select(clip => new CustomShowProcessJob(
                Path.Combine(staging, "clips", clip.Id), clip.StartMs, clip.EndMs)).ToArray();
            CustomShowProcessResult first = await CustomShowProcessor.RunAsync(configuration,
                job.SourcePath, staging, options.Algorithm, null, null,
                options.MattingDetailPx, options.BatchSize, 0, null, log, false,
                cleanup ? new Progress<CustomShowProgress>(value => progress.Report(
                    value with { Percent = value.Percent * .9 })) : progress,
                token, jobs);
            for (int i = 0; i < included.Length; i++)
            {
                int clipIndex = i;
                CustomShowProcessResult media = i == 0 ? first : JsonSerializer.Deserialize<
                    CustomShowProcessResult>(await File.ReadAllTextAsync(Path.Combine(
                        jobs[i].Output, "result.json"), token), CustomShowStore.JsonOptions)!;
                if (cleanup)
                    await CleanupAlpha(jobs[i].Output,
                        new Progress<CustomShowProgress>(value => progress.Report(value with
                        {
                            Percent = 90 + 10d * (clipIndex + value.Percent / 100) /
                                included.Length,
                            Message = $"Clip {clipIndex + 1}/{included.Length}: {value.Message}"
                        })));
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
            IProgress<CustomShowProgress> aggregate = new Progress<CustomShowProgress>(value => progress.Report(value with
            {
                Percent = CustomShowProcessingForm.AggregateClipPercent(before,
                    clipDuration, total, value.Percent),
                Message = $"Clip {i + 1}/{included.Length}: {value.Message}"
            }));
            IProgress<CustomShowProgress> processingProgress = cleanup
                ? new Progress<CustomShowProgress>(value => aggregate.Report(value with
                    { Percent = value.Percent * .9 }))
                : aggregate;
            IProgress<CustomShowProgress> cleanupProgress = new Progress<CustomShowProgress>(value =>
                aggregate.Report(value with { Percent = 90 + value.Percent * .1 }));
            string output = Path.Combine(staging, "clips", clip.Id);
            string? mask = job.InitialMaskAssets.TryGetValue(clip.Id, out string? relative)
                ? Path.Combine(queue.Assets(job.Id), relative.Replace('/',
                    Path.DirectorySeparatorChar))
                : retainedInitialMasks.GetValueOrDefault(clip.Id);
            string? tracked = retainedTrackedMasks.GetValueOrDefault(clip.Id);
            CustomShowProcessResult media = await CustomShowProcessor.RunAsync(configuration,
                job.SourcePath, output, options.Algorithm, mask, tracked,
                options.MattingDetailPx, options.BatchSize, clip.StartMs, clip.EndMs,
                log, i > 0, processingProgress, token, maskFrameMs:
                    job.InitialMaskFrameMs.GetValueOrDefault(clip.Id, clip.StartMs),
                rvmInitializerAlphaThresholdPercent:
                    options.RvmInitializerAlphaThresholdPercent ?? 40,
                rvmMatAnyoneMaskRefresh: options.RvmMatAnyoneMaskRefresh,
                propSegmenterEveryFrame:
                    options.PropSegmenterEveryFrame,
                debugPropContribution: options.DebugPropContribution,
                rvmMatAnyoneRefreshStrengthPercent:
                    options.RvmMatAnyoneRefreshStrengthPercent,
                matAnyoneMaxMemoryFrames: options.MatAnyoneMaxMemoryFrames,
                matAnyoneUseLongTermMemory: options.MatAnyoneUseLongTermMemory,
                vitMatteInferenceDetailPx:
                    options.VitMatteInferenceDetailPx ?? 1024,
                propSegmenterModelPath: propSegmenterPath);
            if (cleanup)
                await CleanupAlpha(output, cleanupProgress);
            if (options.Algorithm is "rvm-vitmatte-s" or "rvm-vitmatte-b")
            {
                string masks = Path.Combine(output, ".rvm-masks");
                if (Directory.Exists(masks)) generatedMasks[clip.Id] = masks;
            }
            SetMedia(clip, media);
            completed += clipDuration;
            firstResult ??= media;
        }
        if (options.Algorithm is "rvm-vitmatte-s" or "rvm-vitmatte-b")
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

    internal static string? PropSegmenterPath(CustomShowProcessing options)
    {
        if (options.PropSegmenterModelId is not string id) return null;
        if (!PropSegmenterPackage.TryLoad(id, true, out PropSegmenterPackage? package) ||
            !string.Equals(package!.CheckpointSha256,
                options.PropSegmenterCheckpointSha256,
                StringComparison.OrdinalIgnoreCase) ||
            package.ConfidenceThreshold != options.PropSegmenterConfidenceThreshold ||
            package.ProximityRadiusAt512 != options.PropSegmenterProximityRadiusAt512 ||
            options.PropSegmenterManifestSha256 != null &&
                (!package.ManifestSha256.Equals(options.PropSegmenterManifestSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                 package.ManifestSchemaVersion != options.PropSegmenterManifestSchemaVersion ||
                 package.Architecture != options.PropSegmenterArchitecture ||
                 package.PostprocessingContract != options.PropSegmenterPostprocessingContract))
            throw new CustomShowQueueAttentionException(
                $"The queued prop-segmenter model '{id}' is missing, changed, or incompatible. Reinstall that exact model or edit the job.");
        return package.Folder;
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
        CustomShowProcessResult result, CustomShowClip[] clips,
        CustomShowClip[] processedClips, CustomShowProcessing? previous,
        long appendedOffset, int defaultAlphaThreshold)
    {
        if (job.Operation == CustomShowQueueOperation.Reprocess && previous != null &&
            processedClips.Length < clips.Count(clip => clip.Included))
            return Clone(previous);
        CustomShowProcessing value = Clone(job.Manifest.Processing!);
        value.AutoAcceptedAlphaThreshold =
            job.Manifest.Processing?.AutoAcceptedAlphaThreshold ??
                defaultAlphaThreshold;
        value.ProcessedUtc = DateTime.UtcNow;
        value.QuickPlayerVersion = Application.ProductVersion;
        value.ResolvedExecutionMode = result.ExecutionMode;
        value.EffectiveBatchSize = result.EffectiveSequenceChunk;
        value.PipelineDepth = result.PipelineDepth;
        value.Encoder = result.Encoder;
        value.EncoderPreset = result.EncoderPreset;
        value.Clips = clips.Where(clip => clip.Included).Select(clip =>
            new CustomClipProcessing { ClipId = clip.Id,
                InitialMaskFrameMs = value.Algorithm == "matanyone2" &&
                    job.InitialMaskFrameMs.TryGetValue(clip.Id, out long frame)
                    ? checked(frame + appendedOffset) : null })
            .ToArray();
        return value;
    }

    static CustomShowClip[] ClipsToProcess(CustomShowQueueJob job,
        IEnumerable<CustomShowClip> clips)
    {
        CustomShowClip[] included = clips.Where(clip => clip.Included).ToArray();
        if (job.Operation != CustomShowQueueOperation.Reprocess ||
            job.ReprocessClipIds.Length == 0) return included;
        HashSet<string> selected = job.ReprocessClipIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        return included.Where(clip => selected.Contains(clip.Id)).ToArray();
    }

    static async Task SaveCover(CustomShowQueueJob job, CustomShowManifest show,
        string staging, CustomShowQueueStore queue, CancellationToken token)
    {
        string destination = Path.Combine(staging, show.Media.Cover);
        if (job.CoverAsset != null)
        {
            ResizeCover(Path.Combine(queue.Assets(job.Id), job.CoverAsset.Replace('/',
                Path.DirectorySeparatorChar)), destination, job.Performer.ModelName,
                show.Title, ColorTranslator.FromHtml(show.Media.CoverTitleColor),
                show.Media.CoverModelFont, show.Media.CoverTitleFont);
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
                ColorTranslator.FromHtml(show.Media.CoverTitleColor), token,
                show.Media.CoverModelFont, show.Media.CoverTitleFont);
            show.Media.AutoGeneratedCover = true;
        }
    }

    static void ResizeCover(string source, string destination, string modelName,
        string showTitle, Color titleColor, string modelFont, string titleFont)
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
        CustomShowProcessor.DrawCoverText(copy, modelName, showTitle, titleColor,
            modelFont, titleFont);
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
        target.Media.CoverModelFont = source.Media.CoverModelFont;
        target.Media.CoverTitleFont = source.Media.CoverTitleFont;
        target.Media.PhotosFolder = source.Media.PhotosFolder;
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
            if (!CustomShowStore.IsValidDetectionLabel("Short (<20s)") ||
                !CustomShowStore.IsValidDetectionLabel("Short (<0.125s)") ||
                CustomShowStore.IsValidDetectionLabel("Short (<twenty seconds)"))
                return false;
            CustomShowStore shows = new(root);
            CustomShowQueueStore storage = new(shows);
            CustomPerformerProfile savedPerformer = new()
            {
                ModelName = "Existing model", IstripperModelId = "123"
            };
            shows.SavePerformer(savedPerformer);
            CustomShowQueueJob duplicatePerformer = new()
            {
                Performer = new()
                {
                    ModelName = "existing MODEL", IstripperModelId = "456"
                }
            };
            duplicatePerformer.Manifest.PerformerId = duplicatePerformer.Performer.Id;
            ResolvePerformer(duplicatePerformer, shows);
            if (duplicatePerformer.Performer.Id != savedPerformer.Id ||
                duplicatePerformer.Manifest.PerformerId != savedPerformer.Id)
                return false;
            CustomShowQueueDocument document = new();
            CustomShowQueueJob pending = new()
            {
                Message = "first", StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow
            };
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
            CustomShowQueueJob validationFailed = new()
            {
                Status = CustomShowQueueStatus.Failed,
                ReadyToPublish = true,
                Message = "Processed; ready to retry publication",
                Error = "Foreground/alpha validation failed"
            };
            CustomShowQueueJob publicationFailed = new()
            {
                Status = CustomShowQueueStatus.Failed,
                ReadyToPublish = true,
                Message = "Processed; publication failed"
            };
            document.Jobs.AddRange([pending, running, completed,
                validationFailed, publicationFailed]);
            Directory.CreateDirectory(storage.Work(running.Id));
            File.WriteAllText(Path.Combine(storage.Work(running.Id), "partial"), "x");
            string published = Path.Combine(shows.ShowsFolder, "published");
            Directory.CreateDirectory(published);
            File.WriteAllText(Path.Combine(published, "sentinel"), "x");
            storage.Save(document);
            CustomShowQueueDocument loaded = storage.Load();
            if (loaded.Jobs.Count != 5 || loaded.Jobs[0].Id != pending.Id ||
                loaded.Jobs[0].StartedUtc != null || loaded.Jobs[0].CompletedUtc != null ||
                loaded.Jobs[1].Status != CustomShowQueueStatus.Pending ||
                loaded.Jobs[1].Percent != 0 || Directory.Exists(storage.Work(running.Id)) ||
                loaded.Jobs[3].ReadyToPublish ||
                loaded.Jobs[3].Message != "Failed" ||
                !loaded.Jobs[4].ReadyToPublish)
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
            CustomShowQueueJob completedHistory = new()
            {
                Status = CustomShowQueueStatus.Completed,
                PublishedShowId = "published"
            };
            CustomShowQueueJob failedHistory = new()
            {
                Status = CustomShowQueueStatus.Failed
            };
            manager.AddOrUpdate(completedHistory, null, null,
                new Dictionary<string, string>());
            manager.AddOrUpdate(failedHistory, null, null,
                new Dictionary<string, string>());
            if (manager.RemoveCompleted() != 1 ||
                manager.Find(completedHistory.Id) != null ||
                manager.Find(failedHistory.Id) == null ||
                Directory.Exists(storage.Assets(completedHistory.Id)) ||
                !Directory.Exists(published)) return false;
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
            manager.Delete(firstTarget.Id);

            CustomShowQueueJob firstAppend = new()
            {
                Operation = CustomShowQueueOperation.Append,
                TargetShowId = "append-target"
            };
            CustomShowQueueJob secondAppend = new()
            {
                Operation = CustomShowQueueOperation.Append,
                TargetShowId = "append-target"
            };
            CustomShowQueueJob thirdAppend = new()
            {
                Operation = CustomShowQueueOperation.Append,
                TargetShowId = "append-target"
            };
            manager.AddOrUpdate(firstAppend, null, null,
                new Dictionary<string, string>());
            manager.AddOrUpdate(secondAppend, null, null,
                new Dictionary<string, string>());
            manager.AddOrUpdate(thirdAppend, null, null,
                new Dictionary<string, string>());
            if (manager.Find(secondAppend.Id)?.DependsOnQueueJobId !=
                    firstAppend.Id ||
                manager.Find(thirdAppend.Id)?.DependsOnQueueJobId !=
                    secondAppend.Id)
                return false;
            manager.Delete(secondAppend.Id);
            if (manager.Find(thirdAppend.Id)?.DependsOnQueueJobId !=
                    firstAppend.Id)
                return false;
            manager.Delete(firstAppend.Id);
            if (manager.Find(thirdAppend.Id)?.DependsOnQueueJobId != null)
                return false;
            try
            {
                manager.AddOrUpdate(new()
                {
                    Operation = CustomShowQueueOperation.Reprocess,
                    TargetShowId = "append-target"
                }, null, null, new Dictionary<string, string>());
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
    const int MessageColumnIndex = 7;
    readonly CustomShowQueueManager manager;
    readonly Action<CustomShowQueueJob> edit;
    readonly Action<CustomShowQueueJob, string> duplicate;
    readonly QueueGrid grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        AllowUserToResizeColumns = true,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoGenerateColumns = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ScrollBars = ScrollBars.Both
    };
    readonly Button run = new() { Text = "Run", AutoSize = true };
    readonly Button pause = new() { Text = "Pause", AutoSize = true };
    readonly Button stop = new() { Text = "Stop", AutoSize = true };
    readonly Button up = new() { Text = "Move Up", AutoSize = true };
    readonly Button down = new() { Text = "Move Down", AutoSize = true };
    readonly Button editButton = new() { Text = "Edit", AutoSize = true };
    readonly Button duplicateButton = new() { Text = "Duplicate", AutoSize = true };
    readonly Button details = new() { Text = "Details", AutoSize = true };
    readonly Button delete = new() { Text = "Delete", AutoSize = true };
    readonly Button removeCompleted = new() { Text = "Remove Completed", AutoSize = true };
    readonly Button retry = new() { Text = "Retry", AutoSize = true };
    readonly Button open = new() { Text = "Open", AutoSize = true };
    readonly Label summary = new() { Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    volatile bool refreshPending = true;

    internal CustomShowQueueForm(CustomShowQueueManager manager,
        Action<CustomShowQueueJob> edit,
        Action<CustomShowQueueJob, string> duplicate)
    {
        this.manager = manager; this.edit = edit; this.duplicate = duplicate;
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
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.Controls.Add(grid, 0, 0); layout.Controls.Add(summary, 0, 1);
        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 5, 12, 12)
        };
        buttons.Controls.AddRange([run, pause, stop, up, down, editButton,
            duplicateButton, details, retry, open, delete, removeCompleted]);
        layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        run.Click += (_, _) => manager.Run();
        pause.Click += Pause;
        stop.Click += async (_, _) => await manager.StopAsync();
        up.Click += (_, _) => { if (SelectedId() is string id) manager.Move(id, -1); };
        down.Click += (_, _) => { if (SelectedId() is string id) manager.Move(id, 1); };
        retry.Click += (_, _) => { if (SelectedId() is string id) manager.Retry(id); };
        delete.Click += Delete;
        removeCompleted.Click += RemoveCompleted;
        open.Click += OpenSelected;
        editButton.Click += (_, _) => { if (Selected() is { } job) edit(job); };
        duplicateButton.Click += (_, _) => DuplicateSelected();
        details.Click += (_, _) => ShowDetails();
        grid.SelectionChanged += (_, _) => RefreshButtons();
        grid.CellContentClick += OpenLogLink;
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
            MinimumWidth = 30, Resizable = DataGridViewTriState.True,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None };

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
        for (int rowIndex = 0; rowIndex < jobs.Length; rowIndex++)
            ConfigureMessageCell(grid.Rows[rowIndex], jobs[rowIndex]);
        if (selected != null && structureChanged)
            foreach (DataGridViewRow row in grid.Rows)
                if (string.Equals(row.Tag as string, selected, StringComparison.Ordinal))
                { row.Selected = true; grid.CurrentCell = row.Cells[0]; break; }
        int pending = jobs.Count(job => job.Status == CustomShowQueueStatus.Pending);
        summary.Text = manager.IsRunning
            ? $"Queue running • {pending} pending" : $"Queue stopped • {pending} pending";
        RefreshButtons();
    }

    void ConfigureMessageCell(DataGridViewRow row, CustomShowQueueJob job)
    {
        bool link = job.Status is (CustomShowQueueStatus.Failed or
            CustomShowQueueStatus.NeedsAttention) &&
            File.Exists(manager.ProcessingLog(job.Id));
        DataGridViewCell current = row.Cells[MessageColumnIndex];
        if (link == (current is DataGridViewLinkCell)) return;
        DataGridViewCell replacement = link
            ? new DataGridViewLinkCell
            {
                LinkBehavior = LinkBehavior.HoverUnderline,
                TrackVisitedState = false
            }
            : new DataGridViewTextBoxCell();
        replacement.Value = current.Value;
        row.Cells[MessageColumnIndex] = replacement;
    }

    void OpenLogLink(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != MessageColumnIndex ||
            grid.Rows[e.RowIndex].Cells[e.ColumnIndex] is not DataGridViewLinkCell ||
            grid.Rows[e.RowIndex].Tag is not string id)
            return;
        string path = manager.ProcessingLog(id);
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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

    void RemoveCompleted(object? sender, EventArgs e)
    {
        int count = manager.Jobs.Count(job =>
            job.Status == CustomShowQueueStatus.Completed);
        if (count == 0) return;
        string entries = count == 1 ? "entry" : "entries";
        if (MessageBox.Show(this,
                $"Remove all {count} completed queue {entries} from history?\n\n" +
                "Published shows will not be deleted.", "Remove Completed Jobs",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            manager.RemoveCompleted();
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

    void DuplicateSelected()
    {
        if (Selected() is not { } source ||
            source.Status == CustomShowQueueStatus.Running) return;
        CustomShowQueueJob draft = JsonSerializer.Deserialize<CustomShowQueueJob>(
            JsonSerializer.Serialize(source, CustomShowStore.JsonOptions),
            CustomShowStore.JsonOptions)!;
        draft.Id = Guid.NewGuid().ToString("N");
        draft.Manifest.Id = Guid.NewGuid().ToString("N");
        draft.Manifest.Title = string.IsNullOrWhiteSpace(draft.Manifest.Title)
            ? "Copy" : draft.Manifest.Title + " copy";
        draft.Operation = CustomShowQueueOperation.New;
        draft.TargetShowId = null;
        draft.TargetManifestSha256 = null;
        draft.RequestedOutputPath = manager.PublishedShowFolder(draft.Manifest.Id);
        draft.Status = CustomShowQueueStatus.Pending;
        draft.Percent = 0;
        draft.Message = "Draft";
        draft.Error = null;
        draft.CreatedUtc = DateTime.UtcNow;
        draft.StartedUtc = null;
        draft.CompletedUtc = null;
        draft.PublishedShowId = null;
        draft.ReadyToPublish = false;
        duplicate(draft, source.Id);
    }

    void ShowDetails()
    {
        if (Selected() is not { } job) return;
        CustomShowProcessing? processing = job.Manifest.Processing;
        string prompt = processing?.Algorithm == "sam2matting"
                ? processing.PromptMode == "rvm-initial-mask" &&
                    job.ScenePrompts.Length == 0
                    ? $"Automatic RVM initial masks: pending; created when the job runs " +
                        $"for {processing.Scenes.Length} scenes"
                    : $"{(processing.PromptMode == "rvm-initial-mask" ?
                        "Automatic RVM initial masks" : "Interactive initial masks")}: " +
                        $"{job.ScenePrompts.Length}/{processing.Scenes.Length} scenes"
                : "Prompt: existing pipeline";
        string detail =
            $"Tracker: {(processing?.Tracker == null ? "—" : Sam2MattingSupport.DisplayName(processing.Tracker))}\r\n" +
            $"Prompt type: {processing?.PromptMode ?? "—"}\r\n{prompt}\r\n" +
            (processing?.RvmInitializerAlphaThresholdPercent is int threshold
                ? $"RVM initializer alpha: {threshold}%\r\n" : "") +
            $"Checkpoint revision: {processing?.CheckpointRevision ?? "—"}\r\n" +
            $"Scene detector: {job.Manifest.ClipDetection?.Method ?? "—"} " +
            $"({job.Manifest.ClipDetection?.ToolRevision ?? "saved settings"})\r\n" +
            $"Output policy: {processing?.EncoderPolicy ?? "existing"}; " +
            $"{processing?.AlphaEncodingPolicy ?? "existing"}\r\n" +
            $"Input: {job.SourcePath}\r\nOutput: {job.RequestedOutputPath}";
        MessageBox.Show(this, detail, $"Queue Details — {job.Manifest.Title}",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    CustomShowQueueJob? Selected() => SelectedId() is string id ? manager.Find(id) : null;
    string? SelectedId() => grid.CurrentRow?.Tag as string;

    void RefreshButtons()
    {
        CustomShowQueueJob? job = Selected();
        bool editable = job != null && job.Status != CustomShowQueueStatus.Running;
        up.Enabled = down.Enabled = editable && job!.Status != CustomShowQueueStatus.Completed;
        editButton.Enabled = editable;
        duplicateButton.Enabled = editable;
        details.Enabled = job != null;
        delete.Enabled = editable;
        removeCompleted.Enabled = manager.Jobs.Any(value =>
            value.Status == CustomShowQueueStatus.Completed);
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
        public string Algorithm => job.Manifest.Processing?.Algorithm == "sam2matting"
            ? Sam2MattingSupport.DisplayName(job.Manifest.Processing.Tracker)
            : job.Manifest.Processing?.Algorithm ?? "";
        public string Source => Path.GetFileName(job.SourcePath);
        public string Status => job.Status == CustomShowQueueStatus.NeedsAttention
            ? "Needs attention" : job.Status.ToString();
        public string Progress => $"{job.Percent:0}%";
        public string Message => job.Error ?? job.Message;
        public string Timing
        {
            get
            {
                if (job.Status == CustomShowQueueStatus.Pending) return "";
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
