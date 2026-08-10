using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;

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
    private readonly Label status = new()
    {
        Text = "Connecting to QuickPlayer...",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(18, 8, 0, 0)
    };
    private readonly System.Windows.Forms.Timer lightingUpdateTimer = new()
    {
        Interval = 40
    };
    private readonly System.Windows.Forms.Timer statusResetTimer = new()
    {
        Interval = 2500
    };
    private readonly SemaphoreSlim lightingUpdateLock = new(1, 1);
    private Process? streamer;
    private HttpClient? apiClient;
    private bool apiReady;

    public VideoStreamerForm(Options defaults)
    {
        this.defaults = defaults;
        Text = "QuickPlayer Video and Scene Lighting";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 510);
        Size = new Size(987, 663);

        loop.Checked = defaults.Loop;
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
        statusResetTimer.Tick += (_, _) => ResetTransientStatus();
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
        FormClosing += (_, _) => StopStreamer();
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

        FlowLayoutPanel playback = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        playback.Controls.Add(start);
        playback.Controls.Add(stop);
        playback.Controls.Add(loop);
        playback.Controls.Add(status);
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
            SetStatus("Ready");
        }
        catch (Exception exception)
        {
            SetStatus("API: " + exception.Message, error: true);
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
            SetStatus("Browse: " + exception.Message, error: true);
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
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    SafeBeginInvoke(() => SetStatus(
                        args.Data!, error: true));
            };
            process.Exited += (_, _) => SafeBeginInvoke(() =>
                StreamerExited(process));
            if (!process.Start())
                throw new InvalidOperationException(
                    "The streamer did not start.");
            streamer = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            start.Enabled = false;
            stop.Enabled = true;
            SetStatus("Streaming " + Path.GetFileName(path));
        }
        catch (Exception exception)
        {
            SetStatus("Start: " + exception.Message, error: true);
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

    private void LightingChanged(int girlIndex)
    {
        UpdateValueLabels(girlIndex);
        if (!apiReady)
            return;
        lightingUpdateTimer.Stop();
        lightingUpdateTimer.Start();
    }

    private void VideoControlsChanged()
    {
        UpdateVideoValueLabels();
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
            SetStatus("Lighting: " + exception.Message, error: true);
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
            SetStatus("QuickPlayer API is not connected.", error: true);
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
            SetTransientStatus("Next " + GirlLabels[girlIndex] +
                " card requested");
        }
        catch (Exception exception)
        {
            SetStatus("Next card: " + exception.Message, error: true);
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
        if (!IsDisposed)
            SetStatus(apiReady ? "Stopped" : "Connecting to QuickPlayer...");
    }

    private void StreamerExited(Process process)
    {
        if (ReferenceEquals(streamer, process))
            streamer = null;
        process.Dispose();
        start.Enabled = true;
        stop.Enabled = false;
        SetStatus("Stopped");
    }

    private void SetStatus(string text, bool error = false)
    {
        statusResetTimer.Stop();
        status.Text = text;
        status.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
    }

    private void SetTransientStatus(string text)
    {
        SetStatus(text);
        statusResetTimer.Start();
    }

    private void ResetTransientStatus()
    {
        statusResetTimer.Stop();
        SetStatus(streamer != null
            ? "Streaming " + Path.GetFileName(videoPath.Text)
            : "Ready");
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
            statusResetTimer.Dispose();
            apiClient?.Dispose();
            lightingUpdateLock.Dispose();
        }
        base.Dispose(disposing);
    }
}
