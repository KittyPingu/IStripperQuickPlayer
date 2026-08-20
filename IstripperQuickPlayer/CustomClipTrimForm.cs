namespace IStripperQuickPlayer;

internal sealed class CustomClipTrimForm : Form
{
    readonly string foregroundPath;
    readonly string alphaPath;
    readonly long durationMs;
    readonly long frameMs;
    readonly int alphaThreshold;
    readonly int fullOpacityThreshold;
    readonly float edgeChokePixels;
    readonly Controls.PlaybackSeekBar timeline = new()
    {
        Minimum = 0,
        SmallChange = 1,
        LargeChange = 5_000,
        ShowTimeToolTip = true,
        Dock = DockStyle.Fill,
        AccessibleName = "Trim playback position"
    };
    readonly Label positionLabel = new() { AutoSize = true };
    readonly Label rangeLabel = new() { AutoSize = true };
    readonly Label previewStatus = new()
    {
        AutoSize = true,
        Text = "Opening DirectComposition preview…"
    };
    readonly Button playPause = new() { Text = "Play", AutoSize = true };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 33 };
    readonly List<CustomPlayerForm> previews = [];
    CustomPlayerForm? preview;
    bool previewEnded;
    bool dragging;

    internal long StartMs { get; private set; }
    internal long EndMs { get; private set; }

    internal CustomClipTrimForm(string title, string foregroundPath,
        string alphaPath, CustomClipMedia media, int alphaThreshold,
        int fullOpacityThreshold, float edgeChokePixels)
    {
        this.foregroundPath = foregroundPath;
        this.alphaPath = alphaPath;
        durationMs = media.DurationMs;
        this.alphaThreshold = alphaThreshold;
        this.fullOpacityThreshold = fullOpacityThreshold;
        this.edgeChokePixels = edgeChokePixels;
        frameMs = CustomShowStore.TryFrameRate(media.FrameRate, out double fps)
            ? Math.Max(1, (long)Math.Ceiling(1000d / fps)) : 1;
        StartMs = media.PlaybackStartMs;
        EndMs = CustomShowStore.PlaybackEnd(media);

        Text = "Trim Custom Clip — " + title;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 255);
        ClientSize = new Size(900, 255);
        ShowInTaskbar = false;
        KeyPreview = true;

        timeline.Maximum = checked((int)Math.Min(int.MaxValue, durationMs));
        timeline.Value = ToTimeline(StartMs);
        timeline.ToolTipFormatter = value => FormatTime(value);
        timeline.Scroll += (_, _) => SeekFromTimeline();
        timeline.MouseDown += (_, _) => dragging = true;
        timeline.MouseUp += (_, _) => dragging = false;

        Button previousFrame = new() { Text = "◀ 1 frame", AutoSize = true };
        Button nextFrame = new() { Text = "1 frame ▶", AutoSize = true };
        Button goStart = new() { Text = "Go to start", AutoSize = true };
        Button goEnd = new() { Text = "Go to end", AutoSize = true };
        Button setStart = new() { Text = "Set start", AutoSize = true };
        Button setEnd = new() { Text = "Set end", AutoSize = true };
        Button reset = new() { Text = "Reset to full clip", AutoSize = true };
        Button apply = new() { Text = "Apply Trim", AutoSize = true };
        Button cancel = new()
        {
            Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel
        };

        _ = new HoldRepeatButton(previousFrame, () => Step(-frameMs));
        _ = new HoldRepeatButton(nextFrame, () => Step(frameMs));
        goStart.Click += (_, _) => Seek(StartMs);
        goEnd.Click += (_, _) => Seek(Math.Max(StartMs, EndMs - frameMs));
        setStart.Click += (_, _) => SetStartMarker();
        setEnd.Click += (_, _) => SetEndMarker();
        reset.Click += (_, _) =>
        {
            StartMs = 0;
            EndMs = durationMs;
            UpdateRangeDisplay();
        };
        playPause.Click += (_, _) => TogglePlayback();
        apply.Click += (_, _) => DialogResult = DialogResult.OK;

        Label instruction = new()
        {
            AutoSize = true,
            Text = "Move through the processed clip, then set the playback start and end markers."
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14, 12, 14, 10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(instruction, 0, 0);
        layout.Controls.Add(timeline, 0, 1);

        FlowLayoutPanel transport = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 3)
        };
        transport.Controls.AddRange([
            previousFrame, playPause, nextFrame, goStart, goEnd, positionLabel]);
        layout.Controls.Add(transport, 0, 2);

        FlowLayoutPanel markers = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 3)
        };
        markers.Controls.AddRange([setStart, setEnd, reset, rangeLabel]);
        layout.Controls.Add(markers, 0, 3);
        layout.Controls.Add(previewStatus, 0, 4);

        FlowLayoutPanel decisions = new()
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        decisions.Controls.AddRange([cancel, apply]);
        layout.Controls.Add(decisions, 0, 5);
        Controls.Add(layout);
        AcceptButton = apply;
        CancelButton = cancel;

        updateTimer.Tick += (_, _) => UpdatePlaybackPosition();
        Shown += (_, _) =>
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Location = new Point(
                work.Left + Math.Max(0, (work.Width - Width) / 2),
                work.Top + 24);
            CreatePreview(StartMs, paused: true);
            updateTimer.Start();
        };
        FormClosing += (_, _) =>
        {
            updateTimer.Stop();
            foreach (CustomPlayerForm player in previews) player.ClosePlayer();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space) return;
            TogglePlayback();
            e.Handled = true;
        };
        UpdateRangeDisplay();
        AppTheme.Apply(this);
    }

    void CreatePreview(long positionMs, bool paused)
    {
        preview?.ClosePlayer();
        CustomPlayerForm player = new(foregroundPath, alphaPath,
            playerSizePercent: 45, volumePercent: 0,
            alphaThreshold: alphaThreshold,
            fullOpacityThreshold: fullOpacityThreshold,
            suppressErrorDialog: true, startMs: 0, endMs: durationMs,
            edgeChokePixels: edgeChokePixels);
        player.HoldFinalFrameOnCompletion = true;
        player.SetPaused(paused);
        player.SeekTo(positionMs / 1000d);
        player.FirstFramePresented += (_, _) => BeginInvoke(() =>
        {
            if (preview == player)
                previewStatus.Text =
                    "DirectComposition preview (drag the preview to reposition it)";
        });
        player.PlaybackCompleted += (_, _) => BeginInvoke(() =>
        {
            if (preview != player) return;
            previewEnded = true;
            playPause.Text = "Play";
        });
        player.PlaybackFailed += (_, error) => BeginInvoke(() =>
        {
            if (preview == player)
                previewStatus.Text = "Preview failed: " + error.Message;
        });
        previews.Add(player);
        preview = player;
        previewEnded = false;
        playPause.Text = paused ? "Play" : "Pause";
        player.Show(this);
    }

    void SeekFromTimeline()
    {
        Seek(timeline.Value);
    }

    void Seek(long positionMs)
    {
        positionMs = Math.Clamp(positionMs, 0, durationMs);
        timeline.Value = ToTimeline(positionMs);
        if (previewEnded)
            CreatePreview(positionMs, paused: true);
        else
        {
            preview?.SetPaused(true);
            preview?.SeekTo(positionMs / 1000d);
            playPause.Text = "Play";
        }
        UpdatePositionLabel(positionMs);
    }

    void Step(long deltaMs)
    {
        Seek(Math.Clamp((long)timeline.Value + deltaMs, 0, durationMs));
    }

    void TogglePlayback()
    {
        if (preview == null) return;
        if (previewEnded)
        {
            long restart = timeline.Value >= EndMs ? StartMs : timeline.Value;
            CreatePreview(restart, paused: false);
            return;
        }
        if (preview.Paused && timeline.Value >= EndMs)
            Seek(StartMs);
        preview.SetPaused(!preview.Paused);
        playPause.Text = preview.Paused ? "Play" : "Pause";
    }

    void SetStartMarker()
    {
        long requested = timeline.Value;
        if (requested > EndMs - frameMs)
        {
            MessageBox.Show(this, "The start marker must be before the end marker.",
                "Trim Custom Clip", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        StartMs = requested;
        UpdateRangeDisplay();
    }

    void SetEndMarker()
    {
        long requested = timeline.Value;
        if (requested < StartMs + frameMs)
        {
            MessageBox.Show(this, "The end marker must be after the start marker.",
                "Trim Custom Clip", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        EndMs = requested;
        UpdateRangeDisplay();
    }

    void UpdatePlaybackPosition()
    {
        if (preview == null || previewEnded) return;
        long position = Math.Clamp(
            (long)Math.Round(preview.CurrentSeconds * 1000), 0, durationMs);
        if (!preview.Paused && position >= EndMs)
        {
            preview.SetPaused(true);
            preview.SeekTo(EndMs / 1000d);
            position = EndMs;
            playPause.Text = "Play";
        }
        if (!dragging) timeline.Value = ToTimeline(position);
        UpdatePositionLabel(position);
    }

    void UpdateRangeDisplay()
    {
        timeline.RangeStart = ToTimeline(StartMs);
        timeline.RangeEnd = ToTimeline(EndMs);
        rangeLabel.Text = $"Start {FormatTime(StartMs)}   End {FormatTime(EndMs)}" +
            $"   Selected {FormatTime(EndMs - StartMs)}";
        UpdatePositionLabel(timeline.Value);
    }

    void UpdatePositionLabel(long positionMs) =>
        positionLabel.Text = $"  {FormatTime(positionMs)} / {FormatTime(durationMs)}";

    int ToTimeline(long milliseconds) =>
        (int)Math.Clamp(milliseconds, timeline.Minimum, timeline.Maximum);

    static string FormatTime(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}"
            : $"{value.Minutes}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    internal async Task ClosePreviewAsync()
    {
        updateTimer.Stop();
        await Task.WhenAll(previews.Select(player => player.ClosePlayerAsync()));
        foreach (CustomPlayerForm player in previews) player.Dispose();
        previews.Clear();
        preview = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) updateTimer.Dispose();
        base.Dispose(disposing);
    }
}
