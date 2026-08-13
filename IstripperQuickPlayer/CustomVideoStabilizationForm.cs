using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomVideoStabilizationForm : Form
{
    sealed record VideoInfo(int Width, int Height, long DurationMs,
        long EstimatedFrames, string FrameRate);
    sealed record StabilizationSettings(int Shakiness, int Smoothing, int OptZoom);

    readonly CustomShowConfiguration configuration;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-stabilize-" + Guid.NewGuid().ToString("N"));
    readonly TextBox sourcePath = new() { Dock = DockStyle.Fill };
    readonly TextBox outputPath = new() { Dock = DockStyle.Fill };
    readonly DxgiMaskPreviewControl preview = new() { Dock = DockStyle.Fill,
        AccessibleName = "Representative frame from the video to stabilize" };
    readonly ComboBox strength = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 210 };
    readonly ComboBox motion = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 190 };
    readonly ComboBox borders = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 245 };
    readonly Label status = new() { AutoSize = true, Text = "Choose a source video." };
    readonly Button run = new() { Text = "Stabilize video...", AutoSize = true,
        Enabled = false };
    readonly Button review = new() { Text = "Review input vs output...", AutoSize = true,
        Enabled = false };
    VideoInfo? sourceInfo;

    string SourcePreviewPath => Path.Combine(temporary, "source-preview.jpg");
    string OutputPreviewPath => Path.Combine(temporary, "output-preview.jpg");
    string TransformPath => Path.Combine(temporary, "transforms.trf");

    internal CustomVideoStabilizationForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Stabilize Video (FFmpeg)";
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(760, 560);
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
        layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 4),
            Text = "FFmpeg VidStab performs a motion-analysis pass followed by stabilization. " +
                "The source is never changed." }, 0, 2);

        strength.Items.AddRange(["Light (31-frame window)",
            "Standard (61-frame window - recommended)", "Strong (121-frame window)"]);
        strength.SelectedIndex = 1;
        motion.Items.AddRange(["Normal camera motion", "Fast / very shaky camera motion"]);
        motion.SelectedIndex = 0;
        borders.Items.AddRange(["Static auto-zoom (recommended)", "Adaptive auto-zoom",
            "No auto-zoom (may show borders)"]);
        borders.SelectedIndex = 0;
        FlowLayoutPanel options = new() { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        options.Controls.AddRange([OptionLabel("Strength"), strength,
            OptionLabel("Motion"), motion, OptionLabel("Borders"), borders]);
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
            Path.GetFileNameWithoutExtension(dialog.FileName) + "-stabilized.mp4");
        await LoadSourceAsync();
    }

    void BrowseOutput(object? sender, EventArgs e)
    {
        using SaveFileDialog dialog = new() { Filter = "MP4 video|*.mp4",
            DefaultExt = "mp4", AddExtension = true, FileName = outputPath.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            outputPath.Text = dialog.FileName;
            UpdateReviewAvailability();
        }
    }

    async Task LoadSourceAsync()
    {
        try
        {
            run.Enabled = false;
            status.Text = "Reading video information and extracting a preview...";
            Directory.CreateDirectory(temporary);
            sourceInfo = await ProbeAsync(sourcePath.Text, CancellationToken.None);
            await ExtractPreviewAsync(sourcePath.Text, SourcePreviewPath,
                Math.Min(3, sourceInfo.DurationMs / 2000d), CancellationToken.None);
            Bitmap next = LoadBitmap(SourcePreviewPath);
            preview.SetSourceOwned(next);
            run.Enabled = true;
            UpdateReviewAvailability();
            status.Text = $"Ready: {sourceInfo.Width}×{sourceInfo.Height}, " +
                $"{FormatDuration(sourceInfo.DurationMs)}.";
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
            string source = Path.GetFullPath(sourcePath.Text);
            string output = Path.GetFullPath(outputPath.Text);
            if (!File.Exists(source))
                throw new FileNotFoundException("Choose a source video.", source);
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output must not overwrite the source video.");
            if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output file must be an MP4.");
            if (File.Exists(output) && MessageBox.Show(this,
                    "Replace the existing output after stabilization completes?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            VideoInfo info = sourceInfo ?? await ProbeAsync(source, CancellationToken.None);
            StabilizationSettings settings = SelectedSettings();
            CreateWaitingPreview(OutputPreviewPath);
            using CustomShowProcessingForm processing = new((progress, token) =>
                    StabilizeAsync(configuration, source, output, temporary, TransformPath,
                        SourcePreviewPath, OutputPreviewPath, info, settings,
                        progress, token),
                "Stabilizing Video (FFmpeg)", "Detected camera motion",
                "Stabilized frame (available during Step 2)",
                "Two-step stabilization — overall progress\n" +
                "Step 1: analyze camera motion   →   Step 2: render stabilized video");
            if (processing.ShowDialog(this) != DialogResult.OK) return;
            if (File.Exists(OutputPreviewPath))
            {
                Bitmap next = LoadBitmap(OutputPreviewPath);
                preview.SetSourceOwned(next);
            }
            UpdateReviewAvailability();
            status.Text = "Stabilized video complete: " + output;
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

    void UpdateReviewAvailability()
    {
        review.Enabled = File.Exists(sourcePath.Text) && File.Exists(outputPath.Text) &&
            !string.Equals(Path.GetFullPath(sourcePath.Text),
                Path.GetFullPath(outputPath.Text), StringComparison.OrdinalIgnoreCase);
    }

    async Task ReviewAsync()
    {
        try
        {
            string source = Path.GetFullPath(sourcePath.Text);
            string output = Path.GetFullPath(outputPath.Text);
            if (!File.Exists(source) || !File.Exists(output))
                throw new FileNotFoundException(
                    "Both the original and stabilized videos are required for review.");
            VideoInfo info = sourceInfo ?? await ProbeAsync(source, CancellationToken.None);
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

    StabilizationSettings SelectedSettings() => new(
        motion.SelectedIndex == 1 ? 8 : 5,
        strength.SelectedIndex switch { 0 => 15, 2 => 60, _ => 30 },
        borders.SelectedIndex switch { 1 => 2, 2 => 0, _ => 1 });

    static async Task<CustomShowProcessResult> StabilizeAsync(
        CustomShowConfiguration configuration, string source, string output,
        string work, string transform, string sourcePreview, string outputPreview,
        VideoInfo info, StabilizationSettings settings,
        IProgress<CustomShowProgress> progress, CancellationToken token)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        string staged = Path.Combine(Path.GetDirectoryName(output)!, "." +
            Path.GetFileNameWithoutExtension(output) + ".stabilizing-" +
            Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string preset = CustomShowProcessor.ValidNvencPreset(configuration.RvmNvencPreset);
        bool nvenc = await CanUseNvencAsync(ffmpeg, preset, work, token);
        StringBuilder log = new();
        try
        {
            progress.Report(new("analysis", 0,
                "Step 1 of 2 — Starting camera-motion analysis...",
                sourcePreview, outputPreview, "Detected camera motion",
                "Stabilized frame (available during Step 2)"));
            await RunFfmpegAsync(ffmpeg, work,
                ["-y", "-hide_banner", "-nostdin", "-v", "warning",
                 "-progress", "pipe:1", "-nostats", "-i", source,
                 "-filter_complex", DetectPreviewFilter(settings),
                 "-map", "[analysis]", "-an", "-sn", "-dn", "-f", "null", "-",
                 "-map", "[preview]", "-an", "-f", "image2", "-update", "1",
                 "-atomic_writing", "1", "-q:v", "3", sourcePreview],
                "Step 1 of 2 — Analyzing camera motion", 0, 45, info,
                sourcePreview, outputPreview, "Detected camera motion",
                "Stabilized frame (available during Step 2)",
                log, progress, token);

            List<string> transformArguments = ["-y", "-hide_banner", "-nostdin",
                "-v", "warning", "-progress", "pipe:1", "-nostats", "-i", source,
                "-filter_complex", TransformPreviewFilter(settings),
                "-map", "[encoded]", "-map", "0:a?", "-map_metadata", "0",
                "-map_chapters", "0"];
            if (nvenc)
                transformArguments.AddRange(["-c:v", "h264_nvenc", "-preset", preset,
                    "-tune", "hq", "-cq", "19"]);
            else
                transformArguments.AddRange(["-c:v", "libx264", "-preset", "slow",
                    "-crf", "18"]);
            transformArguments.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac",
                "-b:a", "192k", "-movflags", "+faststart",
                "-max_muxing_queue_size", "1024", staged,
                "-map", "[source_preview]", "-an", "-f", "image2", "-update", "1",
                "-atomic_writing", "1", "-q:v", "3", sourcePreview,
                "-map", "[stabilized_preview]", "-an", "-f", "image2",
                "-update", "1", "-atomic_writing", "1", "-q:v", "3",
                outputPreview]);
            await RunFfmpegAsync(ffmpeg, work, transformArguments,
                "Step 2 of 2 — Rendering stabilized video", 45, 53, info,
                sourcePreview, outputPreview, "Original frame", "Stabilized frame",
                log, progress, token);
            if (!File.Exists(staged) || new FileInfo(staged).Length == 0)
                throw new InvalidDataException("FFmpeg did not create a stabilized output file.");
            VideoInfo stabilizedInfo = await ProbeAsync(staged, token);
            long durationDifference = Math.Abs(stabilizedInfo.DurationMs - info.DurationMs);
            if (durationDifference > Math.Max(1_000, info.DurationMs / 100))
                throw new InvalidDataException(
                    "The stabilized output duration does not match the source video.");
            progress.Report(new("preview", 98, "Creating final preview...",
                sourcePreview, outputPreview));
            await ExtractPreviewAsync(staged, outputPreview,
                Math.Min(3, info.DurationMs / 2000d), token);
            File.Move(staged, output, true);
            progress.Report(new("complete", 100, "Stabilization complete.",
                sourcePreview, outputPreview));
            return new CustomShowProcessResult { Width = stabilizedInfo.Width,
                Height = stabilizedInfo.Height, FrameRate = stabilizedInfo.FrameRate,
                DurationMs = stabilizedInfo.DurationMs,
                Encoder = nvenc ? "h264_nvenc" : "libx264",
                EncoderPreset = nvenc ? preset : "slow" };
        }
        finally
        {
            try { await File.WriteAllTextAsync(output + ".stabilization.log",
                log.ToString(), CancellationToken.None); } catch { }
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            try { if (File.Exists(transform)) File.Delete(transform); } catch { }
        }
    }

    static string DetectFilter(StabilizationSettings settings) =>
        $"vidstabdetect=result=transforms.trf:shakiness={settings.Shakiness}:" +
        "accuracy=15:stepsize=6:mincontrast=0.25:show=1:fileformat=binary";

    static string DetectPreviewFilter(StabilizationSettings settings) =>
        $"[0:v]{DetectFilter(settings)},split=2[analysis][preview_input];" +
        "[preview_input]fps=1/2," + PreviewScale + "[preview]";

    static string TransformFilter(StabilizationSettings settings) =>
        $"vidstabtransform=input=transforms.trf:smoothing={settings.Smoothing}:" +
        $"optzoom={settings.OptZoom}:zoomspeed=0.25:crop=black:interpol=bicubic";

    const string PreviewScale =
        "scale=960:-2:force_original_aspect_ratio=decrease,setsar=1";

    static string TransformPreviewFilter(StabilizationSettings settings) =>
        "[0:v]split=2[source_preview_input][stabilize_input];" +
        $"[stabilize_input]{TransformFilter(settings)}," +
        "split=2[encoded][stabilized_preview_input];" +
        $"[source_preview_input]fps=1/2,{PreviewScale}[source_preview];" +
        $"[stabilized_preview_input]fps=1/2,{PreviewScale}[stabilized_preview]";

    static async Task RunFfmpegAsync(string ffmpeg, string work,
        IReadOnlyList<string> arguments, string label, double phaseStart,
        double phaseSize, VideoInfo info, string sourcePreview, string outputPreview,
        string sourceLabel, string outputLabel,
        StringBuilder log, IProgress<CustomShowProgress> progress,
        CancellationToken token)
    {
        ProcessStartInfo start = new(ffmpeg) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = work };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg could not be started.");
        await using ProcessCancellationScope cancellationScope = new(process, token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        long frame = 0;
        while (await process.StandardOutput.ReadLineAsync(token) is string line)
        {
            log.AppendLine(line);
            if (line.StartsWith("frame=", StringComparison.Ordinal))
                long.TryParse(line[6..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out frame);
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                !long.TryParse(line[12..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long microseconds)) continue;
            double fraction = Math.Clamp(microseconds / 1000d / info.DurationMs, 0, 1);
            long displayedFrame = Math.Clamp(frame, 0, info.EstimatedFrames);
            progress.Report(new(label, phaseStart + phaseSize * fraction,
                $"{label} — Processed {displayedFrame:N0}/{info.EstimatedFrames:N0} frames",
                sourcePreview, outputPreview, sourceLabel, outputLabel));
        }
        await process.WaitForExitAsync(token);
        string error = await errorTask;
        log.AppendLine(error);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"FFmpeg {label.ToLowerInvariant()} failed with exit code {process.ExitCode}."
                : error.Trim());
        progress.Report(new(label, phaseStart + phaseSize,
            $"{label} — Processed {info.EstimatedFrames:N0}/{info.EstimatedFrames:N0} frames",
            sourcePreview, outputPreview, sourceLabel, outputLabel));
    }

    static async Task<bool> CanUseNvencAsync(string ffmpeg, string preset,
        string work, CancellationToken token)
    {
        ProcessStartInfo start = new(ffmpeg) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true,
            WorkingDirectory = work };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i",
            "color=size=256x256:duration=0.1", "-frames:v", "1", "-an", "-c:v",
            "h264_nvenc", "-preset", preset, "-f", "null", "-" })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg could not be started.");
        await using ProcessCancellationScope cancellationScope = new(process, token);
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        return process.ExitCode == 0;
    }

    static async Task<VideoInfo> ProbeAsync(string source, CancellationToken token)
    {
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        ProcessStartInfo start = new(ffprobe) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,avg_frame_rate,nb_frames:format=duration",
            "-of", "json", source }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("ffprobe could not be started.");
        await using ProcessCancellationScope cancellationScope = new(process, token);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        string json = await output, errorText = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(errorText.Trim());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement stream = document.RootElement.GetProperty("streams")[0];
        int width = stream.GetProperty("width").GetInt32();
        int height = stream.GetProperty("height").GetInt32();
        string frameRate = stream.TryGetProperty("avg_frame_rate", out JsonElement rate)
            ? rate.GetString() ?? "0/1" : "0/1";
        double fps = ParseRate(frameRate);
        string durationText = document.RootElement.GetProperty("format")
            .GetProperty("duration").GetString() ?? "0";
        if (!double.TryParse(durationText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double duration) || duration <= 0)
            throw new InvalidDataException("The source video has no valid duration.");
        long durationMs = checked((long)Math.Round(duration * 1000));
        long frames = stream.TryGetProperty("nb_frames", out JsonElement count) &&
            long.TryParse(count.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long exact) && exact > 0
            ? exact : Math.Max(1, checked((long)Math.Round(duration * fps)));
        return new(width, height, durationMs, frames, frameRate);
    }

    static double ParseRate(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double denominator) && denominator != 0)
            return numerator / denominator;
        return double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double rate) && rate > 0 ? rate : 30;
    }

    static async Task ExtractPreviewAsync(string source, string output,
        double seconds, CancellationToken token)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        ProcessStartInfo start = new(ffmpeg) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-y", "-v", "error", "-ss",
            seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", source,
            "-frames:v", "1", "-vf",
            "scale=960:-2:force_original_aspect_ratio=decrease,setsar=1", "-q:v", "2", output })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg could not create the preview.");
        await using ProcessCancellationScope cancellationScope = new(process, token);
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "FFmpeg did not create the preview frame." : error.Trim());
    }

    static Bitmap LoadBitmap(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using Image image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    static void CreateWaitingPreview(string path)
    {
        using Bitmap bitmap = new(960, 540);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        using Font heading = new(FontFamily.GenericSansSerif, 28,
            FontStyle.Bold, GraphicsUnit.Pixel);
        using Font detail = new(FontFamily.GenericSansSerif, 20,
            FontStyle.Regular, GraphicsUnit.Pixel);
        using StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString("Stabilized preview", heading, Brushes.White,
            new RectangleF(0, 200, bitmap.Width, 48), format);
        graphics.DrawString("Available during Step 2 of 2", detail, Brushes.LightGray,
            new RectangleF(0, 250, bitmap.Width, 42), format);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
    }

    static string FormatDuration(long durationMs)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(durationMs);
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    internal static bool VerifySettings()
    {
        StabilizationSettings settings = new(5, 30, 1);
        return DetectFilter(settings).Contains("shakiness=5", StringComparison.Ordinal) &&
            TransformFilter(settings).Contains("smoothing=30:optzoom=1",
                StringComparison.Ordinal) &&
            DetectPreviewFilter(settings).Contains("fps=1/2",
                StringComparison.Ordinal) &&
            TransformPreviewFilter(settings).Contains("[encoded]",
                StringComparison.Ordinal) &&
            Math.Abs(ParseRate("30000/1001") - 29.97002997) < .0001 &&
            FormatDuration(65_000) == "1:05";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
