using System.Diagnostics;
using System.Globalization;

namespace IStripperQuickPlayer;

internal sealed class CustomVideoComparisonForm : Form
{
    const int PreviewWidth = 1280;
    const int PreviewHeight = 360;

    readonly string sourcePath;
    readonly string outputPath;
    readonly long durationMs;
    readonly long frameDurationMs;
    readonly DxgiMaskPreviewControl preview = new()
    {
        Dock = DockStyle.Fill,
        AccessibleName = "Synchronized original and stabilized video comparison"
    };
    readonly Controls.PlaybackSeekBar timeline = new()
    {
        Dock = DockStyle.Fill,
        Height = 36,
        AccessibleName = "Comparison playback position"
    };
    readonly Button rewind = new() { Text = "-5 seconds", AutoSize = true };
    readonly Button previousFrame = new() { Text = "Previous frame", AutoSize = true };
    readonly Button play = new() { Text = "Play", AutoSize = true };
    readonly Button nextFrame = new() { Text = "Next frame", AutoSize = true };
    readonly Button forward = new() { Text = "+5 seconds", AutoSize = true };
    readonly Label position = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    readonly Label state = new()
    {
        AutoSize = true,
        Text = "Both panes use one synchronized FFmpeg playback graph. Audio is muted.",
        Padding = new Padding(8, 7, 0, 0)
    };
    readonly System.Windows.Forms.Timer clock = new() { Interval = 50 };
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 120 };
    readonly Stopwatch playClock = new();
    CancellationTokenSource? decodeCancellation;
    long playStartMs;
    bool playing;
    bool scrubbing;
    bool resumeAfterScrub;

    internal CustomVideoComparisonForm(string sourcePath, string outputPath,
        long durationMs, string frameRate)
    {
        this.sourcePath = sourcePath;
        this.outputPath = outputPath;
        this.durationMs = Math.Max(1, durationMs);
        double fps = ParseRate(frameRate);
        frameDurationMs = Math.Max(1, checked((long)Math.Round(1000 / fps)));

        Text = "Review Stabilization - Original vs Stabilized";
        ClientSize = new Size(1320, 690);
        MinimumSize = new Size(780, 500);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        timeline.Minimum = 0;
        timeline.Maximum = checked((int)Math.Min(int.MaxValue, this.durationMs));
        timeline.SmallChange = checked((int)Math.Min(int.MaxValue, frameDurationMs));
        timeline.LargeChange = Math.Min(5_000, timeline.Maximum);
        timeline.ShowTimeToolTip = true;

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel headings = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 5)
        };
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        headings.Controls.Add(Heading("Original input"), 0, 0);
        headings.Controls.Add(Heading("Stabilized output"), 1, 0);
        root.Controls.Add(headings, 0, 0);
        root.Controls.Add(preview, 0, 1);
        root.Controls.Add(timeline, 0, 2);

        FlowLayoutPanel controls = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        controls.Controls.AddRange(
            [rewind, previousFrame, play, nextFrame, forward, position, state]);
        root.Controls.Add(controls, 0, 3);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        Button close = new()
        {
            Text = "Close",
            AutoSize = true,
            DialogResult = DialogResult.OK
        };
        actions.Controls.Add(close);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        CancelButton = close;

        rewind.Click += (_, _) => SeekBy(-5_000);
        previousFrame.Click += (_, _) => SeekBy(-frameDurationMs);
        play.Click += (_, _) => TogglePlayback();
        nextFrame.Click += (_, _) => SeekBy(frameDurationMs);
        forward.Click += (_, _) => SeekBy(5_000);
        timeline.Scroll += (_, _) => TimelineChanged();
        timeline.MouseDown += (_, _) => BeginScrub();
        timeline.MouseUp += (_, _) => EndScrub();
        timeline.KeyDown += (_, _) => BeginScrub();
        timeline.KeyUp += (_, _) => EndScrub();
        clock.Tick += (_, _) => PlaybackTick();
        previewDelay.Tick += (_, _) =>
        {
            previewDelay.Stop();
            if (!playing)
                _ = ShowFrameAsync(timeline.Value);
        };
        Shown += (_, _) =>
        {
            UpdatePosition();
            _ = ShowFrameAsync(0);
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                TogglePlayback();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Left)
            {
                SeekBy(e.Control ? -5_000 : -frameDurationMs);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Right)
            {
                SeekBy(e.Control ? 5_000 : frameDurationMs);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        AppTheme.Apply(this);
    }

    static Label Heading(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold)
    };

    void TogglePlayback()
    {
        if (playing) PausePlayback(); else StartPlayback();
    }

    void StartPlayback()
    {
        if (timeline.Value >= timeline.Maximum)
            timeline.Value = 0;
        previewDelay.Stop();
        CancelDecode();
        playing = true;
        play.Text = "Pause";
        playStartMs = timeline.Value;
        playClock.Restart();
        clock.Start();
        decodeCancellation = new CancellationTokenSource();
        _ = StreamComparisonAsync(playStartMs, decodeCancellation.Token);
    }

    void PausePlayback(bool requestFrame = true)
    {
        playing = false;
        play.Text = "Play";
        clock.Stop();
        playClock.Stop();
        CancelDecode();
        if (requestFrame) RequestFrame();
    }

    void PlaybackTick()
    {
        long requested = playStartMs + playClock.ElapsedMilliseconds;
        timeline.Value = checked((int)Math.Min(timeline.Maximum, requested));
        UpdatePosition();
        if (timeline.Value >= timeline.Maximum)
            PausePlayback();
    }

    void SeekBy(long change)
    {
        bool resume = playing;
        if (playing) PausePlayback(requestFrame: false);
        timeline.Value = checked((int)Math.Clamp((long)timeline.Value + change,
            timeline.Minimum, timeline.Maximum));
        UpdatePosition();
        if (resume) StartPlayback(); else RequestFrame();
    }

    void BeginScrub()
    {
        if (scrubbing) return;
        scrubbing = true;
        resumeAfterScrub = playing;
        if (playing) PausePlayback(requestFrame: false);
    }

    void TimelineChanged()
    {
        UpdatePosition();
        RequestFrame();
    }

    void EndScrub()
    {
        if (!scrubbing) return;
        scrubbing = false;
        if (resumeAfterScrub) StartPlayback(); else RequestFrame();
        resumeAfterScrub = false;
    }

    void RequestFrame()
    {
        if (playing) return;
        previewDelay.Stop();
        previewDelay.Start();
    }

    async Task ShowFrameAsync(long atMs)
    {
        if (playing) return;
        CancelDecode();
        CancellationTokenSource cancellation = new();
        decodeCancellation = cancellation;
        try
        {
            ProcessStartInfo start = ComparisonStart(atMs, realtime: false);
            start.ArgumentList.Add("-frames:v");
            start.ArgumentList.Add("1");
            AddRawOutput(start);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException(
                    "FFmpeg comparison playback could not start.");
            using CancellationTokenRegistration registration =
                cancellation.Token.Register(() => Kill(process));
            Task<string> error = process.StandardError.ReadToEndAsync(cancellation.Token);
            byte[] bytes = new byte[PreviewWidth * PreviewHeight * 4];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes,
                cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(await error);
            preview.SetSourceBgraOwned(bytes, PreviewWidth, PreviewHeight);
            state.Text = "Paused - drag the time bar to compare any frame. Audio is muted.";
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException)
        {
            if (!cancellation.IsCancellationRequested)
                state.Text = "A comparison frame could not be decoded at this position.";
        }
        catch (Exception error)
        {
            if (!IsDisposed && !cancellation.IsCancellationRequested)
                state.Text = "Comparison unavailable: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(decodeCancellation, cancellation))
                decodeCancellation = null;
            cancellation.Dispose();
        }
    }

    async Task StreamComparisonAsync(long atMs, CancellationToken token)
    {
        try
        {
            ProcessStartInfo start = ComparisonStart(atMs, realtime: true);
            AddRawOutput(start);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException(
                    "FFmpeg comparison playback could not start.");
            using CancellationTokenRegistration registration =
                token.Register(() => Kill(process));
            _ = process.StandardError.ReadToEndAsync(token);
            int length = PreviewWidth * PreviewHeight * 4;
            while (!token.IsCancellationRequested)
            {
                byte[] bytes = new byte[length];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token);
                if (!IsHandleCreated || IsDisposed) break;
                preview.SetSourceBgraOwned(bytes, PreviewWidth, PreviewHeight);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }

    ProcessStartInfo ComparisonStart(long atMs, bool realtime)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-nostdin");
        if (realtime) start.ArgumentList.Add("-re");
        AddSeekAndInput(start, atMs, sourcePath);
        AddSeekAndInput(start, atMs, outputPath);
        start.ArgumentList.Add("-filter_complex");
        start.ArgumentList.Add(
            "[0:v]scale=640:360:force_original_aspect_ratio=decrease," +
            "pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1,setpts=PTS-STARTPTS[left];" +
            "[1:v]scale=640:360:force_original_aspect_ratio=decrease," +
            "pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1,setpts=PTS-STARTPTS[right];" +
            "[left][right]hstack=inputs=2," +
            "drawbox=x=639:y=0:w=2:h=ih:color=white@0.5:t=fill[out]");
        start.ArgumentList.Add("-map");
        start.ArgumentList.Add("[out]");
        start.ArgumentList.Add("-an");
        return start;
    }

    static void AddSeekAndInput(ProcessStartInfo start, long atMs, string path)
    {
        start.ArgumentList.Add("-ss");
        start.ArgumentList.Add((atMs / 1000d).ToString("0.###",
            CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);
    }

    static void AddRawOutput(ProcessStartInfo start)
    {
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("rawvideo");
        start.ArgumentList.Add("-pix_fmt");
        start.ArgumentList.Add("bgra");
        start.ArgumentList.Add("pipe:1");
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }

    void UpdatePosition() => position.Text =
        $"{FormatTime(timeline.Value)} / {FormatTime(durationMs)}";

    static string FormatTime(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(milliseconds);
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds / 100}"
            : $"{value.Minutes}:{value.Seconds:00}.{value.Milliseconds / 100}";
    }

    static double ParseRate(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double denominator) && denominator > 0)
            return Math.Max(1, numerator / denominator);
        return double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double rate) && rate > 0 ? rate : 30;
    }

    void CancelDecode()
    {
        CancellationTokenSource? cancellation = decodeCancellation;
        decodeCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            clock.Stop();
            previewDelay.Stop();
            CancelDecode();
        }
        base.Dispose(disposing);
    }
}
