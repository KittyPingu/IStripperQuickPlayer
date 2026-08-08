using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomMaskEditorForm : Form
{
    readonly string videoPath;
    readonly CustomShowConfiguration configuration;
    readonly long startMs;
    readonly long endMs;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-mask-" + Guid.NewGuid().ToString("N"));
    readonly PictureBox image = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
        Enabled = false,
        AccessibleName = "Initial foreground mask image" };
    readonly Label status = new() { AutoSize = true, Padding = new Padding(8) };
    readonly Button clear = new() { Text = "Clear clicks", AutoSize = true, Enabled = false };
    readonly Button automatic = new() { Text = "Auto mask frame", AutoSize = true,
        Enabled = false };
    readonly Button accept = new() { Text = "Use this mask", AutoSize = true, Enabled = false };
    readonly List<PointF> points = [];
    readonly List<int> labels = [];
    readonly SemaphoreSlim responseLock = new(1, 1);
    Process? worker;
    Task<string>? workerError;
    bool updatingMask;
    bool closing;
    internal long FrameMs { get; private set; }
    string FramePath => Path.Combine(temporary, "first-frame.png");
    string MaskPath => Path.Combine(temporary, "initial-mask.png");
    string PreviewPath => Path.Combine(temporary, "mask-preview.jpg");

    internal CustomMaskEditorForm(string videoPath,
        CustomShowConfiguration configuration, long startMs, long endMs,
        string? clipLabel = null, string algorithm = "MatAnyone 2")
    {
        this.videoPath = videoPath;
        this.configuration = configuration;
        this.startMs = startMs;
        this.endMs = endMs;
        FrameMs = startMs;
        Text = clipLabel == null ? $"Create {algorithm} Initial Mask" :
            $"Create {algorithm} Initial Mask - {clipLabel}";
        ClientSize = new Size(1050, 780);
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        Label instructions = new() { Dock = DockStyle.Top, Height = 44,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0),
            Text = "Left-click each person to include. Right-click background or unwanted areas to exclude." };
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 48,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([clear, automatic, accept, cancel, status]);
        Controls.Add(image);
        Controls.Add(instructions);
        Controls.Add(actions);
        CancelButton = cancel;
        image.MouseClick += Image_MouseClick;
        clear.Click += (_, _) => ClearClicks();
        automatic.Click += async (_, _) => await UpdateMaskAsync(true);
        accept.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Shown += async (_, _) => await StartWorkerAsync();
        FormClosing += (_, _) => { closing = true; StopWorker(); };
        AppTheme.Apply(this);
    }

    internal void SaveMask(string destination)
    {
        if (!File.Exists(MaskPath)) throw new InvalidOperationException("Create an initial mask first.");
        File.Copy(MaskPath, destination, true);
    }

    async Task StartWorkerAsync()
    {
        try
        {
            status.Text = "Extracting first frame...";
            Directory.CreateDirectory(temporary);
            await ExtractFrameAsync();
            image.Image = LoadImage(FramePath);
            if (IsMostlyBlack(image.Image))
            {
                FrameMs = startMs + Math.Min(3_000, (endMs - startMs) / 2);
                image.Image.Dispose();
                await ExtractFrameAsync();
                image.Image = LoadImage(FramePath);
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
                "--image", FramePath, "--mask", MaskPath, "--preview", PreviewPath })
                start.ArgumentList.Add(argument);
            worker = Process.Start(start) ?? throw new InvalidOperationException("Python could not be started.");
            workerError = worker.StandardError.ReadToEndAsync();
            JsonElement ready = await ReadResponseAsync();
            if (ready.GetProperty("status").GetString() != "ready")
                throw new InvalidOperationException(ResponseError(ready));
            status.Text = $"{ready.GetProperty("checkpoint").GetString()} ready on " +
                $"{ready.GetProperty("device").GetString()?.ToUpperInvariant()}/" +
                ready.GetProperty("precision").GetString();
            image.Enabled = clear.Enabled = automatic.Enabled = true;
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

    async Task ExtractFrameAsync()
    {
        ProcessStartInfo extract = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-y", "-v", "error", "-ss",
            (FrameMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
            "-frames:v", "1", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,setsar=1",
            FramePath }) extract.ArgumentList.Add(argument);
        using Process process = Process.Start(extract) ??
            throw new InvalidOperationException("FFmpeg could not be started.");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
    }

    async void Image_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right) ||
            worker == null || worker.HasExited || image.Image == null ||
            !image.Enabled || updatingMask) return;
        PointF? point = ImagePoint(e.Location);
        if (point == null) return;
        points.Add(point.Value);
        labels.Add(e.Button == MouseButtons.Right ? 0 : 1);
        await UpdateMaskAsync();
    }

    async Task UpdateMaskAsync(bool useAutomatic = false)
    {
        if (worker == null || worker.HasExited || updatingMask) return;
        updatingMask = true;
        image.Enabled = clear.Enabled = false;
        automatic.Enabled = false;
        accept.Enabled = false;
        status.Text = useAutomatic ? "Generating automatic SAM2 masks..." : "Updating mask...";
        try
        {
            string request = JsonSerializer.Serialize(new
            {
                command = useAutomatic ? "auto" : "prompt",
                points = points.Select(point => new[] { point.X, point.Y }),
                labels
            });
            await worker!.StandardInput.WriteLineAsync(request);
            await worker.StandardInput.FlushAsync();
            JsonElement response = await ReadResponseAsync();
            if (response.GetProperty("status").GetString() != "mask")
                throw new InvalidOperationException(ResponseError(response));
            Image next = LoadImage(PreviewPath);
            Image? old = image.Image;
            image.Image = next;
            old?.Dispose();
            accept.Enabled = true;
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
            if (!closing && !IsDisposed)
                image.Enabled = clear.Enabled = automatic.Enabled = true;
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
        points.Clear(); labels.Clear(); accept.Enabled = false;
        Image next = LoadImage(FramePath);
        Image? old = image.Image; image.Image = next; old?.Dispose();
        status.Text = "Click one or more people";
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
        worker?.Dispose(); worker = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopWorker();
            image.Image?.Dispose();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
