using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed record CustomShowProgress(
    string Stage, double Percent, string Message,
    string? PreviewSource = null, string? PreviewComposite = null);

internal sealed class CustomShowProcessResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string FrameRate { get; set; } = "";
    public long DurationMs { get; set; }
}

internal sealed record CustomShowProcessJob(string Output, long StartMs, long EndMs);

internal static class CustomShowProcessor
{
    internal static string WorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "rvm_worker.py");
    internal static string MatAnyoneWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "matanyone2_worker.py");
    internal static string VideoMaMaWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "videomama_worker.py");
    internal static string ViTMatteWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "vitmatte_worker.py");
    internal static string ProPainterWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "propainter_worker.py");

    internal static string RuntimeRoot(CustomShowConfiguration configuration) =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(configuration.PythonExecutable)!, "..", ".."));

    internal static bool IsTransNetV2Installed(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "TRANSNETV2_COMMIT", "transnetv2");

    internal static bool IsMatAnyone2Installed(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "MATANYONE2_COMMIT", "matanyone2") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(MatAnyoneWorkerPath);

    internal static bool IsVideoMaMaInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "VIDEOMAMA_COMMIT", "videomama") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "videomama-base", "image_encoder",
            "model.fp16.safetensors")) &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "videomama-base", "vae",
            "diffusion_pytorch_model.fp16.safetensors")) &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "videomama-model", "unet",
            "diffusion_pytorch_model.safetensors")) && File.Exists(VideoMaMaWorkerPath);

    internal static bool IsViTMatteSmallInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "VITMATTE_S_REVISION", "vitmatte-s") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "vitmatte-s",
            "model.safetensors")) && File.Exists(ViTMatteWorkerPath);

    internal static bool IsViTMatteBaseInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "VITMATTE_B_REVISION", "vitmatte-b") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "vitmatte-b",
            "pytorch_model.bin")) && File.Exists(ViTMatteWorkerPath);

    internal static bool IsProPainterInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "PROPAINTER_STREAMING_COMMIT", "propainter-streaming") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "propainter-streaming-weights",
            "propainter-0000-5f3cc1e7.pth")) && File.Exists(ProPainterWorkerPath);

    static bool OptionalToolInstalled(CustomShowConfiguration configuration,
        string marker, string folder)
    {
        try
        {
            string runtime = RuntimeRoot(configuration);
            return File.Exists(Path.Combine(runtime, marker)) &&
                Directory.Exists(Path.Combine(runtime, folder));
        }
        catch { return false; }
    }

    internal static bool VerifyOptionalToolDetection()
    {
        string runtime = Path.Combine(Path.GetTempPath(),
            "iqp-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            CustomShowConfiguration configuration = new()
            {
                PythonExecutable = Path.Combine(runtime, "venv", "Scripts", "python.exe")
            };
            if (IsTransNetV2Installed(configuration) ||
                IsMatAnyone2Installed(configuration) || IsVideoMaMaInstalled(configuration) ||
                IsViTMatteSmallInstalled(configuration) ||
                IsViTMatteBaseInstalled(configuration) || IsProPainterInstalled(configuration))
                return false;
            foreach (string directory in new[] { "transnetv2", "matanyone2",
                "videomama", "sam2", "vitmatte-s", "vitmatte-b",
                "propainter-streaming",
                Path.Combine("videomama-base", "image_encoder"),
                Path.Combine("videomama-base", "vae"),
                Path.Combine("videomama-model", "unet"),
                "propainter-streaming-weights" })
                Directory.CreateDirectory(Path.Combine(runtime, directory));
            foreach (string marker in new[] { "TRANSNETV2_COMMIT", "MATANYONE2_COMMIT",
                "VIDEOMAMA_COMMIT", "SAM2_COMMIT",
                "VITMATTE_S_REVISION", "VITMATTE_B_REVISION",
                "PROPAINTER_STREAMING_COMMIT" })
                File.WriteAllText(Path.Combine(runtime, marker), "installed");
            foreach (string model in new[] {
                Path.Combine("videomama-base", "image_encoder", "model.fp16.safetensors"),
                Path.Combine("videomama-base", "vae", "diffusion_pytorch_model.fp16.safetensors"),
                Path.Combine("videomama-model", "unet", "diffusion_pytorch_model.safetensors") })
                File.WriteAllBytes(Path.Combine(runtime, model), []);
            File.WriteAllBytes(Path.Combine(runtime, "propainter-streaming-weights",
                "propainter-0000-5f3cc1e7.pth"), []);
            File.WriteAllBytes(Path.Combine(runtime, "vitmatte-s", "model.safetensors"), []);
            File.WriteAllBytes(Path.Combine(runtime, "vitmatte-b", "pytorch_model.bin"), []);
            return IsTransNetV2Installed(configuration) &&
                IsMatAnyone2Installed(configuration) && IsVideoMaMaInstalled(configuration) &&
                IsViTMatteSmallInstalled(configuration) &&
                IsViTMatteBaseInstalled(configuration) &&
                IsProPainterInstalled(configuration);
        }
        finally { try { Directory.Delete(runtime, true); } catch { } }
    }

    internal static async Task<CustomShowProcessResult> RunAsync(
        CustomShowConfiguration configuration, string source,
        string stagingFolder, string preset, string? initialMask,
        string? maskFolder,
        int mattingResolution, int sequenceChunk, long startMs, long? endMs,
        string? logPath, bool appendLog,
        IProgress<CustomShowProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<CustomShowProcessJob>? jobs = null)
    {
        string python = configuration.PythonExecutable;
        if (!File.Exists(python))
            throw new FileNotFoundException(
                "Configure the Python 3.11-3.14 custom-show environment first.", python);
        string worker = preset switch
        {
            "matanyone2" => MatAnyoneWorkerPath,
            "videomama" => VideoMaMaWorkerPath,
            "vitmatte-s" or "vitmatte-b" => ViTMatteWorkerPath,
            _ => WorkerPath
        };
        if (!File.Exists(worker))
            throw new FileNotFoundException("The processing worker is missing.", worker);
        if (preset == "matanyone2" && !File.Exists(initialMask))
            throw new FileNotFoundException("Create the initial foreground mask first.", initialMask);
        if (preset is "videomama" or "vitmatte-s" or "vitmatte-b" &&
            !Directory.Exists(maskFolder))
            throw new DirectoryNotFoundException(
                "Review and accept the SAM2 video masks before processing.");
        Directory.CreateDirectory(stagingFolder);
        string runtime = RuntimeRoot(configuration);
        ProcessStartInfo start = new(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            worker, "--source", source, "--output", stagingFolder,
            "--runtime", runtime
        }) start.ArgumentList.Add(argument);
        if (preset == "matanyone2")
        {
            start.ArgumentList.Add("--mask");
            start.ArgumentList.Add(initialMask!);
            start.ArgumentList.Add("--max-size");
            start.ArgumentList.Add(mattingResolution.ToString());
        }
        else if (preset is "videomama" or "vitmatte-s" or "vitmatte-b")
        {
            start.ArgumentList.Add("--mask-folder");
            start.ArgumentList.Add(maskFolder!);
            start.ArgumentList.Add("--batch-size");
            start.ArgumentList.Add(Math.Clamp(sequenceChunk, 1, 12).ToString());
            if (preset.StartsWith("vitmatte", StringComparison.Ordinal))
            {
                start.ArgumentList.Add("--model");
                start.ArgumentList.Add(preset[^1..]);
            }
        }
        else
        {
            foreach (string argument in new[]
            {
                "--preset", preset, "--matting-resolution", mattingResolution.ToString(),
                "--sequence-chunk", Math.Clamp(sequenceChunk, 1, 24).ToString()
            }) start.ArgumentList.Add(argument);
            if (jobs != null)
                foreach (CustomShowProcessJob job in jobs)
                {
                    start.ArgumentList.Add("--job");
                    start.ArgumentList.Add(JsonSerializer.Serialize(new
                    {
                        output = job.Output,
                        startMs = job.StartMs,
                        endMs = job.EndMs
                    }));
                }
        }
        if (jobs == null)
        {
            start.ArgumentList.Add("--start-ms");
            start.ArgumentList.Add(Math.Max(0, startMs).ToString());
            if (endMs != null)
            {
                start.ArgumentList.Add("--end-ms");
                start.ArgumentList.Add(endMs.Value.ToString());
            }
        }
        start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

        using Process process = new() { StartInfo = start };
        StringBuilder log = new();
        process.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        Task stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
                lock (log) log.AppendLine(line);
        });
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
            {
                lock (log) log.AppendLine(line);
                try
                {
                    using JsonDocument json = JsonDocument.Parse(line);
                    JsonElement root = json.RootElement;
                    progress?.Report(new CustomShowProgress(
                        root.TryGetProperty("stage", out var stage) ? stage.GetString() ?? "processing" : "processing",
                        root.TryGetProperty("percent", out var percent) ? percent.GetDouble() : 0,
                        root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "",
                        File.Exists(Path.Combine(stagingFolder, "preview-source.jpg"))
                            ? Path.Combine(stagingFolder, "preview-source.jpg") : null,
                        File.Exists(Path.Combine(stagingFolder, "preview-composite.jpg"))
                            ? Path.Combine(stagingFolder, "preview-composite.jpg") : null));
                }
                catch (JsonException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await stderr;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Foreground processing failed (exit code {process.ExitCode}). See processing.log.");
            string resultPath = Path.Combine(jobs?.FirstOrDefault()?.Output ??
                stagingFolder, "result.json");
            return JsonSerializer.Deserialize<CustomShowProcessResult>(
                await File.ReadAllTextAsync(resultPath, cancellationToken),
                CustomShowStore.JsonOptions) ??
                throw new InvalidDataException("The worker did not return media information.");
        }
        finally
        {
            string text;
            lock (log) text = log.ToString();
            string destination = logPath ?? Path.Combine(stagingFolder, "processing.log");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (appendLog)
                await File.AppendAllTextAsync(destination, text, CancellationToken.None);
            else
                await File.WriteAllTextAsync(destination, text, CancellationToken.None);
            foreach (string preview in new[] { "preview-source.jpg", "preview-composite.jpg" })
                try { File.Delete(Path.Combine(stagingFolder, preview)); } catch { }
        }
    }

    internal static async Task GenerateCoverAsync(string source, string destination,
        long durationMs, string modelName, string showTitle, Color titleColor,
        CancellationToken token)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-y", "-v", "error", "-ss",
            (durationMs / 2000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-i", source, "-frames:v", "1", "-vf",
            "scale=600:900:force_original_aspect_ratio=increase,crop=600:900", destination })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg could not generate the cover image.");
        string error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Cover generation failed: " + error.Trim());
        using Image image = Image.FromFile(destination);
        using Bitmap cover = new(image);
        DrawCoverText(cover, modelName, showTitle, titleColor);
        image.Dispose();
        cover.Save(destination, System.Drawing.Imaging.ImageFormat.Jpeg);
    }

    static void DrawCoverText(Bitmap cover, string modelName, string showTitle,
        Color titleColor)
    {
        using Graphics graphics = Graphics.FromImage(cover);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using Font modelFont = FittedFont(graphics, modelName, "Segoe Script", 74, 540);
        using Font titleFont = FittedFont(graphics, showTitle.ToUpperInvariant(),
            "Segoe UI", 42, 540, FontStyle.Bold);
        DrawCentered(graphics, modelName, modelFont, Color.White, 700);
        DrawCentered(graphics, showTitle.ToUpperInvariant(), titleFont, titleColor, 810);
    }

    static Font FittedFont(Graphics graphics, string text, string family,
        float size, float width, FontStyle style = FontStyle.Regular)
    {
        using Font measure = new(family, size, style, GraphicsUnit.Pixel);
        float measured = graphics.MeasureString(text, measure).Width;
        return new Font(family, measured > width ? size * width / measured : size,
            style, GraphicsUnit.Pixel);
    }

    static void DrawCentered(Graphics graphics, string text, Font font,
        Color color, float y)
    {
        using StringFormat format = new() { Alignment = StringAlignment.Center };
        RectangleF area = new(20, y, 560, font.Height + 12);
        using Brush shadow = new SolidBrush(Color.FromArgb(210, Color.Black));
        using Brush foreground = new SolidBrush(color);
        graphics.DrawString(text, font, shadow,
            new RectangleF(area.X + 3, area.Y + 3, area.Width, area.Height), format);
        graphics.DrawString(text, font, foreground, area, format);
    }

    internal static bool VerifyCoverOverlay()
    {
        using Bitmap cover = new(600, 900);
        DrawCoverText(cover, "Model", "Show", Color.Magenta);
        for (int y = 740; y < cover.Height; y += 5)
            for (int x = 0; x < cover.Width; x += 5)
                if (cover.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) return true;
        return false;
    }

    internal static async Task<string> ValidateAsync(
        CustomShowConfiguration configuration, CancellationToken token,
        IProgress<string>? progress = null)
    {
        progress?.Report("Checking bundled FFmpeg libraries...");
        if (!FfmpegCpuDecoder.VerifyRuntime())
            throw new InvalidOperationException(
                "QuickPlayer requires the bundled FFmpeg 8 libavcodec 62 runtime.");
        string temporary = Path.Combine(Path.GetTempPath(),
            "iqp-custom-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string runtime = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(configuration.PythonExecutable)!, "..", ".."));
            foreach (string argument in new[] { WorkerPath, "--validate", "--runtime", runtime })
                start.ArgumentList.Add(argument);
            start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("Python could not be started.");
            StringBuilder output = new(), error = new();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data?.StartsWith("STATUS:") == true)
                    progress?.Report(e.Data[7..]);
                else if (e.Data != null) lock (output) output.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { await process.WaitForExitAsync(token); }
            catch
            {
                if (!process.HasExited) process.Kill(true);
                throw;
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error.ToString());
            progress?.Report("Checking custom library write access...");
            using FileStream writeCheck = new(Path.Combine(configuration.LibraryRoot,
                ".write-check"), FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return output.ToString().Trim();
        }
        finally
        {
            try { Directory.Delete(temporary, true); } catch { }
        }
    }
}

