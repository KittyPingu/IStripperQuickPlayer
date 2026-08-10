using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ShaderVideoStreamer;

internal sealed class VideoStreamerForm : Form
{
    private static string RecentVideoPath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "shader-video-path.txt");
    private readonly Options defaults;
    private readonly TextBox videoPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox textureName = new() { Text = "video1" };
    private readonly NumericUpDown positionX = Number(0, 1, 0.5m, 4, 0.001m);
    private readonly NumericUpDown positionY = Number(0, 1, 0.5m, 4, 0.001m);
    private readonly NumericUpDown displayWidth = Number(1, 2200, 512, 1, 1);
    private readonly NumericUpDown displayHeight = Number(1, 1080, 256, 1, 1);
    private readonly NumericUpDown moveStep = Number(
        0.0001m, 1, 0.001m, 4, 0.0001m);
    private readonly NumericUpDown resizeStep = Number(
        0.1m, 1080, 1, 1, 0.1m);
    private readonly NumericUpDown fps = Number(0, 240, 0, 3, 1);
    private readonly NumericUpDown maxDimension = Number(1, 4096, 4096, 0, 16);
    private readonly CheckBox loop = new() { Text = "Loop", Checked = true, AutoSize = true };
    private readonly Button browse = new() { Text = "Browse…", AutoSize = true };
    private readonly Button start = new() { Text = "Start", AutoSize = true };
    private readonly Button stop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly TextBox log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9)
    };
    private readonly SemaphoreSlim positionUpdateLock = new(1, 1);
    private Process? streamer;
    private HttpClient? apiClient;
    private bool loaded;

    public VideoStreamerForm(Options defaults)
    {
        this.defaults = defaults;
        Text = "QuickPlayer Shader Video Streamer";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 520);
        Size = new Size(820, 620);
        KeyPreview = true;

        textureName.Text = defaults.TextureName;
        positionX.Value = (decimal)defaults.PositionX;
        positionY.Value = (decimal)defaults.PositionY;
        displayWidth.Value = (decimal)defaults.PanelWidth;
        displayHeight.Value = (decimal)defaults.PanelHeight;
        moveStep.Value = (decimal)defaults.MoveStep;
        resizeStep.Value = (decimal)defaults.ResizeStep;
        fps.Value = (decimal)defaults.FramesPerSecond;
        maxDimension.Value = defaults.MaximumDimension;
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
        foreach (Control control in new Control[]
        {
            positionX, positionY, displayWidth, displayHeight
        })
            ((NumericUpDown)control).ValueChanged += PositionChanged;
        FormClosing += (_, _) => StopStreamer();
        Shown += FormShown;
    }

    private Control BuildLayout()
    {
        TableLayoutPanel fields = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 8,
            Padding = new Padding(12),
            AutoSize = true
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        Add(fields, 0, "Video", videoPath, browse);
        AddPair(fields, 1, "Texture name", textureName,
            "Maximum decode size", maxDimension);
        AddPair(fields, 2, "Centre X (0–1)", positionX,
            "Centre Y (0–1)", positionY);
        AddPair(fields, 3, "Display width", displayWidth,
            "Display height", displayHeight);
        AddPair(fields, 4, "FPS override (0=source)", fps,
            "", loop);
        AddPair(fields, 5, "Move step", moveStep,
            "Resize step (pixels)", resizeStep);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        buttons.Controls.Add(start);
        buttons.Controls.Add(stop);
        Label help = new()
        {
            AutoSize = true,
            Margin = new Padding(18, 8, 0, 0),
            Text = "Arrows move • Shift+arrows resize • Q stops"
        };
        buttons.Controls.Add(help);
        fields.Controls.Add(buttons, 0, 6);
        fields.SetColumnSpan(buttons, 4);
        fields.Controls.Add(log, 0, 7);
        fields.SetColumnSpan(log, 4);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return fields;
    }

    private static void Add(TableLayoutPanel panel, int row,
        string label, Control value, Control trailing)
    {
        panel.Controls.Add(Label(label), 0, row);
        panel.Controls.Add(value, 1, row);
        panel.SetColumnSpan(value, 2);
        panel.Controls.Add(trailing, 3, row);
    }

    private static void AddPair(TableLayoutPanel panel, int row,
        string leftLabel, Control left, string rightLabel, Control right)
    {
        panel.Controls.Add(Label(leftLabel), 0, row);
        panel.Controls.Add(left, 1, row);
        panel.Controls.Add(Label(rightLabel), 2, row);
        panel.Controls.Add(right, 3, row);
        left.Dock = DockStyle.Fill;
        right.Dock = DockStyle.Fill;
    }

    private static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 7, 8, 3)
    };

    private static NumericUpDown Number(decimal minimum, decimal maximum,
        decimal value, int decimals, decimal increment) => new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            DecimalPlaces = decimals,
            Increment = increment,
            ThousandsSeparator = decimals == 0,
            Dock = DockStyle.Fill
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
            loaded = true;
            await SendPositionAsync();
        }
        catch (Exception exception)
        {
            AppendLog("API: " + exception.Message);
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
            AppendLog("Browse: " + exception.Message);
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
                throw new FileNotFoundException("Choose an existing video.", path);
            SaveRecentVideo(path);
            StopStreamer();
            await SendPositionAsync();

            ProcessStartInfo info = new(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add(path);
            Add(info, "--name", textureName.Text.Trim());
            Add(info, "--max-dimension", maxDimension.Value);
            if (fps.Value > 0)
                Add(info, "--fps", fps.Value);
            Add(info, "--x", positionX.Value);
            Add(info, "--y", positionY.Value);
            Add(info, "--display-width", displayWidth.Value);
            Add(info, "--display-height", displayHeight.Value);
            Add(info, "--move-step", moveStep.Value);
            Add(info, "--resize-step", resizeStep.Value);
            if (!loop.Checked)
                info.ArgumentList.Add("--no-loop");
            Add(info, "--base-url", defaults.BaseUrl);
            Add(info, "--token", defaults.TokenPath);

            streamer = new Process { StartInfo = info, EnableRaisingEvents = true };
            streamer.OutputDataReceived += (_, args) => AppendLog(args.Data);
            streamer.ErrorDataReceived += (_, args) => AppendLog(args.Data);
            streamer.Exited += (_, _) => BeginInvoke(StreamerExited);
            if (!streamer.Start())
                throw new InvalidOperationException("The streamer did not start.");
            streamer.BeginOutputReadLine();
            streamer.BeginErrorReadLine();
            start.Enabled = false;
            stop.Enabled = true;
            AppendLog("Started " + Path.GetFileName(path));
        }
        catch (Exception exception)
        {
            AppendLog("Start: " + exception.Message);
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

    private async void PositionChanged(object? sender, EventArgs eventArgs)
    {
        if (!loaded)
            return;
        try { await SendPositionAsync(); }
        catch (Exception exception) { AppendLog("Position: " + exception.Message); }
    }

    private async Task SendPositionAsync()
    {
        if (apiClient == null)
            return;
        await positionUpdateLock.WaitAsync();
        try
        {
            double[] values =
            [
                (double)positionX.Value,
                (double)positionY.Value,
                (double)displayWidth.Value,
                (double)displayHeight.Value
            ];
            using HttpResponseMessage response = await apiClient.PutAsJsonAsync(
                "fullscreen/shader-data", new { values });
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            positionUpdateLock.Release();
        }
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        decimal move = moveStep.Value;
        decimal resize = resizeStep.Value;
        if (eventArgs.Control)
        {
            move /= 10;
            resize /= 10;
        }
        if (eventArgs.Shift)
        {
            if (eventArgs.KeyCode == Keys.Left)
                displayWidth.Value = Math.Max(displayWidth.Minimum,
                    displayWidth.Value - resize);
            else if (eventArgs.KeyCode == Keys.Right)
                displayWidth.Value = Math.Min(displayWidth.Maximum,
                    displayWidth.Value + resize);
            else if (eventArgs.KeyCode == Keys.Up)
                displayHeight.Value = Math.Min(displayHeight.Maximum,
                    displayHeight.Value + resize);
            else if (eventArgs.KeyCode == Keys.Down)
                displayHeight.Value = Math.Max(displayHeight.Minimum,
                    displayHeight.Value - resize);
            else
                base.OnKeyDown(eventArgs);
        }
        else if (eventArgs.KeyCode == Keys.Left)
            positionX.Value = Math.Max(positionX.Minimum, positionX.Value - move);
        else if (eventArgs.KeyCode == Keys.Right)
            positionX.Value = Math.Min(positionX.Maximum, positionX.Value + move);
        else if (eventArgs.KeyCode == Keys.Up)
            positionY.Value = Math.Max(positionY.Minimum, positionY.Value - move);
        else if (eventArgs.KeyCode == Keys.Down)
            positionY.Value = Math.Min(positionY.Maximum, positionY.Value + move);
        else if (eventArgs.KeyCode == Keys.Q)
            StopStreamer();
        else
            base.OnKeyDown(eventArgs);
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
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
        StreamerExited();
    }

    private void StreamerExited()
    {
        start.Enabled = true;
        stop.Enabled = false;
    }

    private void AppendLog(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(text));
            return;
        }
        log.AppendText(text + Environment.NewLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopStreamer();
            apiClient?.Dispose();
            positionUpdateLock.Dispose();
        }
        base.Dispose(disposing);
    }
}
