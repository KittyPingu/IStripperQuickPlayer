using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomWatermarkRemovalForm : Form
{
    readonly CustomShowConfiguration configuration;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-watermark-" + Guid.NewGuid().ToString("N"));
    readonly TextBox sourcePath = new() { Dock = DockStyle.Fill };
    readonly TextBox outputPath = new() { Dock = DockStyle.Fill };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
        AccessibleName = "Video frame used to select the area to remove" };
    readonly Label status = new() { AutoSize = true, Text = "Choose a source video." };
    readonly ComboBox method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox scale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    readonly NumericUpDown subvideo = new() { Minimum = 12, Maximum = 80,
        Increment = 2, Value = 40, Width = 65 };
    readonly CheckBox fp16 = new() { Text = "Use FP16 (recommended)", Checked = true,
        AutoSize = true };
    readonly Button run = new() { Text = "Remove selected area...", AutoSize = true,
        Enabled = false };
    Bitmap? frame;
    Rectangle selectedPixels;
    Point dragStart;
    bool dragging;

    string FramePath => Path.Combine(temporary, "selection-frame.png");
    string MaskPath => Path.Combine(temporary, "removal-mask.png");
    string OutputPreviewPath => Path.Combine(temporary, "output-preview.jpg");

    internal CustomWatermarkRemovalForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Remove Video Object / Watermark";
        ClientSize = new Size(1000, 760);
        MinimumSize = new Size(760, 580);
        StartPosition = FormStartPosition.CenterParent;

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(10),
            ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        TableLayoutPanel paths = new() { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 3, RowCount = 2 };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPath(paths, 0, "Source video", sourcePath, BrowseSource);
        AddPath(paths, 1, "Output MP4", outputPath, BrowseOutput);
        layout.Controls.Add(paths, 0, 0);
        layout.Controls.Add(preview, 0, 1);
        layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 4),
            Text = "Drag a rectangle around the static object or watermark to remove. The source is never changed." }, 0, 2);
        FlowLayoutPanel options = new() { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        method.Items.Add("Fast (FFmpeg delogo - recommended)");
        if (CustomShowProcessor.IsProPainterInstalled(configuration))
            method.Items.Add("AI High Quality (ProPainter)");
        method.SelectedIndex = 0;
        scale.Items.AddRange(["Full processing resolution", "75% processing resolution",
            "50% processing resolution (recommended)"]);
        for (int percentage = 45; percentage >= 10; percentage -= 5)
            scale.Items.Add($"{percentage}% processing resolution");
        scale.SelectedIndex = 2;
        scale.SelectedIndexChanged += (_, _) => subvideo.Value =
            scale.SelectedIndex switch { 0 => 12, 1 => 16, _ => 40 };
        options.Controls.AddRange([new Label { Text = "Method", AutoSize = true,
                Padding = new Padding(0, 6, 0, 0) }, method,
            new Label { Text = "Quality", AutoSize = true,
                Padding = new Padding(0, 6, 0, 0) }, scale,
            new Label { Text = "Temporal window (frames)", AutoSize = true,
                Padding = new Padding(12, 6, 0, 0) }, subvideo, fp16]);
        layout.Controls.Add(options, 0, 3);
        FlowLayoutPanel actions = new() { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight };
        Button clear = new() { Text = "Clear selection", AutoSize = true };
        Button cancel = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([clear, run, cancel, status]);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        CancelButton = cancel;

        preview.MouseDown += Preview_MouseDown;
        preview.MouseMove += Preview_MouseMove;
        preview.MouseUp += Preview_MouseUp;
        preview.Paint += Preview_Paint;
        method.SelectedIndexChanged += (_, _) => UpdateMethodOptions();
        clear.Click += (_, _) => { selectedPixels = Rectangle.Empty; run.Enabled = false;
            status.Text = "Drag around the area to remove."; preview.Invalidate(); };
        run.Click += async (_, _) => await RunAsync();
        AppTheme.Apply(this);
        UpdateMethodOptions();
    }

    void UpdateMethodOptions()
    {
        bool ai = method.SelectedIndex > 0;
        scale.Enabled = subvideo.Enabled = fp16.Enabled = ai;
    }

    static void AddPath(TableLayoutPanel table, int row, string label, TextBox box,
        EventHandler browse)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true,
            Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, 0, row);
        table.Controls.Add(box, 1, row);
        Button button = new() { Text = "Browse...", AutoSize = true };
        button.Click += browse;
        table.Controls.Add(button, 2, row);
    }

    async void BrowseSource(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new() { Filter =
            "Supported video|*.mp4;*.mov;*.avi|MP4 video|*.mp4|MOV video|*.mov|AVI video|*.avi" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        sourcePath.Text = dialog.FileName;
        outputPath.Text = Path.Combine(Path.GetDirectoryName(dialog.FileName)!,
            Path.GetFileNameWithoutExtension(dialog.FileName) + "-cleaned.mp4");
        await LoadFrameAsync();
    }

    void BrowseOutput(object? sender, EventArgs e)
    {
        using SaveFileDialog dialog = new() { Filter = "MP4 video|*.mp4",
            DefaultExt = "mp4", AddExtension = true, FileName = outputPath.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) outputPath.Text = dialog.FileName;
    }

    async Task LoadFrameAsync()
    {
        try
        {
            status.Text = "Extracting a preview frame...";
            Directory.CreateDirectory(temporary);
            ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-y", "-v", "error", "-ss", "3",
                "-i", sourcePath.Text, "-frames:v", "1", "-vf", "setsar=1", FramePath })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("FFmpeg could not be started.");
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(FramePath))
                throw new InvalidOperationException(error.Trim());
            using FileStream stream = new(FramePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using Image loaded = Image.FromStream(stream);
            Bitmap next = new(loaded);
            frame?.Dispose(); frame = next; preview.Image = frame;
            selectedPixels = Rectangle.Empty; run.Enabled = false;
            status.Text = "Drag around the area to remove.";
        }
        catch (Exception error)
        {
            status.Text = "Could not load the preview.";
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    void Preview_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || ImagePoint(e.Location, true) is not Point point)
            return;
        dragStart = point; selectedPixels = Rectangle.Empty; dragging = true;
        preview.Capture = true; preview.Invalidate();
    }

    void Preview_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!dragging || ImagePoint(e.Location, false) is not Point point) return;
        selectedPixels = Rectangle.FromLTRB(Math.Min(dragStart.X, point.X),
            Math.Min(dragStart.Y, point.Y), Math.Max(dragStart.X, point.X),
            Math.Max(dragStart.Y, point.Y));
        preview.Invalidate();
    }

    void Preview_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false; preview.Capture = false;
        run.Enabled = selectedPixels.Width >= 2 && selectedPixels.Height >= 2;
        status.Text = run.Enabled ? $"Selected {selectedPixels.Width} x {selectedPixels.Height} pixels." :
            "Drag a larger rectangle.";
    }

    Point? ImagePoint(Point clientPoint, bool requireInside)
    {
        if (frame == null || preview.ClientSize.Width <= 0 || preview.ClientSize.Height <= 0)
            return null;
        float scale = Math.Min(preview.ClientSize.Width / (float)frame.Width,
            preview.ClientSize.Height / (float)frame.Height);
        float left = (preview.ClientSize.Width - frame.Width * scale) / 2,
            top = (preview.ClientSize.Height - frame.Height * scale) / 2;
        float x = (clientPoint.X - left) / scale, y = (clientPoint.Y - top) / scale;
        if (requireInside && (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height))
            return null;
        return new Point(Math.Clamp((int)Math.Round(x), 0, frame.Width - 1),
            Math.Clamp((int)Math.Round(y), 0, frame.Height - 1));
    }

    void Preview_Paint(object? sender, PaintEventArgs e)
    {
        if (frame == null || selectedPixels.IsEmpty) return;
        float scale = Math.Min(preview.ClientSize.Width / (float)frame.Width,
            preview.ClientSize.Height / (float)frame.Height);
        float left = (preview.ClientSize.Width - frame.Width * scale) / 2,
            top = (preview.ClientSize.Height - frame.Height * scale) / 2;
        RectangleF area = new(left + selectedPixels.X * scale,
            top + selectedPixels.Y * scale, selectedPixels.Width * scale,
            selectedPixels.Height * scale);
        using Brush fill = new SolidBrush(Color.FromArgb(90, Color.Red));
        using Pen outline = new(Color.Yellow, 2);
        e.Graphics.FillRectangle(fill, area); e.Graphics.DrawRectangle(outline, area);
    }

    async Task RunAsync()
    {
        try
        {
            string source = Path.GetFullPath(sourcePath.Text),
                output = Path.GetFullPath(outputPath.Text);
            if (!File.Exists(source)) throw new FileNotFoundException("Choose a source video.", source);
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output must not overwrite the source video.");
            if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output file must be an MP4.");
            if (File.Exists(output) && MessageBox.Show(this, "Replace the existing output file?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            SaveMask();
            bool ai = method.SelectedIndex > 0;
            double ratio = ProcessingRatio(scale.SelectedIndex);
            using CustomShowProcessingForm processing = new((progress, token) =>
                    RunProPainterAsync(configuration, source, MaskPath, output, ratio,
                        (int)subvideo.Value, ai && fp16.Checked, ai ? "propainter" : "fast",
                        FramePath, OutputPreviewPath,
                        progress, token),
                "Removing Video Object / Watermark", "Original input", "Output preview");
            if (processing.ShowDialog(this) != DialogResult.OK) return;
            MessageBox.Show(this, "Cleaned video saved to:\n" + output, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    void SaveMask()
    {
        if (frame == null || selectedPixels.Width < 2 || selectedPixels.Height < 2)
            throw new InvalidOperationException("Select the area to remove first.");
        using Bitmap mask = new(frame.Width, frame.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(mask))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, selectedPixels);
        }
        mask.Save(MaskPath, ImageFormat.Png);
    }

    static async Task<CustomShowProcessResult> RunProPainterAsync(
        CustomShowConfiguration configuration, string source, string mask, string output,
        double ratio, int subvideoLength, bool fp16, string method, string previewSource,
        string previewOutput, IProgress<CustomShowProgress> progress,
        CancellationToken token)
    {
        string worker = CustomShowProcessor.ProPainterWorkerPath;
        ProcessStartInfo start = new(configuration.PythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { worker, "--runtime",
            CustomShowProcessor.RuntimeRoot(configuration), "--source", source,
            "--mask", mask, "--output", output, "--resize-ratio",
            ratio.ToString(CultureInfo.InvariantCulture), "--subvideo-length",
            Math.Clamp(subvideoLength, 12, 80).ToString(CultureInfo.InvariantCulture),
            "--method", method, "--preview-output", previewOutput })
            start.ArgumentList.Add(argument);
        if (fp16) start.ArgumentList.Add("--fp16");
        start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        using Process process = new() { StartInfo = start };
        StringBuilder log = new(); process.Start();
        await using ProcessCancellationScope cancellationScope = new(process, token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        while (await process.StandardOutput.ReadLineAsync(token) is string line)
        {
            log.AppendLine(line);
            try
            {
                using JsonDocument json = JsonDocument.Parse(line);
                JsonElement root = json.RootElement;
                progress.Report(new CustomShowProgress(
                    root.GetProperty("stage").GetString() ?? "processing",
                    root.GetProperty("percent").GetDouble(),
                    root.GetProperty("message").GetString() ?? "",
                    previewSource, previewOutput));
            }
            catch (JsonException) { }
        }
        await process.WaitForExitAsync(token);
        string error = await stderr;
        log.Append(error);
        try { await File.WriteAllTextAsync(output + ".processing.log", log.ToString(),
            CancellationToken.None); } catch { }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Watermark removal failed (exit code {process.ExitCode})." : error.Trim());
        return new CustomShowProcessResult();
    }

    internal static bool VerifySelection()
    {
        Rectangle rectangle = Rectangle.FromLTRB(Math.Min(20, 5), Math.Min(10, 30),
            Math.Max(20, 5), Math.Max(10, 30));
        return rectangle == new Rectangle(5, 10, 15, 20) &&
            ProcessingRatio(2) == .5 && ProcessingRatio(10) == .1;
    }

    static double ProcessingRatio(int selectedIndex) => selectedIndex switch
    {
        0 => 1,
        1 => .75,
        _ => (60 - selectedIndex * 5) / 100d
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            preview.Image = null; frame?.Dispose();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
