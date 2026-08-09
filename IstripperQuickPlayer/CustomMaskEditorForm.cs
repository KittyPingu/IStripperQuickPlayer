using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomMaskEditorForm : Form
{
    readonly string videoPath;
    readonly CustomShowConfiguration configuration;
    readonly long startMs;
    readonly long endMs;
    readonly long durationMs;
    readonly double frameDurationMs;
    readonly bool allowFrameSelection;
    readonly string sam2Model;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-mask-" + Guid.NewGuid().ToString("N"));
    readonly PictureBox image = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
        Enabled = false, AccessibleName = "Initial foreground mask image" };
    readonly ClipTimelineControl timeline = new() { Dock = DockStyle.Fill,
        Height = 60, AllowDividerDragging = false,
        AccessibleName = "Initial mask frame timeline" };
    readonly Label position = new() { AutoSize = true, Padding = new Padding(6, 8, 6, 0) };
    readonly Button previousFrame = new() { Text = "◀ 1 frame", AutoSize = true };
    readonly Button play = new() { Text = "Play", AutoSize = true };
    readonly Button nextFrame = new() { Text = "1 frame ▶", AutoSize = true };
    readonly CheckBox slowMotion = new() { Text = "Slow motion (0.25×)", AutoSize = true,
        Padding = new Padding(8, 6, 0, 0) };
    readonly Label status = new() { AutoSize = true, Padding = new Padding(8) };
    readonly Button clear = new() { Text = "Clear clicks", AutoSize = true, Enabled = false };
    readonly Button automatic = new() { Text = "Auto mask frame", AutoSize = true,
        Enabled = false };
    readonly Button accept = new() { Text = "Use this mask", AutoSize = true, Enabled = false };
    readonly List<PointF> points = [];
    readonly List<int> labels = [];
    readonly SemaphoreSlim responseLock = new(1, 1);
    readonly System.Windows.Forms.Timer playback = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 140 };
    readonly Stopwatch playClock = new();
    CancellationTokenSource? previewCancellation;
    CancellationTokenSource? streamCancellation;
    Process? worker;
    Task<string>? workerError;
    long playStartMs;
    long displayedFrameMs = -1;
    long workerFrameMs = -1;
    bool updatingMask;
    bool workerReady;
    bool frameReady;
    bool resumeAfterScrub;
    bool closing;

    internal long FrameMs => Math.Min(Math.Max(startMs, endMs - 1),
        startMs + timeline.PositionMs);
    string FramePath => Path.Combine(temporary, "selected-frame.png");
    string MaskPath => Path.Combine(temporary, "initial-mask.png");
    string PreviewPath => Path.Combine(temporary, "mask-preview.jpg");

    internal CustomMaskEditorForm(string videoPath,
        CustomShowConfiguration configuration, long startMs, long endMs,
        string? clipLabel = null, string algorithm = "MatAnyone 2",
        bool allowFrameSelection = false, string sam2Model = "base-plus")
    {
        this.videoPath = videoPath;
        this.configuration = configuration;
        this.startMs = startMs;
        this.endMs = endMs;
        this.allowFrameSelection = allowFrameSelection;
        this.sam2Model = sam2Model;
        durationMs = Math.Max(1, endMs - startMs);
        try
        {
            using FfmpegCpuDecoder decoder = new(videoPath, fastDecode: true);
            frameDurationMs = Math.Max(.1, decoder.FrameDuration * 1000);
        }
        catch { frameDurationMs = 1000d / 30; }

        Text = clipLabel == null ? $"Create {algorithm} Initial Mask" :
            $"Create {algorithm} Initial Mask - {clipLabel}";
        ClientSize = new Size(1050, allowFrameSelection ? 880 : 780);
        MinimumSize = new Size(720, allowFrameSelection ? 650 : 560);
        StartPosition = FormStartPosition.CenterParent;

        Label instructions = new() { Dock = DockStyle.Fill, Height = 44,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0),
            Text = allowFrameSelection
                ? "Choose any frame, then left-click each person to include. Right-click unwanted areas to exclude."
                : "Left-click each person to include. Right-click background or unwanted areas to exclude." };
        TableLayoutPanel root = new() { Dock = DockStyle.Fill, RowCount = 5,
            ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, allowFrameSelection ? 64 : 0));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(instructions, 0, 0);
        root.Controls.Add(image, 0, 1);
        root.Controls.Add(timeline, 0, 2);

        FlowLayoutPanel precision = new() { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = false, Padding = new Padding(6, 0, 0, 0) };
        precision.Controls.AddRange([previousFrame, play, position, nextFrame, slowMotion]);
        root.Controls.Add(precision, 0, 3);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([clear, automatic, accept, cancel, status]);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        CancelButton = cancel;

        timeline.Visible = precision.Visible = allowFrameSelection;
        timeline.DurationMs = durationMs;
        timeline.Clips = [new CustomShowClip
        {
            StartMs = 0, EndMs = durationMs, Included = true
        }];
        UpdatePosition();
        timeline.PositionChanged += (_, _) =>
        {
            UpdatePosition();
            if (!playback.Enabled) RequestPreview();
        };
        timeline.ScrubStarted += (_, _) =>
        {
            resumeAfterScrub = playback.Enabled;
            if (resumeAfterScrub) StopPlayback(requestPreview: false);
        };
        timeline.ScrubEnded += (_, _) =>
        {
            if (resumeAfterScrub) StartPlayback(); else RequestPreview();
            resumeAfterScrub = false;
        };
        previousFrame.Click += (_, _) => StepFrame(-1);
        play.Click += (_, _) => TogglePlayback();
        nextFrame.Click += (_, _) => StepFrame(1);
        slowMotion.CheckedChanged += (_, _) => RestartPlayback();
        playback.Tick += (_, _) => PlaybackTick();
        previewDelay.Tick += async (_, _) =>
        {
            previewDelay.Stop();
            await LoadSelectedFrameAsync(FrameMs);
        };
        image.MouseClick += Image_MouseClick;
        clear.Click += (_, _) => ClearClicks();
        automatic.Click += async (_, _) => await UpdateMaskAsync(true);
        accept.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Shown += async (_, _) => await StartWorkerAsync();
        FormClosing += (_, _) =>
        {
            closing = true;
            previewDelay.Stop();
            playback.Stop();
            previewCancellation?.Cancel();
            streamCancellation?.Cancel();
            StopWorker();
        };
        AppTheme.Apply(this);
        UpdateEnabledState();
    }

    internal void SaveMask(string destination)
    {
        if (!File.Exists(MaskPath))
            throw new InvalidOperationException("Create an initial mask first.");
        File.Copy(MaskPath, destination, true);
    }

    async Task StartWorkerAsync()
    {
        try
        {
            status.Text = "Extracting the selected frame...";
            Directory.CreateDirectory(temporary);
            await ExtractFrameAsync(FrameMs, CancellationToken.None);
            SetImage(LoadImage(FramePath));
            displayedFrameMs = FrameMs;
            frameReady = true;
            if (IsMostlyBlack(image.Image!))
            {
                timeline.PositionMs = Math.Min(durationMs,
                    Math.Min(3_000, durationMs / 2));
                await ExtractFrameAsync(FrameMs, CancellationToken.None);
                SetImage(LoadImage(FramePath));
                displayedFrameMs = FrameMs;
            }
            string python = configuration.PythonExecutable;
            string runtime = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(python)!, "..", ".."));
            string workerPath = Path.Combine(AppContext.BaseDirectory,
                "custom-shows", "sam_mask_worker.py");
            if (!File.Exists(Path.Combine(runtime, "SAM2_COMMIT")))
                throw new InvalidOperationException(
                    "Interactive masking is not installed. Run Install / Update Processing Tools and select this algorithm.");
            ProcessStartInfo start = new(python)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { workerPath, "--runtime", runtime,
                "--image", FramePath, "--mask", MaskPath, "--preview", PreviewPath,
                "--model", sam2Model })
                start.ArgumentList.Add(argument);
            worker = Process.Start(start) ??
                throw new InvalidOperationException("Python could not be started.");
            workerError = worker.StandardError.ReadToEndAsync();
            JsonElement ready = await ReadResponseAsync();
            if (ready.GetProperty("status").GetString() != "ready")
                throw new InvalidOperationException(ResponseError(ready));
            workerFrameMs = displayedFrameMs;
            workerReady = true;
            status.Text = $"{ready.GetProperty("checkpoint").GetString()} ready on " +
                $"{ready.GetProperty("device").GetString()?.ToUpperInvariant()}/" +
                ready.GetProperty("precision").GetString() +
                (allowFrameSelection ? "; choose the best frame, then create its mask." : "");
            UpdateEnabledState();
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed && !Disposing)
            {
                status.Text = "Mask setup failed";
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    async Task ExtractFrameAsync(long atMs, CancellationToken token)
    {
        string extracted = Path.Combine(temporary,
            "frame-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            ProcessStartInfo extract = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-y", "-v", "error", "-ss",
                (atMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                "-frames:v", "1", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,setsar=1",
                extracted }) extract.ArgumentList.Add(argument);
            using Process process = Process.Start(extract) ??
                throw new InvalidOperationException("FFmpeg could not be started.");
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });
            Task<string> error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException((await error).Trim());
            File.Move(extracted, FramePath, true);
        }
        finally { try { if (File.Exists(extracted)) File.Delete(extracted); } catch { } }
    }

    void TogglePlayback()
    {
        if (playback.Enabled) StopPlayback(); else StartPlayback();
    }

    void StartPlayback()
    {
        if (!allowFrameSelection || updatingMask) return;
        if (timeline.PositionMs >= durationMs) timeline.PositionMs = 0;
        previewDelay.Stop();
        previewCancellation?.Cancel();
        ResetMaskSelection();
        frameReady = false;
        playStartMs = timeline.PositionMs;
        playClock.Restart();
        playback.Start();
        play.Text = "Pause";
        streamCancellation?.Cancel();
        streamCancellation = new CancellationTokenSource();
        double speed = slowMotion.Checked ? .25 : 1;
        _ = Task.Run(() => StreamPreviewAsync(FrameMs, speed,
            streamCancellation.Token));
        UpdateEnabledState();
    }

    void StopPlayback(bool requestPreview = true)
    {
        playback.Stop();
        playClock.Stop();
        streamCancellation?.Cancel();
        streamCancellation = null;
        play.Text = "Play";
        if (requestPreview) RequestPreview();
        UpdateEnabledState();
    }

    void PlaybackTick()
    {
        timeline.PositionMs = Math.Min(durationMs,
            playStartMs + (long)Math.Round(playClock.ElapsedMilliseconds *
                (slowMotion.Checked ? .25 : 1)));
        if (timeline.PositionMs >= durationMs) StopPlayback();
    }

    void RestartPlayback()
    {
        if (!playback.Enabled) return;
        StopPlayback(requestPreview: false);
        StartPlayback();
    }

    void StepFrame(int direction)
    {
        if (!allowFrameSelection || updatingMask) return;
        StopPlayback(requestPreview: false);
        timeline.PositionMs = CustomClipEditorForm.FrameStep(timeline.PositionMs,
            direction, frameDurationMs, durationMs);
    }

    void UpdatePosition() => position.Text =
        $"{Format(timeline.PositionMs)} / {Format(durationMs)}";

    void RequestPreview()
    {
        if (!allowFrameSelection || !workerReady || playback.Enabled || closing) return;
        frameReady = false;
        ResetMaskSelection();
        UpdateEnabledState();
        status.Text = "Seeking selected frame...";
        previewDelay.Stop();
        previewDelay.Start();
    }

    async Task LoadSelectedFrameAsync(long atMs)
    {
        previewCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        previewCancellation = cancellation;
        try
        {
            await ExtractFrameAsync(atMs, cancellation.Token);
            if (cancellation.IsCancellationRequested || FrameMs != atMs) return;
            SetImage(LoadImage(FramePath));
            displayedFrameMs = atMs;
            frameReady = true;
            status.Text = $"Frame {Format(atMs - startMs)} ready; click the foreground to create its mask.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closing && !IsDisposed)
                status.Text = "Frame preview unavailable: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(previewCancellation, cancellation))
                previewCancellation = null;
            UpdateEnabledState();
        }
    }

    async Task StreamPreviewAsync(long atMs, double speed, CancellationToken token)
    {
        try
        {
            ProcessStartInfo start = PreviewProcessStart(atMs);
            start.ArgumentList.Add(speed == 1 ? "-re" : "-readrate");
            if (speed != 1)
                start.ArgumentList.Add(speed.ToString(CultureInfo.InvariantCulture));
            foreach (string argument in new[] { "-i", videoPath, "-t",
                Math.Max(.001, (endMs - atMs) / 1000d).ToString("0.###",
                    CultureInfo.InvariantCulture), "-an", "-vf",
                "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2",
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("FFmpeg did not start.");
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });
            _ = process.StandardError.ReadToEndAsync(token);
            byte[] frame = new byte[640 * 360 * 4];
            while (!token.IsCancellationRequested)
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(frame, token);
                Bitmap bitmap = FrameBitmap(frame, 640, 360);
                try
                {
                    if (!IsHandleCreated || IsDisposed) { bitmap.Dispose(); break; }
                    Invoke(() =>
                    {
                        if (token.IsCancellationRequested || !playback.Enabled)
                            bitmap.Dispose();
                        else SetImage(bitmap);
                    });
                }
                catch { bitmap.Dispose(); throw; }
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }

    ProcessStartInfo PreviewProcessStart(long atMs)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-v", "error", "-ss",
            (atMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        return start;
    }

    static Bitmap FrameBitmap(byte[] bytes, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < height; row++)
                Marshal.Copy(bytes, row * width * 4,
                    data.Scan0 + row * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    async void Image_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right) ||
            worker == null || worker.HasExited || image.Image == null ||
            !image.Enabled || updatingMask || displayedFrameMs != FrameMs) return;
        PointF? point = ImagePoint(e.Location);
        if (point == null) return;
        points.Add(point.Value);
        labels.Add(e.Button == MouseButtons.Right ? 0 : 1);
        await UpdateMaskAsync();
    }

    async Task EnsureWorkerFrameAsync()
    {
        if (displayedFrameMs != FrameMs || !frameReady)
            throw new InvalidOperationException("Wait for the selected frame to finish loading.");
        if (workerFrameMs == displayedFrameMs) return;
        status.Text = "Preparing the selected frame for SAM2...";
        await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            command = "load", image = FramePath
        }));
        await worker.StandardInput.FlushAsync();
        JsonElement response = await ReadResponseAsync();
        if (response.GetProperty("status").GetString() != "loaded")
            throw new InvalidOperationException(ResponseError(response));
        workerFrameMs = displayedFrameMs;
    }

    async Task UpdateMaskAsync(bool useAutomatic = false)
    {
        if (worker == null || worker.HasExited || updatingMask || !frameReady) return;
        updatingMask = true;
        UpdateEnabledState();
        status.Text = useAutomatic ? "Generating automatic SAM2 masks..." : "Updating mask...";
        try
        {
            await EnsureWorkerFrameAsync();
            string request = JsonSerializer.Serialize(new
            {
                command = useAutomatic ? "auto" : "prompt",
                points = points.Select(point => new[] { point.X, point.Y }),
                labels
            });
            await worker.StandardInput.WriteLineAsync(request);
            await worker.StandardInput.FlushAsync();
            JsonElement response = await ReadResponseAsync();
            if (response.GetProperty("status").GetString() != "mask")
                throw new InvalidOperationException(ResponseError(response));
            SetImage(LoadImage(PreviewPath));
            status.Text = useAutomatic && response.TryGetProperty("candidates", out JsonElement count)
                ? $"Automatic mask ready from {count.GetInt32()} candidates; refine with clicks if needed."
                : $"Mask ready · {points.Count} click{(points.Count == 1 ? "" : "s")}";
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed && !Disposing)
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            updatingMask = false;
            UpdateEnabledState();
        }
    }

    async Task<JsonElement> ReadResponseAsync()
    {
        await responseLock.WaitAsync();
        try
        {
            while (await worker!.StandardOutput.ReadLineAsync() is string line)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }
                catch (JsonException) { }
            }
            string detail = workerError == null ? "" : (await workerError).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "The mask worker stopped unexpectedly." : detail);
        }
        finally { responseLock.Release(); }
    }

    PointF? ImagePoint(Point location)
    {
        Image source = image.Image!;
        float scale = Math.Min(image.ClientSize.Width / (float)source.Width,
            image.ClientSize.Height / (float)source.Height);
        float left = (image.ClientSize.Width - source.Width * scale) / 2;
        float top = (image.ClientSize.Height - source.Height * scale) / 2;
        float x = (location.X - left) / scale, y = (location.Y - top) / scale;
        return x < 0 || y < 0 || x >= source.Width || y >= source.Height
            ? null : new PointF(x, y);
    }

    void ClearClicks()
    {
        points.Clear();
        labels.Clear();
        try { if (File.Exists(MaskPath)) File.Delete(MaskPath); } catch { }
        if (frameReady && File.Exists(FramePath)) SetImage(LoadImage(FramePath));
        status.Text = "Click one or more people";
        UpdateEnabledState();
    }

    void ResetMaskSelection()
    {
        points.Clear();
        labels.Clear();
        try { if (File.Exists(MaskPath)) File.Delete(MaskPath); } catch { }
        try { if (File.Exists(PreviewPath)) File.Delete(PreviewPath); } catch { }
    }

    void UpdateEnabledState()
    {
        if (closing || IsDisposed) return;
        bool editable = workerReady && frameReady && !updatingMask && !playback.Enabled;
        image.Enabled = clear.Enabled = automatic.Enabled = editable;
        accept.Enabled = editable && File.Exists(MaskPath);
        timeline.Enabled = previousFrame.Enabled = nextFrame.Enabled =
            slowMotion.Enabled = allowFrameSelection && workerReady && !updatingMask;
        play.Enabled = allowFrameSelection && workerReady && !updatingMask;
    }

    void SetImage(Image next)
    {
        Image? old = image.Image;
        image.Image = next;
        old?.Dispose();
    }

    static Image LoadImage(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Image source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    static bool IsMostlyBlack(Image source)
    {
        using Bitmap bitmap = new(source);
        long brightness = 0;
        int samples = 0;
        int stepX = Math.Max(1, bitmap.Width / 40);
        int stepY = Math.Max(1, bitmap.Height / 40);
        for (int y = 0; y < bitmap.Height; y += stepY)
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color pixel = bitmap.GetPixel(x, y);
                brightness += pixel.R + pixel.G + pixel.B;
                samples++;
            }
        return brightness / Math.Max(1, samples * 3) < 12;
    }

    static string Format(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(
            milliseconds >= 3_600_000 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");

    internal static bool VerifyFrameSelection()
    {
        using Bitmap black = new(4, 4);
        using Bitmap white = new(4, 4);
        using Graphics graphics = Graphics.FromImage(white);
        graphics.Clear(Color.White);
        return IsMostlyBlack(black) && !IsMostlyBlack(white);
    }

    static string ResponseError(JsonElement response) =>
        response.TryGetProperty("message", out JsonElement message)
            ? message.GetString() ?? "Mask generation failed." : "Mask generation failed.";

    void StopWorker()
    {
        try { if (worker is { HasExited: false }) worker.Kill(true); } catch { }
        worker?.Dispose();
        worker = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            previewDelay.Dispose();
            playback.Dispose();
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            streamCancellation?.Cancel();
            streamCancellation?.Dispose();
            StopWorker();
            responseLock.Dispose();
            image.Image?.Dispose();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
