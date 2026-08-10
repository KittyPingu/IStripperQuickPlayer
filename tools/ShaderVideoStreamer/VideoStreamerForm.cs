using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShaderVideoStreamer;

internal sealed class VideoStreamerForm : Form
{
    private static readonly string[] GirlSources =
        ["GirlLeft", "GirlCentre", "GirlRight"];
    private static readonly string[] GirlLabels =
        ["Left girl", "Centre girl", "Right girl"];
    private static string RecentVideoPath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "shader-video-path.txt");
    private static string ControlSettingsPath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "shader-video-settings.json");

    private readonly Options defaults;
    private readonly TextBox videoPath = new() { Dock = DockStyle.Fill };
    private readonly CheckBox loop = new()
    {
        Text = "Loop",
        Checked = true,
        AutoSize = true,
        Margin = new Padding(12, 8, 12, 0)
    };
    private readonly Button browse = new() { Text = "Browse...", AutoSize = true };
    private readonly Button start = new() { Text = "Start", AutoSize = true };
    private readonly Button stop = new()
    {
        Text = "Stop",
        AutoSize = true,
        Enabled = false
    };
    private readonly TrackBar playbackTimeline = new()
    {
        Minimum = 0,
        Maximum = 10_000,
        Value = 0,
        TickStyle = TickStyle.None,
        AutoSize = false,
        Height = 30,
        Dock = DockStyle.Fill,
        Enabled = false,
        Margin = new Padding(12, 2, 8, 0)
    };
    private readonly Label playbackTime = new()
    {
        Text = "0:00 / 0:00",
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        MinimumSize = new Size(92, 0),
        TextAlign = ContentAlignment.MiddleRight,
        Margin = new Padding(0, 7, 0, 0)
    };
    private readonly TrackBar[] brightness =
        [Slider(50), Slider(50), Slider(50)];
    private readonly TrackBar[] purpleness =
        [Slider(75), Slider(75), Slider(75)];
    private readonly TrackBar videoBrightness = Slider(100, 200);
    private readonly TrackBar videoBlackAndWhite = Slider(100);
    private readonly TrackBar sceneBrightness = Slider(100);
    private readonly Label videoBrightnessValue = ValueLabel();
    private readonly Label videoBlackAndWhiteValue = ValueLabel();
    private readonly Label sceneBrightnessValue = ValueLabel();
    private readonly Label[] brightnessValues =
        [ValueLabel(), ValueLabel(), ValueLabel()];
    private readonly Label[] purplenessValues =
        [ValueLabel(), ValueLabel(), ValueLabel()];
    private readonly Button[] nextCard =
    [
        new() { Text = "Next card", AutoSize = true },
        new() { Text = "Next card", AutoSize = true },
        new() { Text = "Next card", AutoSize = true }
    ];
    private readonly System.Windows.Forms.Timer lightingUpdateTimer = new()
    {
        Interval = 40
    };
    private readonly System.Windows.Forms.Timer settingsSaveTimer = new()
    {
        Interval = 300
    };
    private readonly SemaphoreSlim lightingUpdateLock = new(1, 1);
    private Process? streamer;
    private HttpClient? apiClient;
    private bool apiReady;
    private double playbackDurationSeconds;
    private bool timelineDragging;
    private bool updatingTimeline;

    public VideoStreamerForm(Options defaults)
    {
        this.defaults = defaults;
        Text = "QuickPlayer Video and Scene Lighting";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 510);
        Size = new Size(987, 663);

        loop.Checked = defaults.Loop;
        LoadControlSettings();
        try
        {
            if (File.Exists(RecentVideoPath))
                videoPath.Text = File.ReadAllText(RecentVideoPath).Trim();
        }
        catch { }

        Controls.Add(BuildLayout());
        browse.Click += BrowseClicked;
        start.Click += StartClicked;
        stop.Click += (_, _) => StopStreamer();
        lightingUpdateTimer.Tick += LightingUpdateTimerTick;
        settingsSaveTimer.Tick += (_, _) => SaveControlSettings();
        playbackTimeline.MouseDown += (_, _) => timelineDragging = true;
        playbackTimeline.MouseUp += (_, _) =>
        {
            timelineDragging = false;
            SeekToTimeline();
        };
        playbackTimeline.Scroll += (_, _) => TimelineScrolled();
        playbackTimeline.KeyUp += (_, _) => SeekToTimeline();
        loop.CheckedChanged += (_, _) => QueueSettingsSave();
        videoBrightness.ValueChanged += (_, _) => VideoControlsChanged();
        videoBlackAndWhite.ValueChanged += (_, _) => VideoControlsChanged();
        sceneBrightness.ValueChanged += (_, _) => VideoControlsChanged();
        UpdateVideoValueLabels();
        for (int index = 0; index < GirlSources.Length; index++)
        {
            int girlIndex = index;
            brightness[index].ValueChanged += (_, _) =>
                LightingChanged(girlIndex);
            purpleness[index].ValueChanged += (_, _) =>
                LightingChanged(girlIndex);
            nextCard[index].Click += async (_, _) =>
                await NextCardAsync(girlIndex);
            UpdateValueLabels(index);
        }
        FormClosing += (_, _) =>
        {
            SaveControlSettings();
            StopStreamer();
        };
        Shown += FormShown;
    }

    private Control BuildLayout()
    {
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildVideoSection(), 0, 0);
        layout.Controls.Add(BuildVideoAppearanceSection(), 0, 1);
        layout.Controls.Add(BuildSceneAppearanceSection(), 0, 2);
        layout.Controls.Add(BuildPerformerSection(), 0, 3);
        return layout;
    }

    private Control BuildVideoSection()
    {
        TableLayoutPanel fields = SectionTable(3, 2);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        fields.Controls.Add(TextLabel("File"), 0, 0);
        fields.Controls.Add(videoPath, 1, 0);
        fields.Controls.Add(browse, 2, 0);

        TableLayoutPanel playback = SectionTable(2, 1);
        playback.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        playback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        playback.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        playback.Margin = new Padding(0, 6, 0, 0);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(start);
        buttons.Controls.Add(stop);
        buttons.Controls.Add(loop);
        playback.Controls.Add(buttons, 0, 0);

        TableLayoutPanel timeline = SectionTable(2, 1);
        timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timeline.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timeline.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        timeline.Controls.Add(playbackTimeline, 0, 0);
        timeline.Controls.Add(playbackTime, 1, 0);
        playback.Controls.Add(timeline, 1, 0);
        fields.Controls.Add(playback, 0, 1);
        fields.SetColumnSpan(playback, 3);
        return Section("Video", fields);
    }

    private Control BuildVideoAppearanceSection()
    {
        TableLayoutPanel fields = SectionTable(2, 1);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(SliderField(
            "Brightness", videoBrightness, videoBrightnessValue), 0, 0);
        fields.Controls.Add(SliderField(
            "Black && white", videoBlackAndWhite,
            videoBlackAndWhiteValue), 1, 0);
        return Section("Video appearance", fields);
    }

    private Control BuildSceneAppearanceSection()
    {
        TableLayoutPanel fields = SectionTable(1, 1);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(SliderField(
            "Brightness", sceneBrightness, sceneBrightnessValue), 0, 0);
        return Section("Whole scene", fields);
    }

    private Control BuildPerformerSection()
    {
        TableLayoutPanel fields = SectionTable(4, 4);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int row = 0; row < fields.RowCount; row++)
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        fields.Controls.Add(HeaderLabel("Performer"), 0, 0);
        fields.Controls.Add(HeaderLabel("Brightness"), 1, 0);
        fields.Controls.Add(HeaderLabel("Purpleness"), 2, 0);

        for (int index = 0; index < GirlLabels.Length; index++)
        {
            int row = index + 1;
            fields.Controls.Add(TextLabel(GirlLabels[index]), 0, row);
            fields.Controls.Add(SliderWithValue(
                brightness[index], brightnessValues[index]), 1, row);
            fields.Controls.Add(SliderWithValue(
                purpleness[index], purplenessValues[index]), 2, row);
            nextCard[index].Anchor = AnchorStyles.Right;
            fields.Controls.Add(nextCard[index], 3, row);
        }
        return Section("Performer lighting", fields);
    }

    private static TableLayoutPanel SectionTable(int columns, int rows) =>
        new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            RowCount = rows,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

    private static Control Section(string title, Control content)
    {
        GroupBox group = new()
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 8, 10, 10),
            Margin = new Padding(0, 0, 0, 10)
        };
        group.Controls.Add(content);
        return group;
    }

    private static Control SliderField(
        string title, TrackBar slider, Label value)
    {
        TableLayoutPanel fields = SectionTable(1, 2);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Margin = new Padding(0, 0, 12, 0);
        fields.Controls.Add(HeaderLabel(title), 0, 0);
        fields.Controls.Add(SliderWithValue(slider, value), 0, 1);
        return fields;
    }

    private static Control SliderWithValue(TrackBar slider, Label value)
    {
        TableLayoutPanel fields = SectionTable(2, 1);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(slider, 0, 0);
        fields.Controls.Add(value, 1, 0);
        return fields;
    }

    private static TrackBar Slider(int value, int maximum = 100) => new()
    {
        Minimum = 0,
        Maximum = maximum,
        Value = value,
        TickFrequency = 10,
        SmallChange = 1,
        LargeChange = 10,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = 42,
        Margin = new Padding(0, 0, 8, 0)
    };

    private static Label HeaderLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
        Margin = new Padding(3, 4, 10, 3)
    };

    private static Label TextLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 10, 3)
    };

    private static Label ValueLabel() => new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        MinimumSize = new Size(42, 0),
        TextAlign = ContentAlignment.MiddleRight
    };

    private async void FormShown(object? sender, EventArgs eventArgs)
    {
        try
        {
            string token = File.ReadAllText(defaults.TokenPath).Trim();
            apiClient = new HttpClient
            {
                BaseAddress = new Uri(defaults.BaseUrl),
                Timeout = TimeSpan.FromSeconds(5)
            };
            apiClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            apiReady = true;
            await SendLightingAsync();
        }
        catch (Exception exception)
        {
            ShowError("QuickPlayer API: " + exception.Message);
        }
    }

    private async void BrowseClicked(object? sender, EventArgs eventArgs)
    {
        browse.Enabled = false;
        try
        {
            string? selected = await ShowVideoPickerAsync(videoPath.Text);
            if (selected != null)
            {
                videoPath.Text = selected;
                SaveRecentVideo(selected);
            }
        }
        catch (Exception exception)
        {
            ShowError("Browse: " + exception.Message);
        }
        finally
        {
            browse.Enabled = true;
        }
    }

    private static Task<string?> ShowVideoPickerAsync(string currentPath)
    {
        TaskCompletionSource<string?> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread pickerThread = new(() =>
        {
            try
            {
                using OpenFileDialog dialog = new()
                {
                    AutoUpgradeEnabled = false,
                    Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm|All files|*.*",
                    CheckFileExists = true,
                    CheckPathExists = true,
                    DereferenceLinks = false,
                    RestoreDirectory = true,
                    InitialDirectory = Directory.Exists(
                        Path.GetDirectoryName(currentPath))
                        ? Path.GetDirectoryName(currentPath)
                        : Environment.GetFolderPath(
                            Environment.SpecialFolder.MyVideos),
                    Title = "Choose a video"
                };
                completion.TrySetResult(
                    dialog.ShowDialog() == DialogResult.OK
                        ? dialog.FileName : null);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Video file picker"
        };
        pickerThread.SetApartmentState(ApartmentState.STA);
        pickerThread.Start();
        return completion.Task;
    }

    private async void StartClicked(object? sender, EventArgs eventArgs)
    {
        try
        {
            string path = Path.GetFullPath(videoPath.Text.Trim());
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "Choose an existing video.", path);
            SaveRecentVideo(path);
            StopStreamer();
            await SendLightingAsync();

            ProcessStartInfo info = new(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add(path);
            Add(info, "--name", defaults.TextureName);
            Add(info, "--max-dimension", defaults.MaximumDimension);
            if (defaults.FramesPerSecond > 0)
                Add(info, "--fps", defaults.FramesPerSecond);
            if (!loop.Checked)
                info.ArgumentList.Add("--no-loop");
            Add(info, "--base-url", defaults.BaseUrl);
            Add(info, "--token", defaults.TokenPath);

            Process process = new()
            {
                StartInfo = info,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, args) =>
                ProcessOutputData(args.Data);
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    SafeBeginInvoke(() => ShowError(args.Data!));
            };
            process.Exited += (_, _) => SafeBeginInvoke(() =>
                StreamerExited(process));
            if (!process.Start())
                throw new InvalidOperationException(
                    "The streamer did not start.");
            streamer = process;
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            start.Enabled = false;
            stop.Enabled = true;
        }
        catch (Exception exception)
        {
            ShowError("Start: " + exception.Message);
        }
    }

    private static void Add(ProcessStartInfo info, string name, object value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(Convert.ToString(value,
            System.Globalization.CultureInfo.InvariantCulture)!);
    }

    private static void SaveRecentVideo(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                RecentVideoPath)!);
            File.WriteAllText(RecentVideoPath, path);
        }
        catch { }
    }

    private void LoadControlSettings()
    {
        try
        {
            if (!File.Exists(ControlSettingsPath))
                return;
            ControlSettings? settings = JsonSerializer.Deserialize<ControlSettings>(
                File.ReadAllText(ControlSettingsPath));
            if (settings == null)
                return;

            SetSavedValue(videoBrightness, settings.VideoBrightness);
            SetSavedValue(videoBlackAndWhite, settings.VideoBlackAndWhite);
            SetSavedValue(sceneBrightness, settings.SceneBrightness);
            for (int index = 0; index < GirlSources.Length; index++)
            {
                if (index < settings.PerformerBrightness.Length)
                    SetSavedValue(brightness[index],
                        settings.PerformerBrightness[index]);
                if (index < settings.PerformerPurpleness.Length)
                    SetSavedValue(purpleness[index],
                        settings.PerformerPurpleness[index]);
            }
            loop.Checked = settings.Loop;
        }
        catch { }
    }

    private static void SetSavedValue(TrackBar slider, int value) =>
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);

    private void QueueSettingsSave()
    {
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    private void SaveControlSettings()
    {
        settingsSaveTimer.Stop();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                ControlSettingsPath)!);
            ControlSettings settings = new()
            {
                Loop = loop.Checked,
                VideoBrightness = videoBrightness.Value,
                VideoBlackAndWhite = videoBlackAndWhite.Value,
                SceneBrightness = sceneBrightness.Value,
                PerformerBrightness = brightness.Select(
                    slider => slider.Value).ToArray(),
                PerformerPurpleness = purpleness.Select(
                    slider => slider.Value).ToArray()
            };
            File.WriteAllText(ControlSettingsPath,
                JsonSerializer.Serialize(settings,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void ProcessOutputData(string? line)
    {
        if (line == null || !line.StartsWith("QP_PROGRESS|",
            StringComparison.Ordinal))
            return;
        string[] parts = line.Split('|');
        if (parts.Length != 3 ||
            !double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double position) ||
            !double.TryParse(parts[2], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double duration))
            return;
        SafeBeginInvoke(() => UpdatePlaybackTimeline(position, duration));
    }

    private void UpdatePlaybackTimeline(double position, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0)
            return;
        playbackDurationSeconds = duration;
        playbackTimeline.Enabled = streamer != null;
        if (!timelineDragging)
        {
            updatingTimeline = true;
            playbackTimeline.Value = Math.Clamp((int)Math.Round(
                position / duration * playbackTimeline.Maximum),
                playbackTimeline.Minimum, playbackTimeline.Maximum);
            updatingTimeline = false;
        }
        UpdatePlaybackTimeLabel(timelineDragging
            ? TimelineSeconds() : position);
    }

    private void TimelineScrolled()
    {
        UpdatePlaybackTimeLabel(TimelineSeconds());
        if (!timelineDragging && !updatingTimeline)
            SeekToTimeline();
    }

    private double TimelineSeconds() => playbackDurationSeconds <= 0
        ? 0
        : playbackTimeline.Value / (double)playbackTimeline.Maximum *
            playbackDurationSeconds;

    private void SeekToTimeline()
    {
        if (updatingTimeline || playbackDurationSeconds <= 0)
            return;
        Process? process = streamer;
        if (process == null)
            return;
        try
        {
            if (process.HasExited)
                return;
            double seconds = TimelineSeconds();
            process.StandardInput.WriteLine("SEEK " + seconds.ToString(
                "R", CultureInfo.InvariantCulture));
            UpdatePlaybackTimeLabel(seconds);
        }
        catch (Exception exception) when (exception is IOException or
            InvalidOperationException or ObjectDisposedException)
        {
            ShowError("Seek: " + exception.Message);
        }
    }

    private void UpdatePlaybackTimeLabel(double position) =>
        playbackTime.Text = FormatTime(position) + " / " +
            FormatTime(playbackDurationSeconds);

    private static string FormatTime(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void ResetPlaybackTimeline()
    {
        playbackDurationSeconds = 0;
        timelineDragging = false;
        updatingTimeline = true;
        playbackTimeline.Value = 0;
        playbackTimeline.Enabled = false;
        updatingTimeline = false;
        playbackTime.Text = "0:00 / 0:00";
    }

    private void LightingChanged(int girlIndex)
    {
        UpdateValueLabels(girlIndex);
        QueueSettingsSave();
        if (!apiReady)
            return;
        lightingUpdateTimer.Stop();
        lightingUpdateTimer.Start();
    }

    private void VideoControlsChanged()
    {
        UpdateVideoValueLabels();
        QueueSettingsSave();
        if (!apiReady)
            return;
        lightingUpdateTimer.Stop();
        lightingUpdateTimer.Start();
    }

    private void UpdateValueLabels(int girlIndex)
    {
        brightnessValues[girlIndex].Text =
            brightness[girlIndex].Value + "%";
        purplenessValues[girlIndex].Text =
            purpleness[girlIndex].Value + "%";
    }

    private void UpdateVideoValueLabels()
    {
        videoBrightnessValue.Text = videoBrightness.Value + "% brightness";
        videoBlackAndWhiteValue.Text = videoBlackAndWhite.Value + "% B&W";
        sceneBrightnessValue.Text = sceneBrightness.Value + "%";
    }

    private async void LightingUpdateTimerTick(
        object? sender, EventArgs eventArgs)
    {
        lightingUpdateTimer.Stop();
        try { await SendLightingAsync(); }
        catch (Exception exception)
        {
            ShowError("Lighting: " + exception.Message);
        }
    }

    private async Task SendLightingAsync()
    {
        if (apiClient == null)
            return;
        await lightingUpdateLock.WaitAsync();
        try
        {
            double[] values =
            [
                PackLight(0),
                PackLight(1),
                PackLight(2),
                PackVideoControls()
            ];
            using HttpResponseMessage response = await apiClient.PutAsJsonAsync(
                "fullscreen/shader-data", new { values });
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            lightingUpdateLock.Release();
        }
    }

    private int PackLight(int girlIndex)
    {
        int brightnessByte = (int)Math.Round(
            brightness[girlIndex].Value * 255d / 100d);
        int purpleByte = (int)Math.Round(
            purpleness[girlIndex].Value * 255d / 100d);
        return brightnessByte + purpleByte * 256;
    }

    private int PackVideoControls()
    {
        int brightnessByte = (int)Math.Round(
            videoBrightness.Value * 255d / 200d);
        int blackAndWhiteByte = (int)Math.Round(
            videoBlackAndWhite.Value * 255d / 100d);
        // Byte zero remains the no-controls marker. Values 1..255 encode
        // 0..100%, keeping the complete packed value exactly representable.
        int sceneBrightnessByte = (int)Math.Round(
            sceneBrightness.Value * 254d / 100d) + 1;
        return brightnessByte + blackAndWhiteByte * 256 +
            sceneBrightnessByte * 65536;
    }

    private async Task NextCardAsync(int girlIndex)
    {
        if (apiClient == null)
        {
            ShowError("QuickPlayer API is not connected.");
            return;
        }
        Button button = nextCard[girlIndex];
        button.Enabled = false;
        try
        {
            string source = GirlSources[girlIndex];
            using HttpResponseMessage response = await apiClient.PostAsync(
                "fullscreen/source/" + Uri.EscapeDataString(source) +
                    "/next", null);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception)
        {
            ShowError("Next card: " + exception.Message);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private void StopStreamer()
    {
        Process? process = streamer;
        streamer = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                }
            }
            catch { }
            process.Dispose();
        }
        start.Enabled = true;
        stop.Enabled = false;
        ResetPlaybackTimeline();
    }

    private void StreamerExited(Process process)
    {
        if (ReferenceEquals(streamer, process))
            streamer = null;
        process.Dispose();
        start.Enabled = true;
        stop.Enabled = false;
        ResetPlaybackTimeline();
    }

    private void ShowError(string message)
    {
        if (IsDisposed || Disposing)
            return;
        MessageBox.Show(this, message,
            "QuickPlayer Video and Scene Lighting",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void SafeBeginInvoke(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopStreamer();
            lightingUpdateTimer.Dispose();
            settingsSaveTimer.Dispose();
            apiClient?.Dispose();
            lightingUpdateLock.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class ControlSettings
{
    public bool Loop { get; set; } = true;
    public int VideoBrightness { get; set; } = 100;
    public int VideoBlackAndWhite { get; set; } = 100;
    public int SceneBrightness { get; set; } = 100;
    public int[] PerformerBrightness { get; set; } = [50, 50, 50];
    public int[] PerformerPurpleness { get; set; } = [75, 75, 75];
}