internal sealed class CustomShowProcessingForm : Form
{
    readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 28 };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    readonly Button cancel = new() { Dock = DockStyle.Bottom, Text = "Cancel", Height = 36 };
    readonly PictureBox source = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly PictureBox composite = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly CancellationTokenSource cancellation = new();
    readonly Stopwatch elapsed = new();
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    readonly Func<IProgress<CustomShowProgress>, CancellationToken,
        Task<CustomShowProcessResult>> operation;
    readonly List<(double Seconds, double Percent)> progressSamples = [];
    string statusMessage = "Starting processing tools...";
    double percent;
    DateTime lastPreviewUtc;
    internal CustomShowProcessResult? Result { get; private set; }

    internal CustomShowProcessingForm(Func<IProgress<CustomShowProgress>,
        CancellationToken, Task<CustomShowProcessResult>> operation,
        string text = "Processing Custom Show", string sourceLabel = "Original input",
        string compositeLabel = "Composited result")
    {
        this.operation = operation;
        Text = text;
        ClientSize = new Size(1000, 680);
        MinimumSize = new Size(700, 500);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill,
            RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        TableLayoutPanel previews = new() { Dock = DockStyle.Fill,
            RowCount = 1, ColumnCount = 2, Padding = new Padding(8) };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.Controls.Add(PreviewGroup(sourceLabel, source), 0, 0);
        previews.Controls.Add(PreviewGroup(compositeLabel, composite), 1, 0);
        layout.Controls.Add(bar, 0, 0);
        layout.Controls.Add(previews, 0, 1);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(cancel, 0, 3);
        Controls.Add(layout);
        cancel.Click += (_, _) => { cancel.Enabled = false; status.Text = "Cancelling..."; cancellation.Cancel(); };
        clock.Tick += (_, _) => RefreshStatus();
        Shown += (_, e) =>
        {
            elapsed.Start();
            RefreshStatus();
            clock.Start();
            Run(this, e);
        };
        FormClosing += (_, e) => { if (Result == null && !cancellation.IsCancellationRequested) { e.Cancel = true; cancel.PerformClick(); } };
    }

    async void Run(object? sender, EventArgs e)
    {
        try
        {
            Progress<CustomShowProgress> progress = new(value =>
            {
                percent = Math.Clamp(value.Percent, 0, 100);
                AddProgressSample(progressSamples,
                    elapsed.Elapsed.TotalSeconds, percent);
                statusMessage = string.IsNullOrWhiteSpace(value.Message) ? value.Stage : value.Message;
                bool compiling = statusMessage.StartsWith("Compiling optimized SAM2",
                    StringComparison.Ordinal) || statusMessage.StartsWith(
                    "Loading cached optimized SAM2", StringComparison.Ordinal);
                bar.Style = compiling ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
                if (!compiling) bar.Value = (int)Math.Round(percent);
                UpdatePreview(value.PreviewSource, value.PreviewComposite);
            });
            Result = await operation(progress, cancellation.Token);
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException) { DialogResult = DialogResult.Cancel; }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Abort;
        }
        finally { clock.Stop(); elapsed.Stop(); Close(); }
    }

    static GroupBox PreviewGroup(string text, PictureBox picture)
    {
        GroupBox group = new() { Text = text, Dock = DockStyle.Fill,
            Padding = new Padding(6) };
        group.Controls.Add(picture);
        return group;
    }

    void UpdatePreview(string? sourcePath, string? compositePath)
    {
        if (sourcePath == null || compositePath == null) return;
        try
        {
            DateTime sourceWritten = File.GetLastWriteTimeUtc(sourcePath),
                compositeWritten = File.GetLastWriteTimeUtc(compositePath),
                written = sourceWritten > compositeWritten ? sourceWritten : compositeWritten;
            if (written <= lastPreviewUtc) return;
            Image nextSource = LoadPreview(sourcePath),
                nextComposite = LoadPreview(compositePath);
            Image? oldSource = source.Image, oldComposite = composite.Image;
            source.Image = nextSource; composite.Image = nextComposite;
            oldSource?.Dispose(); oldComposite?.Dispose();
            lastPreviewUtc = written;
        }
        catch (IOException) { }
        catch (ArgumentException) { }
    }

    static Image LoadPreview(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using Image source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Image?.Dispose();
            composite.Image?.Dispose();
            cancellation.Dispose();
            clock.Dispose();
        }
        base.Dispose(disposing);
    }

    void RefreshStatus()
    {
        TimeSpan? remaining = EstimateRemaining(progressSamples, percent);
        status.Text = $"{statusMessage}{Environment.NewLine}Elapsed {FormatDuration(elapsed.Elapsed)}  •  " +
            (remaining == null ? "Remaining: estimating..." :
                $"Remaining: ~{FormatDuration(remaining.Value)}");
    }

    static TimeSpan? EstimateRemaining(
        IReadOnlyList<(double Seconds, double Percent)> samples,
        double percent)
    {
        if (samples.Count < 4 || percent >= 100) return null;
        (double startSeconds, double startPercent) = samples[0];
        (double endSeconds, double endPercent) = samples[^1];
        double sampleSeconds = endSeconds - startSeconds;
        double samplePercent = endPercent - startPercent;
        if (sampleSeconds < 3 || samplePercent <= 0) return null;
        double seconds = (100 - percent) * sampleSeconds / samplePercent;
        return double.IsFinite(seconds) && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds) : null;
    }

    static void AddProgressSample(
        List<(double Seconds, double Percent)> samples,
        double seconds, double percent)
    {
        if (percent <= 0 || percent >= 100 ||
            samples.Count > 0 && percent <= samples[^1].Percent)
            return;
        samples.Add((seconds, percent));
        double cutoff = seconds - 30;
        while (samples.Count > 2 && samples[1].Seconds < cutoff)
            samples.RemoveAt(0);
    }

    static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    internal static double AggregateClipPercent(long completedDuration,
        long clipDuration, long totalDuration, double clipPercent) =>
        totalDuration <= 0 ? 0 : 100 * (completedDuration +
            Math.Max(0, clipDuration) * Math.Clamp(clipPercent, 0, 100) / 100) /
            totalDuration;

    internal static bool VerifyEstimate()
    {
        List<(double Seconds, double Percent)> fast = [];
        for (int index = 1; index <= 100; index++)
            AddProgressSample(fast, index * 0.05, index * 0.5);
        return EstimateRemaining(
                   [(5, 20), (10, 30), (15, 40), (20, 50)], 50) ==
               TimeSpan.FromSeconds(25) &&
            EstimateRemaining([(5, 20), (10, 30), (15, 40)], 40) == null &&
            EstimateRemaining(fast, 50) is not null &&
            AggregateClipPercent(0, 10_000, 100_000, 100) == 10 &&
            AggregateClipPercent(10_000, 90_000, 100_000, 50) == 55;
    }
}
