using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class TrainingStudioForm : Form
{
    readonly TextBox datasetPath = new() { Width = 430 };
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
    readonly Button burst = new() { Text = "Create 9-frame burst", AutoSize = true, Enabled = false };
    readonly Button train = new() { Text = "Train model", AutoSize = true };
    readonly Button cancelTraining = new() { Text = "Cancel training", AutoSize = true, Enabled = false };
    readonly Button install = new() { Text = "Install trained model", AutoSize = true, Enabled = false };
    readonly Button openReview = new() { Text = "Open error review", AutoSize = true, Enabled = false };
    readonly CheckBox resumeTraining = new() { Text = "Resume latest checkpoint", AutoSize = true,
        Padding = new(6, 7, 0, 0) };
    readonly SemaphoreSlim derivationGate = new(1, 1);
    DatasetStore? store;
    TrainingSample? current;
    CancellationTokenSource? operationCancellation, trainingCancellation;
    TrainingRunner? runner;

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
            datasetPath, openDataset, indexFolder, next, positive, negative, reject, burst]);

        FlowLayoutPanel trainActions = new() { Dock = DockStyle.Top, Height = 42 };
        trainActions.Controls.AddRange([train, resumeTraining, cancelTraining, openReview, install]);
        Panel right = new() { Dock = DockStyle.Fill };
        Panel dashboard = new() { Dock = DockStyle.Top, Height = 210, Padding = new(8) };
        dashboard.Controls.Add(stats); dashboard.Controls.Add(new Label
        { Text = "Dataset dashboard", Dock = DockStyle.Top, Height = 28, Font = new Font(Font, FontStyle.Bold) });
        right.Controls.Add(trainingLog); right.Controls.Add(trainActions); right.Controls.Add(dashboard);

        Panel left = new() { Dock = DockStyle.Fill };
        left.Controls.Add(preview); left.Controls.Add(candidateInfo);
        SplitContainer split = new() { Dock = DockStyle.Fill, SplitterDistance = 900 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        Controls.Add(split); Controls.Add(top); Controls.Add(status);

        openDataset.Click += (_, _) => ChooseDataset();
        indexFolder.Click += async (_, _) => await IndexFolderAsync();
        next.Click += async (_, _) => await NextAsync();
        positive.Click += async (_, _) => await AcceptPositiveAsync();
        negative.Click += async (_, _) => await AcceptNegativeAsync();
        reject.Click += async (_, _) => await RejectAsync();
        burst.Click += async (_, _) => await BurstAsync();
        train.Click += async (_, _) => await TrainAsync();
        cancelTraining.Click += (_, _) => { trainingCancellation?.Cancel(); runner?.Cancel(); };
        openReview.Click += (_, _) => OpenErrorReview();
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
        try { store = new DatasetStore(path); UpdateStats(); status.Text = "Dataset ready."; }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async Task IndexFolderAsync()
    {
        if (store == null) return;
        using FolderBrowserDialog dialog = new() { Description = "Choose a folder to scan recursively for videos",
            UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await BusyAsync(async token =>
        {
            int added = await store.IndexFolderAsync(dialog.SelectedPath,
                new Progress<string>(value => status.Text = value), token);
            status.Text = $"Indexed {added:N0} new video(s)."; UpdateStats();
        });
    }

    async Task NextAsync()
    {
        if (store == null) return;
        await BusyAsync(async token =>
        {
            TrainingSample? sample = store.NextCandidate();
            if (sample == null) { status.Text = "No unseen eligible frame remains. Index more videos."; return; }
            if (sample.FramePath == null || !File.Exists(store.Resolve(sample.FramePath)))
                await store.ExtractDraftFrameAsync(sample, token);
            ShowSample(sample); await Task.CompletedTask;
        });
    }

    void ShowSample(TrainingSample sample)
    {
        current = sample; preview.Image?.Dispose();
        preview.Image = LoadUnlockedImage(store!.Resolve(sample.FramePath!));
        VideoSource source = store.Source(sample.SourceId);
        candidateInfo.Text = $"{Path.GetFileName(source.Path)}  ·  {TimeSpan.FromMilliseconds(sample.TimestampMs):hh\\:mm\\:ss\\.fff}  ·  {source.Split}" +
            (sample.BurstId == null ? "" : $"  ·  burst frame {sample.BurstIndex! + 1}/9");
        positive.Enabled = negative.Enabled = reject.Enabled = true;
        burst.Enabled = sample.BurstId == null;
        status.Text = "Accept and label, mark as a negative example, or reject.";
    }

    async Task AcceptPositiveAsync()
    {
        if (store == null || current == null) return;
        var draft = store.LoadDraftAnnotation(current);
        using AnnotationForm editor = new(store.Resolve(current.FramePath!), draft.Ids,
            draft.Objects, draft.Provenance,
            (ids, objects, provenance) => store.SaveDraftAnnotation(current, ids, objects, provenance));
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        TrainingSample accepted = current;
        int[] acceptedIds = editor.ObjectIds;
        AnnotationObject[] acceptedObjects = [.. editor.AnnotationObjects];
        try { store.Accept(accepted, acceptedIds, acceptedObjects, editor.Provenance); }
        catch (Exception error) { status.Text = "Could not accept this frame: " + error.Message; return; }
        ClearCurrent(); UpdateStats(); _ = GenerateDerivedArtifactsAsync(accepted);
        if (accepted.BurstId != null)
            await PropagateBurstDraftsAsync(accepted, acceptedIds, acceptedObjects);
        await NextAsync();
    }

    async Task AcceptNegativeAsync()
    {
        if (store == null || current == null) return;
        TrainingSample accepted = current;
        try { store.Accept(accepted, new int[accepted.Width * accepted.Height], [], "confirmed-negative"); }
        catch (Exception error) { status.Text = "Could not accept this frame: " + error.Message; return; }
        ClearCurrent(); UpdateStats(); _ = GenerateDerivedArtifactsAsync(accepted); await NextAsync();
    }

    internal static Bitmap LoadUnlockedImage(string path)
    {
        using Image opened = Image.FromFile(path);
        return new Bitmap(opened);
    }

    async Task RejectAsync()
    {
        if (store == null || current == null) return;
        store.Reject(current); ClearCurrent(); UpdateStats(); await NextAsync();
    }

    async Task BurstAsync()
    {
        if (store == null || current == null) return;
        TrainingSample anchor = current;
        await BusyAsync(async token =>
        {
            IReadOnlyList<TrainingSample> frames = await store.CreateBurstAsync(anchor, token);
            status.Text = $"Created a {frames.Count}-frame review burst. Every frame must be reviewed.";
            UpdateStats(); ShowSample(anchor);
        });
    }

    async Task GenerateDerivedArtifactsAsync(TrainingSample sample)
    {
        if (store == null) return;
        const double rvmThreshold = .4;
        string? rvmRevision = null;
        await derivationGate.WaitAsync();
        try
        {
            VideoSource source = store.Source(sample.SourceId);
            string sampleFolder = Path.GetDirectoryName(store.Resolve(sample.FramePath!))!;
            string review = Path.Combine(sampleFolder, "rvm-person-review.png");
            string person = Path.Combine(sampleFolder, "rvm-person-mask.png");
            string desired = Path.Combine(sampleFolder, "desired-foreground.png");
            string python = Sam2Client.RuntimePython();
            string worker = Path.Combine(AppContext.BaseDirectory, "custom-shows", "rvm_initial_mask_worker.py");
            string runtime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(python)!, "..", ".."));
            string revisionFile = Path.Combine(runtime, "RVM_COMMIT");
            if (File.Exists(revisionFile)) rvmRevision = File.ReadAllText(revisionFile).Trim();
            await DatasetStore.RunAsync(python, [worker, "--source", source.Path, "--runtime", runtime,
                "--mask", review, "--frame-ms", sample.TimestampMs.ToString(CultureInfo.InvariantCulture),
                "--alpha-threshold", rvmThreshold.ToString(CultureInfo.InvariantCulture)], CancellationToken.None);
            using Bitmap opened = new(review);
            using Bitmap resized = new(sample.Width, sample.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.DrawImage(opened, new Rectangle(0, 0, resized.Width, resized.Height));
            }
            byte[] personPixels = BinaryPixels(resized); PngMaskWriter.SaveGray8(person,
                sample.Width, sample.Height, personPixels);
            using Bitmap prop = new(store.Resolve(sample.PropMaskPath!));
            byte[] propPixels = BinaryPixels(prop);
            byte[] union = personPixels.Zip(propPixels, (first, second) =>
                first != 0 || second != 0 ? (byte)255 : (byte)0).ToArray();
            PngMaskWriter.SaveGray8(desired, sample.Width, sample.Height, union);
            File.Delete(review);
            store.SetDerivation(sample, "ready", store.Relative(person), store.Relative(desired), null,
                rvmRevision, rvmThreshold);
        }
        catch (Exception error) { store.SetDerivation(sample, "failed", null, null, error.Message,
            rvmRevision, rvmThreshold); }
        finally { derivationGate.Release(); if (!IsDisposed) BeginInvoke(UpdateStats); }
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
                    Math.Sign(value.BurstIndex.GetValueOrDefault(4) - 4) == direction)
                    .OrderBy(value => Math.Abs(value.BurstIndex.GetValueOrDefault(4) - 4));
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
        DatasetStatistics value = store.Statistics();
        List<string> warnings = [];
        if (value.Accepted < 300) warnings.Add($"only {value.Accepted} accepted frames (300 recommended)");
        if (value.NegativeRatio is < .25 or > .35) warnings.Add($"{value.NegativeRatio:P0} negatives (25–35% recommended)");
        if (value.Train == 0 || value.Validation == 0 || value.Test == 0) warnings.Add("one or more source-level splits are empty");
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
        try
        {
            int exit = await runner.RunAsync(store.Root, resumeTraining.Checked, trainingCancellation.Token);
            status.Text = exit == 0 ? "Training and evaluation completed." : "Training failed; see the log.";
            install.Enabled = exit == 0 && Directory.Exists(runner.PackagePath);
            openReview.Enabled = exit == 0 && Directory.Exists(runner.ReviewPath);
        }
        catch (OperationCanceledException) { status.Text = "Training cancelled; the last checkpoint can be resumed."; }
        catch (Exception error) { status.Text = "Training failed: " + error.Message; }
        finally { train.Enabled = true; cancelTraining.Enabled = false; }
    }

    void InstallModel()
    {
        if (runner?.PackagePath == null) return;
        try
        {
            string modelId = TrainingRunner.InstallPackage(runner.PackagePath);
            MessageBox.Show(this, $"Installed model {modelId}. Enable it explicitly in QuickPlayer Custom Show settings.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information); install.Enabled = false;
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void OpenErrorReview()
    {
        if (runner?.ReviewPath == null || !Directory.Exists(runner.ReviewPath)) return;
        Process.Start(new ProcessStartInfo(runner.ReviewPath) { UseShellExecute = true });
    }

    void UpdateStats()
    {
        if (store == null) { stats.Text = "Open or create a dataset."; return; }
        DatasetStatistics value = store.Statistics(); int target = 500;
        int desiredNegatives = (int)Math.Round(target * .30), desiredPositives = target - desiredNegatives;
        string readiness = value.Accepted >= 300 && value.NegativeRatio is >= .25 and <= .35 &&
            value.Train > 0 && value.Validation > 0 && value.Test > 0 ? "Ready for a pilot training run" : "More data recommended";
        stats.Text = $"{readiness}\r\n\r\nAccepted: {value.Accepted:N0}  ·  positive: {value.Positive:N0}  ·  negative: {value.Negative:N0} ({value.NegativeRatio:P1})\r\n" +
            $"Rejected: {value.Rejected:N0}  ·  drafts: {value.Draft:N0}  ·  objects: {value.Objects:N0}\r\n" +
            $"Videos: {value.CoveredVideos:N0}/{value.Videos:N0} covered  ·  train/validation/test: {value.Train:N0}/{value.Validation:N0}/{value.Test:N0}\r\n" +
            $"Toward {target}: {Math.Max(0, desiredPositives - value.Positive):N0} positives and {Math.Max(0, desiredNegatives - value.Negative):N0} negatives remaining.";
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
        operationCancellation?.Cancel(); trainingCancellation?.Cancel(); runner?.Cancel();
        preview.Image?.Dispose(); base.OnFormClosing(e);
    }
}
