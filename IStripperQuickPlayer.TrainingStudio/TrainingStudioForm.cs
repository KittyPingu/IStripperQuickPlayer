using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class TrainingStudioForm : Form
{
    readonly TextBox datasetPath = new() { Width = 430 };
    readonly ComboBox activeSource = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button rescanSource = new() { Text = "Rescan", AutoSize = true, Enabled = false };
    readonly Button history = new() { Text = "Dataset history", AutoSize = true, Enabled = false };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(30, 30, 30) };
    readonly Label candidateInfo = new() { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true };
    readonly Label stats = new() { Dock = DockStyle.Fill, AutoSize = false };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 30, AutoEllipsis = true };
    readonly TextBox trainingLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill };
    readonly Button next = new() { Text = "Random frame", AutoSize = true };
    readonly Button positive = new() { Text = "Accept + label", AutoSize = true, Enabled = false };
    readonly Button negative = new() { Text = "No foreground objects", AutoSize = true, Enabled = false };
    readonly Button reject = new() { Text = "Reject", AutoSize = true, Enabled = false };
    readonly Button burst = new() { Text = "Create 5-frame burst", AutoSize = true, Enabled = false };
    readonly Button undoDecision = new() { Text = "Undo last decision", AutoSize = true, Enabled = false };
    readonly Button train = new() { Text = "Train model", AutoSize = true };
    readonly Button cancelTraining = new() { Text = "Cancel training", AutoSize = true, Enabled = false };
    readonly Button install = new() { Text = "Install trained model", AutoSize = true, Enabled = false };
    readonly Button openReview = new() { Text = "Open error review", AutoSize = true, Enabled = false };
    readonly ComboBox minimumResolution = new() { Width = 105, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox trainingSize = new() { Width = 75, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox negativeSelection = new() { Width = 145, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label queueStatus = new() { Text = "Queue: 0/10", AutoSize = true,
        Padding = new Padding(10, 8, 0, 0) };
    readonly CheckBox resumeTraining = new() { Text = "Resume latest checkpoint", AutoSize = true,
        Padding = new(6, 7, 0, 0) };
    readonly SemaphoreSlim derivationGate = new(1, 1);
    readonly RvmPreviewClient rvmPreview = new();
    readonly PersistentSam2Session annotationSam2 = new();
    DatasetStore? store;
    TrainingSample? current;
    string? activeBurstId;
    CancellationTokenSource? operationCancellation, trainingCancellation;
    readonly CancellationTokenSource prefetchCancellation = new();
    readonly SemaphoreSlim prefetchFillGate = new(1, 1);
    readonly object previewCacheGate = new();
    readonly Dictionary<string, Bitmap> previewCache = [];
    readonly Dictionary<string, TrainingSample> previewSamples = [];
    readonly Queue<string> previewOrder = new();
    int previewGeneration;
    readonly object prefetchCancellationGate = new();
    CancellationTokenSource? nextFramePrefetchCancellation;
    CancellationTokenSource? queueFillCancellation;
    Task nextFramePrefetch = Task.CompletedTask;
    volatile bool queueIsDecoding;
    TrainingRunner? runner;
    string? installPackagePath;
    bool refreshingSourceFolders;

    internal TrainingStudioForm()
    {
        Text = "QuickPlayer Training Studio"; Width = 1450; Height = 900;
        MinimumSize = new(1050, 700); StartPosition = FormStartPosition.CenterScreen;
        datasetPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "IStripperQuickPlayer", "TrainingData");
        Button openDataset = new() { Text = "Open / create dataset", AutoSize = true };
        Button indexFolder = new() { Text = "Index video folder", AutoSize = true };
        FlowLayoutPanel top = new() { Dock = DockStyle.Top, Height = 44, Padding = new(6) };
        top.Controls.AddRange([new Label { Text = "Dataset", AutoSize = true, Padding = new(0, 8, 0, 0) },
            datasetPath, openDataset, indexFolder, history, queueStatus]);
        FlowLayoutPanel reviewActions = new() { Dock = DockStyle.Top, Height = 44, Padding = new(6) };
        reviewActions.Controls.AddRange([
            rescanSource,
            new Label { Text = "Active videos", AutoSize = true, Padding = new(8, 8, 0, 0) },
            activeSource, next, positive, negative, reject, burst, undoDecision]);

        FlowLayoutPanel trainActions = new()
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Padding = new Padding(0, 0, 0, 8), MinimumSize = new Size(0, 132)
        };
        minimumResolution.Items.AddRange(new object[]
        {
            new ResolutionOption("Any resolution", 0), new ResolutionOption("480p+", 480),
            new ResolutionOption("720p+", 720), new ResolutionOption("1080p+", 1080),
            new ResolutionOption("1440p+", 1440), new ResolutionOption("2160p+", 2160)
        });
        minimumResolution.SelectedIndex = 0;
        trainingSize.Items.AddRange(new object[] { 512, 768, 1024 });
        trainingSize.SelectedItem = 512;
        negativeSelection.Items.AddRange(new object[]
        {
            new NegativeOption("Compare 20–35%", "compare"),
            new NegativeOption("20%", "20"), new NegativeOption("25%", "25"),
            new NegativeOption("30%", "30"), new NegativeOption("35%", "35"),
            new NegativeOption("ALL negatives", "all")
        });
        negativeSelection.SelectedIndex = 0;
        trainActions.Controls.AddRange([train,
            new Label { Text = "Minimum", AutoSize = true, Padding = new Padding(6, 7, 0, 0) },
            minimumResolution,
            new Label { Text = "Training size", AutoSize = true, Padding = new Padding(6, 7, 0, 0) },
            trainingSize,
            new Label { Text = "Negatives", AutoSize = true, Padding = new Padding(6, 7, 0, 0) },
            negativeSelection, resumeTraining, cancelTraining, openReview, install]);
        Panel right = new() { Dock = DockStyle.Fill };
        Panel dashboard = new() { Dock = DockStyle.Top, Height = 242, Padding = new(8) };
        dashboard.Controls.Add(stats); dashboard.Controls.Add(new Label
        { Text = "Dataset dashboard", Dock = DockStyle.Top, Height = 28, Font = new Font(Font, FontStyle.Bold) });
        right.Controls.Add(trainingLog); right.Controls.Add(trainActions); right.Controls.Add(dashboard);

        Panel left = new() { Dock = DockStyle.Fill };
        left.Controls.Add(preview); left.Controls.Add(candidateInfo);
        SplitContainer split = new() { Dock = DockStyle.Fill, SplitterDistance = 900 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        Controls.Add(split); Controls.Add(reviewActions); Controls.Add(top); Controls.Add(status);

        openDataset.Click += (_, _) => ChooseDataset();
        indexFolder.Click += async (_, _) => await IndexFolderAsync();
        history.Click += (_, _) => OpenDatasetHistory();
        rescanSource.Click += async (_, _) => await RescanActiveFolderAsync();
        activeSource.SelectedIndexChanged += async (_, _) =>
        {
            if (refreshingSourceFolders || store == null || activeSource.SelectedItem is not string folder ||
                string.Equals(store.Dataset.ActiveSourceFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            activeBurstId = null; ClearPrefetchQueue(); ClearCurrent();
            status.Text = $"Switching active video source to {folder}";
            store.SetActiveSourceFolder(folder);
            await NextAsync();
        };
        next.Click += async (_, _) => await NextAsync();
        positive.Click += async (_, _) => await AcceptPositiveAsync();
        negative.Click += async (_, _) => await AcceptNegativeAsync();
        reject.Click += async (_, _) => await RejectAsync();
        burst.Click += async (_, _) => await BurstAsync();
        undoDecision.Click += async (_, _) => await UndoLastDecisionAsync();
        train.Click += async (_, _) => await TrainAsync();
        cancelTraining.Click += (_, _) => { trainingCancellation?.Cancel(); runner?.Cancel(); };
        openReview.Click += async (_, _) => await OpenErrorReviewAsync();
        minimumResolution.SelectedIndexChanged += async (_, _) =>
        {
            UpdateStats();
            if (store == null || current == null) return;
            int minimum = SelectedMinimumResolution();
            if (minimum == 0 || Math.Min(current.Width, current.Height) >= minimum) return;
            activeBurstId = null; ClearPrefetchQueue(); ClearCurrent(); await NextAsync();
        };
        negativeSelection.SelectedIndexChanged += (_, _) => UpdateStats();
        install.Click += (_, _) => InstallModel();
        Shown += (_, _) => OpenDataset(datasetPath.Text);
    }

    void ChooseDataset()
    {
        using FolderBrowserDialog dialog = new() { Description = "Choose or create a training dataset folder",
            SelectedPath = datasetPath.Text, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            datasetPath.Text = dialog.SelectedPath; OpenDataset(dialog.SelectedPath);
        }
    }

    void OpenDataset(string path)
    {
        try
        {
            ClearPrefetchQueue(); ClearCurrent();
            store = new DatasetStore(path); RefreshSourceFolders(); UpdateStats(); UpdateUndoDecision();
            history.Enabled = true;
            openReview.Enabled = ErrorReviewForm.FindLatest(store.Root) != null;
            installPackagePath = TrainingRunner.FindLatestPackage(store.Root);
            install.Enabled = installPackagePath != null;
            status.Text = "Dataset ready.";
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void OpenDatasetHistory()
    {
        if (store == null) return;
        using DatasetHistoryForm form = new(store, DeleteHistorySampleAsync);
        form.ShowDialog(this);
        UpdateStats(); UpdateUndoDecision();
    }

    async Task DeleteHistorySampleAsync(TrainingSample sample)
    {
        await derivationGate.WaitAsync();
        try { await Task.Run(() => store!.DeleteAcceptedSample(sample)); }
        finally { derivationGate.Release(); }
    }

    async Task IndexFolderAsync()
    {
        if (store == null) return;
        using FolderBrowserDialog dialog = new() { Description = "Choose a folder to scan recursively for videos",
            UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        bool sourceChanged = false;
        await BusyAsync(async token =>
        {
            int added = await store.IndexFolderAsync(dialog.SelectedPath,
                new Progress<string>(value => status.Text = value), token);
            activeBurstId = null; ClearPrefetchQueue(); ClearCurrent();
            status.Text = $"Indexed {added:N0} new video(s). Active source: {dialog.SelectedPath}";
            RefreshSourceFolders(); UpdateStats();
            sourceChanged = true;
        });
        if (sourceChanged) await NextAsync();
    }

    async Task RescanActiveFolderAsync()
    {
        if (store == null || activeSource.SelectedItem is not string folder) return;
        rescanSource.Enabled = false;
        try
        {
            await BusyAsync(async token =>
            {
                int added = await store.IndexFolderAsync(folder,
                    new Progress<string>(value => status.Text = value), token);
                ClearPrefetchQueue();
                RefreshSourceFolders(); UpdateStats();
                status.Text = added == 0
                    ? $"Rescan complete. No new videos found in {folder}"
                    : $"Rescan complete. Found {added:N0} new video(s) in {folder}";
                if (current != null)
                {
                    TrainingSample displayed = current;
                    StartNextFramePrefetch(displayed);
                }
            });
        }
        finally { rescanSource.Enabled = activeSource.SelectedIndex >= 0; }
    }

    void RefreshSourceFolders()
    {
        if (store == null) return;
        refreshingSourceFolders = true; activeSource.BeginUpdate();
        try
        {
            activeSource.Items.Clear();
            activeSource.Items.AddRange(store.Dataset.SourceFolders.Cast<object>().ToArray());
            string? active = store.Dataset.ActiveSourceFolder;
            int index = active == null ? -1 : activeSource.Items.Cast<string>().ToList()
                .FindIndex(value => string.Equals(value, active, StringComparison.OrdinalIgnoreCase));
            activeSource.SelectedIndex = index >= 0 ? index : activeSource.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            activeSource.EndUpdate(); refreshingSourceFolders = false;
            rescanSource.Enabled = activeSource.SelectedIndex >= 0;
        }
    }

    async Task NextAsync()
    {
        if (store == null) return;
        await nextFramePrefetch;
        if (TakeNextCachedPreview() is { } cached)
        {
            ShowSample(cached.Sample, cached.Preview); return;
        }
        await BusyAsync(async token =>
        {
            TrainingSample? sample = await Task.Run(() =>
                store.NextCandidate(activeBurstId, SelectedMinimumResolution()), token);
            if (sample == null)
            {
                int minimum = SelectedMinimumResolution();
                status.Text = minimum == 0
                    ? "No unseen eligible frame remains. Index more videos."
                    : $"No unseen frame at {minimum}p or above remains in the active videos.";
                return;
            }
            if (activeBurstId != null && sample.BurstId != activeBurstId)
                activeBurstId = null;
            if (sample.FramePath == null || !File.Exists(store.Resolve(sample.FramePath)))
                await store.ExtractDraftFrameAsync(sample, token);
            ShowSample(sample); await Task.CompletedTask;
        });
    }

    void ShowSample(TrainingSample sample, Bitmap? cachedPreview = null)
    {
        current = sample; preview.Image?.Dispose();
        preview.Image = cachedPreview ?? TakeCachedPreview(sample.Id) ??
            LoadUnlockedImage(store!.Resolve(sample.FramePath!));
        VideoSource source = store!.Source(sample.SourceId);
        string burstText = "";
        if (sample.BurstId != null)
        {
            TrainingSample[] members = store.Dataset.Samples.Where(value =>
                    value.BurstId == sample.BurstId)
                .OrderBy(value => value.BurstIndex).ToArray();
            int position = Array.FindIndex(members, value => value.Id == sample.Id);
            burstText = $"  ·  burst frame {position + 1}/{members.Length}";
        }
        candidateInfo.Text = $"{Path.GetFileName(source.Path)}  ·  {TimeSpan.FromMilliseconds(sample.TimestampMs):hh\\:mm\\:ss\\.fff}  ·  {source.Split}" +
            burstText;
        positive.Enabled = negative.Enabled = reject.Enabled = true;
        burst.Enabled = sample.BurstId == null;
        status.Text = "Accept and label, mark as a negative example, or reject.";
        StartNextFramePrefetch(sample);
    }

    void StartNextFramePrefetch(TrainingSample sample)
    {
        int minimumResolution = SelectedMinimumResolution();
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(prefetchCancellation.Token);
        lock (prefetchCancellationGate)
        {
            nextFramePrefetchCancellation?.Cancel();
            nextFramePrefetchCancellation = cancellation;
        }
        nextFramePrefetch = Task.Run(() => PrefetchNextFrameAsync(sample,
            minimumResolution, cancellation.Token)).ContinueWith(_ =>
            {
                lock (prefetchCancellationGate)
                    if (ReferenceEquals(nextFramePrefetchCancellation, cancellation))
                        nextFramePrefetchCancellation = null;
                cancellation.Dispose();
            }, TaskScheduler.Default);
    }

    bool IsBurstAnchor(TrainingSample sample)
    {
        if (store == null || sample.BurstId == null) return false;
        TrainingSample[] members = store.Dataset.Samples.Where(value =>
                value.BurstId == sample.BurstId)
            .OrderBy(value => value.BurstIndex).ToArray();
        return members.Length > 0 &&
            members[members.Length / 2].Id == sample.Id;
    }

    async Task AcceptPositiveAsync()
    {
        if (store == null || current == null) return;
        var draft = store.LoadDraftAnnotation(current);
        using AnnotationForm editor = new(store.Resolve(current.FramePath!), draft.Ids,
            draft.Objects, draft.Provenance,
            (ids, objects, provenance) => store.SaveDraftAnnotation(current, ids, objects, provenance),
            null, threshold => GenerateRvmPreviewAsync(current, threshold, CancellationToken.None),
            annotationSam2.LoadAsync);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        TrainingSample accepted = current;
        int[] acceptedIds = editor.ObjectIds;
        AnnotationObject[] acceptedObjects = [.. editor.AnnotationObjects];
        try { await Task.Run(() => store.Accept(accepted, acceptedIds, acceptedObjects, editor.Provenance)); }
        catch (Exception error) { status.Text = "Could not accept this frame: " + error.Message; return; }
        ClearCurrent(); UpdateStats();
        _ = Task.Run(() => GenerateDerivedArtifactsAsync(accepted, editor.RvmThreshold));
        UpdateUndoDecision();
        if (accepted.BurstId != null && IsBurstAnchor(accepted))
            await PropagateBurstDraftsAsync(accepted, acceptedIds, acceptedObjects);
        await NextAsync();
    }

    async Task PrefetchNextFrameAsync(TrainingSample editing, int minimumResolution,
        CancellationToken token)
    {
        if (store == null) return;
        try
        {
            int generation = PreviewGeneration();
            HashSet<string> reserved = CachedPreviewIds(); reserved.Add(editing.Id);
            if (CachedPreviewCount() == 0)
            {
                SetQueueDecoding(generation, true);
                try
                {
                    TrainingSample? nextSample = store.NextCandidate(editing.BurstId,
                        minimumResolution, reserved);
                    if (nextSample?.FramePath == null || !File.Exists(store.Resolve(nextSample.FramePath)))
                    {
                        if (nextSample != null) await store.ExtractDraftFrameAsync(nextSample, token);
                    }
                    if (nextSample != null)
                    {
                        token.ThrowIfCancellationRequested();
                        reserved.Add(nextSample.Id); CachePreview(nextSample, generation);
                    }
                }
                finally { SetQueueDecoding(generation, false); }
            }
            CancellationTokenSource fill;
            lock (prefetchCancellationGate)
            {
                if (queueFillCancellation is { IsCancellationRequested: false }) return;
                fill = CancellationTokenSource.CreateLinkedTokenSource(token);
                queueFillCancellation = fill;
            }
            _ = FillPrefetchQueueAsync(editing.BurstId, minimumResolution, reserved, generation, fill.Token)
                .ContinueWith(_ =>
                {
                    lock (prefetchCancellationGate)
                        if (ReferenceEquals(queueFillCancellation, fill)) queueFillCancellation = null;
                    fill.Dispose();
                }, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { /* Prefetch is opportunistic; ordinary Next handles and reports failures. */ }
    }

    void StopPrefetchWork()
    {
        lock (prefetchCancellationGate)
        {
            nextFramePrefetchCancellation?.Cancel();
            queueFillCancellation?.Cancel();
        }
        nextFramePrefetch = Task.CompletedTask;
    }

    async Task FillPrefetchQueueAsync(string? preferredBurstId, int minimumResolution,
        HashSet<string> reserved, int generation, CancellationToken token)
    {
        if (store == null) return;
        try { await prefetchFillGate.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        try
        {
            SetQueueDecoding(generation, true);
            while (CachedPreviewCount() < 10)
            {
                token.ThrowIfCancellationRequested();
                TrainingSample? sample = store.NextCandidate(preferredBurstId,
                    minimumResolution, reserved);
                if (sample == null) return;
                reserved.Add(sample.Id);
                if (sample.FramePath == null || !File.Exists(store.Resolve(sample.FramePath)))
                    await store.ExtractDraftFrameAsync(sample, token);
                token.ThrowIfCancellationRequested();
                CachePreview(sample, generation);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { /* The foreground workflow can decode missing frames normally. */ }
        finally { SetQueueDecoding(generation, false); prefetchFillGate.Release(); }
    }

    int CachedPreviewCount()
    {
        lock (previewCacheGate) return previewCache.Count;
    }

    HashSet<string> CachedPreviewIds()
    {
        lock (previewCacheGate) return [.. previewCache.Keys];
    }

    int PreviewGeneration()
    {
        lock (previewCacheGate) return previewGeneration;
    }

    void ClearPrefetchQueue()
    {
        StopPrefetchWork();
        lock (previewCacheGate)
        {
            previewGeneration++;
            queueIsDecoding = false;
            foreach (Bitmap bitmap in previewCache.Values) bitmap.Dispose();
            previewCache.Clear();
            previewSamples.Clear(); previewOrder.Clear();
        }
        RefreshQueueStatus();
    }

    void CachePreview(TrainingSample sample, int generation)
    {
        if (store == null || sample.FramePath == null) return;
        lock (previewCacheGate)
            if (generation != previewGeneration || previewCache.ContainsKey(sample.Id)) return;
        Bitmap bitmap = LoadUnlockedImage(store.Resolve(sample.FramePath));
        lock (previewCacheGate)
        {
            if (generation != previewGeneration || previewCache.Count >= 10 ||
                previewCache.ContainsKey(sample.Id)) bitmap.Dispose();
            else
            {
                previewCache.Add(sample.Id, bitmap);
                previewSamples.Add(sample.Id, sample);
                previewOrder.Enqueue(sample.Id);
            }
        }
        RefreshQueueStatus();
    }

    Bitmap? TakeCachedPreview(string sampleId)
    {
        lock (previewCacheGate)
        {
            if (!previewCache.Remove(sampleId, out Bitmap? bitmap)) return null;
            previewSamples.Remove(sampleId);
            RefreshQueueStatus();
            return bitmap;
        }
    }

    (TrainingSample Sample, Bitmap Preview)? TakeNextCachedPreview()
    {
        lock (previewCacheGate)
        {
            while (previewOrder.TryDequeue(out string? sampleId))
            {
                if (!previewCache.Remove(sampleId, out Bitmap? bitmap) ||
                    !previewSamples.Remove(sampleId, out TrainingSample? sample)) continue;
                RefreshQueueStatus();
                return (sample, bitmap);
            }
            return null;
        }
    }

    void SetQueueDecoding(int generation, bool value)
    {
        lock (previewCacheGate)
        {
            if (generation != previewGeneration) return;
            queueIsDecoding = value;
        }
        RefreshQueueStatus();
    }

    void RefreshQueueStatus()
    {
        int count = CachedPreviewCount();
        string text = $"Queue: {count}/10" + (queueIsDecoding ? " · decoding…" : "");
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => queueStatus.Text = text);
        else queueStatus.Text = text;
    }

    async Task AcceptNegativeAsync()
    {
        if (store == null || current == null) return;
        TrainingSample accepted = current;
        try
        {
            await Task.Run(() => store.Accept(accepted,
                new int[accepted.Width * accepted.Height], [], "confirmed-negative"));
        }
        catch (Exception error) { status.Text = "Could not accept this frame: " + error.Message; return; }
        ClearCurrent(); UpdateStats();
        _ = Task.Run(() => GenerateDerivedArtifactsAsync(accepted));
        await NextAsync();
        UpdateUndoDecision();
    }

    internal static Bitmap LoadUnlockedImage(string path)
    {
        using Image opened = Image.FromFile(path);
        return new Bitmap(opened);
    }

    async Task RejectAsync()
    {
        if (store == null || current == null) return;
        TrainingSample rejected = current;
        await Task.Run(() => store.Reject(rejected));
        ClearCurrent(); await NextAsync(); UpdateStats();
        UpdateUndoDecision();
    }

    void UpdateUndoDecision()
    {
        TrainingSample? sample = store?.MostRecentDecision();
        undoDecision.Enabled = sample?.Decision is "positive" or "negative";
        undoDecision.Text = sample == null ? "Undo last decision" :
            $"Undo last: {sample.Decision}";
    }

    async Task UndoLastDecisionAsync()
    {
        if (store?.MostRecentDecision() is not TrainingSample sample ||
            sample.Decision is not ("positive" or "negative")) return;
        undoDecision.Enabled = false;
        await derivationGate.WaitAsync();
        try
        {
            store.UndoDecision(sample); activeBurstId = sample.BurstId;
            ClearPrefetchQueue(); ClearCurrent(); UpdateStats(); UpdateUndoDecision();
            status.Text = $"Undid {sample.Id}; restored it to the review queue.";
            ShowSample(sample);
        }
        catch (Exception error) { status.Text = "Could not undo decision: " + error.Message; }
        finally { derivationGate.Release(); UpdateUndoDecision(); }
    }

    async Task BurstAsync()
    {
        if (store == null || current == null) return;
        TrainingSample anchor = current;
        await BusyAsync(async token =>
        {
            IReadOnlyList<TrainingSample> frames = await store.CreateBurstAsync(anchor, token);
            ClearPrefetchQueue(); activeBurstId = anchor.BurstId;
            status.Text = $"Created a {frames.Count}-frame review burst. Every frame must be reviewed.";
            UpdateStats(); ShowSample(anchor);
        });
    }

    async Task GenerateDerivedArtifactsAsync(TrainingSample sample, double rvmThreshold = .4)
    {
        if (store == null) return;
        string? rvmRevision = null;
        await derivationGate.WaitAsync();
        try
        {
            string sampleFolder = Path.GetDirectoryName(store.Resolve(sample.FramePath!))!;
            string person = Path.Combine(sampleFolder, "rvm-person-mask.png");
            string desired = Path.Combine(sampleFolder, "desired-foreground.png");
            string python = Sam2Client.RuntimePython();
            string runtime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(python)!, "..", ".."));
            string revisionFile = Path.Combine(runtime, "RVM_COMMIT");
            if (File.Exists(revisionFile)) rvmRevision = File.ReadAllText(revisionFile).Trim();
            await GenerateRvmPersonMaskAsync(sample, rvmThreshold, CancellationToken.None);
            using Bitmap personBitmap = new(person);
            byte[] personPixels = BinaryPixels(personBitmap);
            using Bitmap prop = new(store.Resolve(sample.PropMaskPath!));
            byte[] propPixels = BinaryPixels(prop);
            byte[] union = personPixels.Zip(propPixels, (first, second) =>
                first != 0 || second != 0 ? (byte)255 : (byte)0).ToArray();
            PngMaskWriter.SaveGray8(desired, sample.Width, sample.Height, union);
            store.SetDerivation(sample, "ready", store.Relative(person), store.Relative(desired), null,
                rvmRevision, rvmThreshold);
        }
        catch (Exception error) { store.SetDerivation(sample, "failed", null, null, error.Message,
            rvmRevision, rvmThreshold); }
        finally { derivationGate.Release(); if (!IsDisposed) BeginInvoke(UpdateStats); }
    }

    async Task<string> GenerateRvmPreviewAsync(TrainingSample sample, double threshold,
        CancellationToken token)
    {
        if (store == null) throw new InvalidOperationException("Dataset is not open.");
        string folder = Path.GetDirectoryName(store.Resolve(sample.FramePath!))!;
        string preview = Path.Combine(folder, $"rvm-preview-{Math.Round(threshold * 100):00}.png");
        VideoSource source = store.Source(sample.SourceId);
        return await rvmPreview.GenerateAsync(source.Path, sample.TimestampMs, threshold, preview, token);
    }

    async Task<string> GenerateRvmPersonMaskAsync(TrainingSample sample, double threshold,
        CancellationToken token)
    {
        if (store == null) throw new InvalidOperationException("Dataset is not open.");
        string folder = Path.GetDirectoryName(store.Resolve(sample.FramePath!))!;
        string person = Path.Combine(folder, "rvm-person-mask.png");
        string review = Path.Combine(folder, "rvm-person-review.png");
        VideoSource source = store.Source(sample.SourceId);
        await rvmPreview.GenerateAsync(source.Path, sample.TimestampMs, threshold, review, token);
        {
            using Bitmap opened = new(review);
            using Bitmap resized = new(sample.Width, sample.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.DrawImage(opened, new Rectangle(Point.Empty, resized.Size));
            }
            PngMaskWriter.SaveGray8(person, sample.Width, sample.Height, BinaryPixels(resized));
        }
        File.Delete(review);
        return person;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ = rvmPreview.DisposeAsync().AsTask();
        _ = annotationSam2.DisposeAsync().AsTask(); derivationGate.Dispose();
        base.OnFormClosed(e);
    }

    static byte[] BinaryPixels(Bitmap bitmap)
    {
        using Bitmap converted = new(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(converted)) graphics.DrawImageUnscaled(bitmap, 0, 0);
        BitmapData data = converted.LockBits(new Rectangle(0, 0, converted.Width, converted.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] result = new byte[converted.Width * converted.Height]; int[] row = new int[converted.Width];
        try
        {
            for (int y = 0; y < converted.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < row.Length; x++) result[y * converted.Width + x] =
                    ((row[x] >> 16) & 255) >= 128 ? (byte)255 : (byte)0;
            }
        }
        finally { converted.UnlockBits(data); }
        return result;
    }

    async Task PropagateBurstDraftsAsync(TrainingSample anchor, int[] anchorIds,
        IReadOnlyList<AnnotationObject> objects)
    {
        if (store == null || anchor.BurstId == null || objects.Count == 0) return;
        TrainingSample[] drafts = store.Dataset.Samples.Where(value =>
            value.BurstId == anchor.BurstId && value.Decision == "draft" &&
            value.FramePath != null).ToArray();
        if (drafts.Length == 0) return;
        string temporary = Path.Combine(Path.GetTempPath(),
            "iqp-training-burst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            status.Text = "Creating SAM2 burst annotation drafts…";
            string firstFrame = store.Resolve(drafts[0].FramePath!);
            await using Sam2Client client = new(firstFrame, temporary);
            await client.StartAsync(CancellationToken.None);
            int anchorIndex = anchor.BurstIndex ?? 2;
            foreach (int direction in new[] { -1, 1 })
            {
                Dictionary<int, string> seeds = [];
                foreach (AnnotationObject item in objects)
                {
                    string seed = Path.Combine(temporary, $"seed-{direction}-{item.Id}-anchor.png");
                    byte[] pixels = anchorIds.Select(value => value == item.Id ? (byte)255 : (byte)0).ToArray();
                    PngMaskWriter.SaveGray8(seed, anchor.Width, anchor.Height, pixels); seeds[item.Id] = seed;
                }
                IEnumerable<TrainingSample> ordered = drafts.Where(value =>
                    Math.Sign(value.BurstIndex.GetValueOrDefault(anchorIndex) -
                        anchorIndex) == direction)
                    .OrderBy(value => Math.Abs(value.BurstIndex.GetValueOrDefault(
                        anchorIndex) - anchorIndex));
                foreach (TrainingSample sample in ordered)
                {
                    await client.LoadAsync(store.Resolve(sample.FramePath!), CancellationToken.None);
                    int[] propagated = new int[sample.Width * sample.Height];
                    foreach (AnnotationObject item in objects)
                    {
                        await client.GenerateAsync([], [], null, seeds[item.Id], CancellationToken.None);
                        using Bitmap mask = new(client.MaskPath);
                        byte[] pixels = BinaryPixels(mask);
                        for (int index = 0; index < pixels.Length; index++)
                            if (pixels[index] != 0) propagated[index] = item.Id;
                        string nextSeed = Path.Combine(temporary,
                            $"seed-{direction}-{item.Id}-{sample.BurstIndex}.png");
                        File.Copy(client.MaskPath, nextSeed, true); seeds[item.Id] = nextSeed;
                    }
                    store.SaveDraftAnnotation(sample, propagated, objects, "sam2-burst-draft");
                }
            }
            status.Text = "SAM2 burst drafts are ready; review every neighbouring frame.";
        }
        catch (Exception error)
        {
            status.Text = "Burst frames are queued for manual review; SAM2 propagation was unavailable: " + error.Message;
        }
        finally { try { Directory.Delete(temporary, true); } catch { } UpdateStats(); }
    }

    async Task TrainAsync()
    {
        if (store == null) return;
        int minimum = SelectedMinimumResolution();
        int inputSize = trainingSize.SelectedItem is int selectedSize ? selectedSize : 512;
        string negativeMode = negativeSelection.SelectedItem is NegativeOption selectedNegatives
            ? selectedNegatives.Value : "compare";
        TrainingSample[] eligible = store.Dataset.Samples.Where(sample =>
            sample.Decision is "positive" or "negative" &&
            (minimum == 0 || Math.Min(sample.Width, sample.Height) >= minimum)).ToArray();
        DatasetStatistics value = store.Statistics();
        List<string> warnings = [];
        int eligibleNegatives = eligible.Count(sample => sample.Decision == "negative");
        double eligibleNegativeRatio = eligibleNegatives / (double)Math.Max(1, eligible.Length);
        if (eligible.Length < 300) warnings.Add($"only {eligible.Length} eligible accepted frames (300 recommended)");
        if (eligibleNegativeRatio < .25) warnings.Add($"only {eligibleNegativeRatio:P0} eligible negatives (25–35% recommended)");
        if (new[] { "train", "validation", "test" }.Any(split =>
            !eligible.Any(sample => sample.Split == split)))
            warnings.Add("one or more source-level splits are empty after the resolution filter");
        if (warnings.Count > 0 && MessageBox.Show(this, "Training readiness warning:\n\n• " +
            string.Join("\n• ", warnings) + "\n\nTrain anyway?", Text, MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) != DialogResult.Yes) return;
        trainingCancellation = new(); runner = new TrainingRunner();
        runner.Message += value => BeginInvoke(() =>
        {
            trainingLog.AppendText(value + Environment.NewLine); trainingLog.SelectionStart = trainingLog.TextLength;
            trainingLog.ScrollToCaret();
        });
        train.Enabled = false; cancelTraining.Enabled = true; install.Enabled = openReview.Enabled = false;
        installPackagePath = null;
        try
        {
            int exit = await runner.RunAsync(store.Root, resumeTraining.Checked, minimum, inputSize,
                negativeMode,
                trainingCancellation.Token);
            status.Text = exit == 0 ? "Training and evaluation completed." : "Training failed; see the log.";
            installPackagePath = exit == 0 && Directory.Exists(runner.PackagePath)
                ? runner.PackagePath : null;
            install.Enabled = installPackagePath != null;
            openReview.Enabled = exit == 0 && Directory.Exists(runner.ReviewPath);
        }
        catch (OperationCanceledException) { status.Text = "Training cancelled; the last checkpoint can be resumed."; }
        catch (Exception error) { status.Text = "Training failed: " + error.Message; }
        finally
        {
            train.Enabled = true; cancelTraining.Enabled = false;
            openReview.Enabled = store != null && ErrorReviewForm.FindLatest(store.Root) != null;
        }
    }

    void InstallModel()
    {
        if (installPackagePath == null) return;
        try
        {
            string modelId = TrainingRunner.InstallPackage(installPackagePath);
            MessageBox.Show(this, $"Installed model {modelId}. Enable it explicitly in QuickPlayer Custom Show settings.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information); install.Enabled = false;
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async Task OpenErrorReviewAsync()
    {
        if (store == null) return;
        string? reviewPath = runner?.ReviewPath;
        if (reviewPath == null || !File.Exists(Path.Combine(reviewPath, "review.json")))
            reviewPath = ErrorReviewForm.FindLatest(store.Root);
        if (reviewPath == null) return;
        using ErrorReviewForm dialog = new(reviewPath);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Choice is not ErrorReviewChoice choice) return;
        TrainingSample? sample = store.Dataset.Samples.FirstOrDefault(value => value.Id == choice.SampleId);
        if (sample == null)
        {
            status.Text = "That review sample no longer exists in this dataset."; return;
        }
        if (sample.Decision is not ("positive" or "negative" or "draft"))
        {
            status.Text = "That review sample cannot be reopened."; return;
        }
        await BusyAsync(async token =>
        {
            await derivationGate.WaitAsync(token);
            try
            {
                if (sample.Decision is "positive" or "negative") store.UndoDecision(sample);
                store.MarkFeedbackPriority(sample);
            }
            finally { derivationGate.Release(); }
            if (choice.CreateBurst) await store.CreateBurstAsync(sample, token, forceNew: true);
            ClearPrefetchQueue(); activeBurstId = sample.BurstId;
            ClearCurrent(); UpdateStats(); UpdateUndoDecision();
            ShowSample(sample);
            status.Text = choice.CreateBurst
                ? "Error reopened with a five-frame feedback burst; correct and review every frame."
                : "Error reopened; correct its label and accept it again.";
        });
    }

    void UpdateStats()
    {
        if (store == null) { stats.Text = "Open or create a dataset."; return; }
        DatasetStatistics value = store.Statistics(); int target = 500;
        int desiredNegatives = (int)Math.Round(target * .30), desiredPositives = target - desiredNegatives;
        string readiness = value.Accepted >= 300 && value.NegativeRatio >= .25 &&
            value.Train > 0 && value.Validation > 0 && value.Test > 0 ? "Ready for a pilot training run" : "More data recommended";
        string negativeMode = negativeSelection.SelectedItem is NegativeOption selectedNegatives
            ? selectedNegatives.Value : "compare";
        string balanceNote = negativeMode switch
        {
            "compare" => "\r\nTraining will compare reproducible 20%, 25%, 30%, and 35% negative subsets.",
            "all" => "\r\nTraining will use every eligible negative sample.",
            _ => $"\r\nTraining will use a reproducible {negativeMode}% negative subset."
        };
        int minimum = SelectedMinimumResolution();
        int eligible = store.Dataset.Samples.Count(sample => sample.Decision is "positive" or "negative" &&
            (minimum == 0 || Math.Min(sample.Width, sample.Height) >= minimum));
        string resolutionNote = minimum == 0 ? "" : $"  ·  eligible at {minimum}p+: {eligible:N0}";
        stats.Text = $"{readiness}\r\n\r\nAccepted: {value.Accepted:N0}  ·  positive: {value.Positive:N0}  ·  negative: {value.Negative:N0} ({value.NegativeRatio:P1}){resolutionNote}\r\n" +
            $"Rejected: {value.Rejected:N0}  ·  drafts: {value.Draft:N0}  ·  objects: {value.Objects:N0}\r\n" +
            $"Videos: {value.CoveredVideos:N0}/{value.Videos:N0} covered  ·  train/validation/test: {value.Train:N0}/{value.Validation:N0}/{value.Test:N0}\r\n" +
            $"Toward {target}: {Math.Max(0, desiredPositives - value.Positive):N0} positives and {Math.Max(0, desiredNegatives - value.Negative):N0} negatives remaining.{balanceNote}";
    }

    int SelectedMinimumResolution() => minimumResolution.SelectedItem is ResolutionOption option
        ? option.Pixels : 0;

    sealed record ResolutionOption(string Label, int Pixels)
    {
        public override string ToString() => Label;
    }

    sealed record NegativeOption(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    async Task BusyAsync(Func<CancellationToken, Task> action)
    {
        operationCancellation?.Cancel(); operationCancellation = new(); UseWaitCursor = true; next.Enabled = false;
        try { await action(operationCancellation.Token); }
        catch (OperationCanceledException) { status.Text = "Cancelled."; }
        catch (Exception error) { status.Text = error.Message; }
        finally { UseWaitCursor = false; next.Enabled = true; }
    }

    void ClearCurrent()
    {
        current = null; preview.Image?.Dispose(); preview.Image = null; candidateInfo.Text = "";
        positive.Enabled = negative.Enabled = reject.Enabled = burst.Enabled = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        operationCancellation?.Cancel(); trainingCancellation?.Cancel(); StopPrefetchWork();
        prefetchCancellation.Cancel(); runner?.Cancel();
        preview.Image?.Dispose(); base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            prefetchCancellation.Dispose();
            ClearPrefetchQueue();
        }
        base.Dispose(disposing);
    }
}
