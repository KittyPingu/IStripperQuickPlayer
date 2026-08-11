using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomStabiloStabilizationForm : Form
{
    sealed record VideoInfo(int Width, int Height, long DurationMs, string FrameRate);
    sealed record StabiloSettings(string Detector, string Transformation,
        double Downsample, string Border);

    readonly CustomShowConfiguration configuration;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-stabilo-" + Guid.NewGuid().ToString("N"));
    readonly TextBox sourcePath = new() { Dock = DockStyle.Fill };
    readonly TextBox outputPath = new() { Dock = DockStyle.Fill };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
        AccessibleName = "Representative source frame" };
    readonly ComboBox preset = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 255 };
    readonly ComboBox sam2Model = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 155 };
    readonly ComboBox borders = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180 };
    readonly Label status = new() { AutoSize = true, Text = "Choose a source video." };
    readonly Button run = new() { Text = "Start guided workflow...", AutoSize = true,
        Enabled = false };
    readonly Button review = new() { Text = "Review input vs output...", AutoSize = true,
        Enabled = false };
    Bitmap? frame;
    VideoInfo? sourceInfo;

    string InitialMaskPath => Path.Combine(temporary, "initial-mask.png");
    string MasksPath => Path.Combine(temporary, "sam2-masks");
    string CorrectionStatePath => Path.Combine(temporary, "sam2-corrections.json");
    string ProfileLogPath => Path.Combine(temporary, "sam2-profile.log");
    string SourcePreviewPath => Path.Combine(temporary, "source-preview.jpg");
    string OutputPreviewPath => Path.Combine(temporary, "output-preview.jpg");

    internal CustomStabiloStabilizationForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Camera / Background Lock (Stabilo + SAM2)";
        ClientSize = new Size(1040, 760);
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterParent;

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(10),
            ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        TableLayoutPanel paths = new() { Dock = DockStyle.Fill, AutoSize = true,
            ColumnCount = 3, RowCount = 2 };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        AddPath(paths, 0, "Source video", sourcePath, BrowseSource);
        AddPath(paths, 1, "Output MP4", outputPath, BrowseOutput);
        layout.Controls.Add(paths, 0, 0);
        layout.Controls.Add(preview, 0, 1);
        layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 5),
            Text = "Guided workflow:  1. choose the performer and reference frame   →   " +
                "2. review/correct SAM2 tracking   →   3. Stabilo locks the background.\n" +
                "During processing, Step 1 analyzes background motion and Step 2 renders the video. " +
                "The source is never changed." }, 0, 2);

        preset.Items.AddRange(["Balanced GPU (XFeat + affine - recommended)",
            "Fast CPU (ORB + affine)", "Strong perspective GPU (XFeat + projective)"]);
        preset.SelectedIndex = 0;
        foreach (string model in CustomShowProcessor.InstalledSam2Models(configuration))
            sam2Model.Items.Add(model switch
            {
                "small" => "Small (balanced)",
                "tiny" => "Tiny (fastest)",
                _ => "Base+ (robust)"
            });
        if (sam2Model.Items.Count > 0)
        {
            int small = sam2Model.Items.Cast<string>().ToList().FindIndex(
                value => value.StartsWith("Small", StringComparison.Ordinal));
            sam2Model.SelectedIndex = small >= 0 ? small : 0;
        }
        borders.Items.AddRange(["Reflected edges (recommended)", "Black edges"]);
        borders.SelectedIndex = 0;
        FlowLayoutPanel options = new() { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        options.Controls.AddRange([OptionLabel("Registration"), preset,
            OptionLabel("SAM2 model"), sam2Model, OptionLabel("Borders"), borders]);
        layout.Controls.Add(options, 0, 3);

        FlowLayoutPanel actions = new() { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight };
        Button close = new() { Text = "Close", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([run, review, close, status]);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        CancelButton = close;
        run.Click += async (_, _) => await RunAsync();
        review.Click += async (_, _) => await ReviewAsync();
        sourcePath.TextChanged += (_, _) => UpdateReviewAvailability();
        outputPath.TextChanged += (_, _) => UpdateReviewAvailability();
        AppTheme.Apply(this);
    }

    static Label OptionLabel(string text) => new() { Text = text, AutoSize = true,
        Padding = new Padding(10, 6, 0, 0) };

    static void AddPath(TableLayoutPanel table, int row, string label, TextBox box,
        EventHandler browse)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true,
            Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, 0, row);
        table.Controls.Add(box, 1, row);
        Button button = new() { Text = "Browse...", Dock = DockStyle.Fill };
        button.Click += browse;
        table.Controls.Add(button, 2, row);
    }

    async void BrowseSource(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new() { Filter =
            "Supported video|*.mp4;*.mov;*.avi;*.mkv;*.webm|MP4 video|*.mp4|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        sourcePath.Text = dialog.FileName;
        outputPath.Text = Path.Combine(Path.GetDirectoryName(dialog.FileName)!,
            Path.GetFileNameWithoutExtension(dialog.FileName) + "-background-locked.mp4");
        await LoadSourceAsync();
    }

    void BrowseOutput(object? sender, EventArgs e)
    {
        using SaveFileDialog dialog = new() { Filter = "MP4 video|*.mp4",
            DefaultExt = "mp4", AddExtension = true, FileName = outputPath.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) outputPath.Text = dialog.FileName;
    }

    async Task LoadSourceAsync()
    {
        try
        {
            run.Enabled = false;
            status.Text = "Reading video information and extracting a preview...";
            Directory.CreateDirectory(temporary);
            using FfmpegCpuDecoder decoder = new(sourcePath.Text, fastDecode: true);
            sourceInfo = new(decoder.Width, decoder.Height,
                (long)Math.Round(decoder.Duration * 1000), decoder.FrameRate);
            await ExtractPreviewAsync(sourcePath.Text, SourcePreviewPath,
                Math.Min(3, decoder.Duration / 2), CancellationToken.None);
            Bitmap next = LoadBitmap(SourcePreviewPath);
            preview.Image = next;
            frame?.Dispose(); frame = next;
            run.Enabled = sam2Model.Items.Count > 0;
            status.Text = run.Enabled
                ? $"Ready: {sourceInfo.Width}×{sourceInfo.Height}, " +
                    $"{FormatDuration(sourceInfo.DurationMs)}."
                : "Install a SAM2 model before using this workflow.";
            UpdateReviewAvailability();
        }
        catch (Exception error)
        {
            sourceInfo = null; run.Enabled = false;
            status.Text = "Could not load the source video.";
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    async Task RunAsync()
    {
        try
        {
            if (!CustomShowProcessor.IsStabiloInstalled(configuration))
                throw new InvalidOperationException(
                    "Stabilo + SAM2 is not installed. Open Custom Shows → " +
                    "Install / Update Processing Tools and select Stabilo + SAM2.");
            string source = Path.GetFullPath(sourcePath.Text);
            string output = Path.GetFullPath(outputPath.Text);
            if (!File.Exists(source)) throw new FileNotFoundException("Choose a source video.", source);
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output must not overwrite the source video.");
            if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output file must be an MP4.");
            if (File.Exists(output) && MessageBox.Show(this,
                    "Replace the existing output after processing completes?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            VideoInfo info = sourceInfo ?? ReadInfo(source);
            Directory.CreateDirectory(temporary);
            string model = SelectedSam2Model();
            using CustomMaskEditorForm maskEditor = new(source, configuration, 0,
                info.DurationMs, algorithm: "Stabilo + SAM2 subject exclusion",
                allowFrameSelection: true, sam2Model: model,
                draftFolder: Path.Combine(temporary, "selection"));
            if (maskEditor.ShowDialog(this) != DialogResult.OK) return;
            maskEditor.SaveMask(InitialMaskPath);
            long referenceMs = maskEditor.FrameMs;

            using CustomVideoMaskEditorForm refinement = new(source, configuration,
                0, info.DurationMs, InitialMaskPath, MasksPath,
                sam2Model: model, profileLog: ProfileLogPath,
                statePath: CorrectionStatePath, maskEngine: "sam2",
                initialFrameMs: referenceMs);
            if (refinement.ShowDialog(this) != DialogResult.OK) return;

            if (File.Exists(SourcePreviewPath)) File.Copy(SourcePreviewPath,
                OutputPreviewPath, true);
            StabiloSettings settings = SelectedSettings();
            using CustomShowProcessingForm processing = new((progress, token) =>
                    StabilizeAsync(configuration, source, output, MasksPath,
                        referenceMs, settings, SourcePreviewPath, OutputPreviewPath,
                        progress, token),
                "Camera / Background Lock (Stabilo + SAM2)",
                "Stabilo background motion / SAM2 exclusion",
                "Stabilized frame (available during Step 2)",
                "Two-step Stabilo processing — overall progress\n" +
                "Step 1: analyze unmasked background   →   Step 2: warp and encode output");
            if (processing.ShowDialog(this) != DialogResult.OK) return;
            if (File.Exists(OutputPreviewPath))
            {
                Bitmap next = LoadBitmap(OutputPreviewPath);
                preview.Image = next;
                frame?.Dispose(); frame = next;
            }
            UpdateReviewAvailability();
            status.Text = "Background-locked video complete: " + output;
            using CustomVideoComparisonForm comparison = new(source, output,
                info.DurationMs, info.FrameRate);
            comparison.ShowDialog(this);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    string SelectedSam2Model()
    {
        string selected = sam2Model.SelectedItem?.ToString() ?? "Small";
        if (selected.StartsWith("Tiny", StringComparison.Ordinal)) return "tiny";
        if (selected.StartsWith("Base+", StringComparison.Ordinal)) return "base-plus";
        return "small";
    }

    StabiloSettings SelectedSettings() => preset.SelectedIndex switch
    {
        1 => new("orb", "affine", .50, borders.SelectedIndex == 1 ? "black" : "reflect"),
        2 => new("xfeat", "projective", .35, borders.SelectedIndex == 1 ? "black" : "reflect"),
        _ => new("xfeat", "affine", .35, borders.SelectedIndex == 1 ? "black" : "reflect")
    };

    static async Task<CustomShowProcessResult> StabilizeAsync(
        CustomShowConfiguration configuration, string source, string output,
        string masks, long referenceMs, StabiloSettings settings,
        string sourcePreview, string outputPreview,
        IProgress<CustomShowProgress> progress, CancellationToken token)
    {
        string staged = Path.Combine(Path.GetDirectoryName(output)!, "." +
            Path.GetFileNameWithoutExtension(output) + ".stabilo-" +
            Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        StringBuilder log = new();
        try
        {
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                CustomShowProcessor.StabiloWorkerPath,
                "--source", source, "--output", staged, "--masks", masks,
                "--runtime", CustomShowProcessor.RuntimeRoot(configuration),
                "--reference-ms", referenceMs.ToString(CultureInfo.InvariantCulture),
                "--detector", settings.Detector,
                "--transformation", settings.Transformation,
                "--downsample", settings.Downsample.ToString(CultureInfo.InvariantCulture),
                "--border", settings.Border,
                "--nvenc-preset", CustomShowProcessor.ValidNvencPreset(
                    configuration.RvmNvencPreset),
                "--source-preview", sourcePreview,
                "--output-preview", outputPreview,
                "--ffmpeg", Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                "--ffprobe", Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")
            }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("The Stabilo worker could not be started.");
            await using ProcessCancellationScope cancellationScope = new(process, token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            CustomShowProcessResult? result = null;
            string? workerError = null;
            while (await process.StandardOutput.ReadLineAsync(token) is string line)
            {
                log.AppendLine(line);
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    string state = root.TryGetProperty("status", out JsonElement statusValue)
                        ? statusValue.GetString() ?? "" : "";
                    string message = root.TryGetProperty("message", out JsonElement messageValue)
                        ? messageValue.GetString() ?? state : state;
                    double percent = root.TryGetProperty("percent", out JsonElement percentValue)
                        ? percentValue.GetDouble() : 0;
                    string? previewSource = JsonText(root, "sourcePreview");
                    string? previewOutput = JsonText(root, "outputPreview");
                    progress.Report(new(state, percent, message, previewSource,
                        previewOutput, JsonText(root, "sourceLabel"),
                        JsonText(root, "outputLabel")));
                    if (state == "complete" && root.TryGetProperty("result", out JsonElement value))
                        result = JsonSerializer.Deserialize<CustomShowProcessResult>(
                            value.GetRawText(), CustomShowStore.JsonOptions);
                    else if (state == "error") workerError = message;
                }
                catch (JsonException) { }
            }
            await process.WaitForExitAsync(token);
            string stderr = await errorTask;
            log.AppendLine(stderr);
            if (process.ExitCode != 0 || result == null)
                throw new InvalidOperationException(workerError ??
                    (string.IsNullOrWhiteSpace(stderr) ?
                        "Stabilo did not return a completed result." : stderr.Trim()));
            if (!File.Exists(staged) || new FileInfo(staged).Length == 0)
                throw new InvalidDataException("Stabilo did not create an output video.");
            File.Move(staged, output, true);
            return result;
        }
        finally
        {
            try { await File.WriteAllTextAsync(output + ".stabilo.log",
                log.ToString(), CancellationToken.None); } catch { }
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }

    static string? JsonText(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    void UpdateReviewAvailability()
    {
        try
        {
            review.Enabled = File.Exists(sourcePath.Text) && File.Exists(outputPath.Text) &&
                !string.Equals(Path.GetFullPath(sourcePath.Text),
                    Path.GetFullPath(outputPath.Text), StringComparison.OrdinalIgnoreCase);
        }
        catch { review.Enabled = false; }
    }

    async Task ReviewAsync()
    {
        try
        {
            string source = Path.GetFullPath(sourcePath.Text),
                output = Path.GetFullPath(outputPath.Text);
            if (!File.Exists(source) || !File.Exists(output))
                throw new FileNotFoundException("Both videos are required for review.");
            VideoInfo info = sourceInfo ?? ReadInfo(source);
            using CustomVideoComparisonForm comparison = new(source, output,
                info.DurationMs, info.FrameRate);
            comparison.ShowDialog(this);
            await Task.CompletedTask;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    static VideoInfo ReadInfo(string source)
    {
        using FfmpegCpuDecoder decoder = new(source, fastDecode: true);
        return new(decoder.Width, decoder.Height,
            (long)Math.Round(decoder.Duration * 1000), decoder.FrameRate);
    }

    static async Task ExtractPreviewAsync(string source, string destination,
        double seconds, CancellationToken token)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-y", "-v", "error", "-ss",
            seconds.ToString(CultureInfo.InvariantCulture), "-i", source,
            "-frames:v", "1", "-vf", "scale=960:-2:force_original_aspect_ratio=decrease",
            destination }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg could not extract the preview.");
        string error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0 || !File.Exists(destination))
            throw new InvalidOperationException(error.Trim());
    }

    static Bitmap LoadBitmap(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using Image image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    static string FormatDuration(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(milliseconds);
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" :
            $"{value.Minutes}:{value.Seconds:00}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frame?.Dispose();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
