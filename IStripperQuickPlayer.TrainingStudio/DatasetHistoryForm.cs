using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class DatasetHistoryForm : Form
{
    readonly DatasetStore store;
    readonly Func<TrainingSample, Task> deleteSample;
    readonly ListView samples = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = false, HideSelection = false
    };
    readonly PictureBox preview = new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(30, 30, 30)
    };
    readonly Label details = new() { Dock = DockStyle.Top, Height = 66, Padding = new Padding(8) };
    readonly ComboBox filter = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox showMask = new() { Text = "Show mask overlay", Checked = true, AutoSize = true,
        Padding = new Padding(8, 7, 0, 0) };
    readonly Button delete = new() { Text = "Delete from training data", AutoSize = true, Enabled = false };

    internal DatasetHistoryForm(DatasetStore store, Func<TrainingSample, Task> deleteSample)
    {
        this.store = store; this.deleteSample = deleteSample;
        Text = "Training dataset history — newest first"; Width = 1400; Height = 820;
        MinimumSize = new Size(900, 600); StartPosition = FormStartPosition.CenterParent;
        samples.Columns.Add("Reviewed", 145); samples.Columns.Add("Type", 75);
        samples.Columns.Add("Video", 265); samples.Columns.Add("Timestamp", 95);
        samples.Columns.Add("Split", 80); samples.Columns.Add("Objects", 65);
        filter.Items.AddRange(["All", "Positive", "Negative"]); filter.SelectedIndex = 0;

        FlowLayoutPanel filters = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 5, 8, 5) };
        filters.Controls.AddRange([new Label { Text = "Show", AutoSize = true,
            Padding = new Padding(0, 7, 0, 0) }, filter, showMask]);
        Panel left = new() { Dock = DockStyle.Fill };
        left.Controls.Add(samples); left.Controls.Add(filters);
        Panel right = new() { Dock = DockStyle.Fill };
        right.Controls.Add(preview); right.Controls.Add(details);
        SplitContainer split = new() { Dock = DockStyle.Fill, SplitterDistance = 650 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        Button close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 52,
            Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([close, delete]);
        Controls.Add(split); Controls.Add(actions); CancelButton = close;

        samples.SelectedIndexChanged += (_, _) => ShowSelected();
        filter.SelectedIndexChanged += (_, _) => Populate();
        showMask.CheckedChanged += (_, _) => ShowSelected();
        delete.Click += async (_, _) => await DeleteSelectedAsync();
        split.SizeChanged += (_, _) => SizePanels(split);
        Populate();
    }

    static void SizePanels(SplitContainer split)
    {
        int available = split.ClientSize.Width - split.SplitterWidth;
        if (available >= 760)
            split.SplitterDistance = Math.Clamp((int)Math.Round(available * .48), 430,
                Math.Max(430, available - 430));
    }

    void Populate(string? selectId = null)
    {
        string selectedId = selectId ?? SelectedSample()?.Id ?? "";
        string choice = filter.SelectedItem as string ?? "All";
        samples.BeginUpdate(); samples.Items.Clear();
        foreach (TrainingSample sample in store.AcceptedHistory().Where(value =>
                     choice == "All" || string.Equals(value.Decision, choice, StringComparison.OrdinalIgnoreCase)))
        {
            VideoSource source = store.Source(sample.SourceId);
            DateTime reviewed = sample.ReviewedUtc ?? sample.PresentedUtc;
            ListViewItem row = new([
                reviewed.ToLocalTime().ToString("g"), DisplayDecision(sample.Decision),
                Path.GetFileName(source.Path), TimeSpan.FromMilliseconds(sample.TimestampMs).ToString("hh\\:mm\\:ss\\.fff"),
                sample.Split, sample.ObjectCount.ToString("N0")]) { Tag = sample };
            samples.Items.Add(row);
            if (sample.Id == selectedId) row.Selected = true;
        }
        samples.EndUpdate();
        if (samples.SelectedItems.Count == 0 && samples.Items.Count > 0) samples.Items[0].Selected = true;
        if (samples.Items.Count == 0) ShowSelected();
    }

    TrainingSample? SelectedSample() => samples.SelectedItems.Count == 1
        ? samples.SelectedItems[0].Tag as TrainingSample : null;

    void ShowSelected()
    {
        preview.Image?.Dispose(); preview.Image = null;
        TrainingSample? sample = SelectedSample(); delete.Enabled = sample != null;
        if (sample?.FramePath == null)
        {
            details.Text = samples.Items.Count == 0 ? "No accepted samples match this filter." : "";
            return;
        }
        VideoSource source = store.Source(sample.SourceId);
        details.Text = $"{DisplayDecision(sample.Decision)} · {Path.GetFileName(source.Path)} · " +
            $"{TimeSpan.FromMilliseconds(sample.TimestampMs):hh\\:mm\\:ss\\.fff} · {sample.Width}×{sample.Height} · " +
            $"{sample.Split} · reviewed {(sample.ReviewedUtc ?? sample.PresentedUtc).ToLocalTime():g}";
        string framePath = store.Resolve(sample.FramePath);
        if (!File.Exists(framePath)) { details.Text += " · frame missing"; return; }
        if (showMask.Checked && sample.PropMaskPath != null && File.Exists(store.Resolve(sample.PropMaskPath)))
            preview.Image = CreateMaskOverlay(framePath, store.Resolve(sample.PropMaskPath));
        else preview.Image = TrainingStudioForm.LoadUnlockedImage(framePath);
    }

    async Task DeleteSelectedAsync()
    {
        if (SelectedSample() is not TrainingSample sample) return;
        VideoSource source = store.Source(sample.SourceId);
        DialogResult confirmation = MessageBox.Show(this,
            $"Delete this {sample.Decision} sample from training data?\n\n" +
            $"{Path.GetFileName(source.Path)}\n{TimeSpan.FromMilliseconds(sample.TimestampMs):hh\\:mm\\:ss\\.fff}\n\n" +
            "Its source and timestamp will remain rejected so it is not offered again.",
            "Delete training sample", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;
        string id = sample.Id;
        delete.Enabled = false; UseWaitCursor = true;
        try
        {
            await deleteSample(sample); Populate();
            details.Text = $"Deleted sample {id} from training data.";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            delete.Enabled = SelectedSample() != null;
        }
        finally { UseWaitCursor = false; }
    }

    internal static Bitmap CreateMaskOverlay(string framePath, string maskPath)
    {
        using Bitmap frame = TrainingStudioForm.LoadUnlockedImage(framePath);
        using Bitmap mask = TrainingStudioForm.LoadUnlockedImage(maskPath);
        Bitmap result = new(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        using Bitmap scaledMask = new(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(result))
            graphics.DrawImage(frame, new Rectangle(0, 0, result.Width, result.Height));
        using (Graphics graphics = Graphics.FromImage(scaledMask))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(mask, new Rectangle(0, 0, scaledMask.Width, scaledMask.Height));
        }

        try
        {
            Rectangle bounds = new(0, 0, result.Width, result.Height);
            BitmapData frameData = result.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            BitmapData maskData = scaledMask.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int frameStride = Math.Abs(frameData.Stride), maskStride = Math.Abs(maskData.Stride);
                byte[] framePixels = new byte[frameStride * result.Height];
                byte[] maskPixels = new byte[maskStride * result.Height];
                Marshal.Copy(frameData.Scan0, framePixels, 0, framePixels.Length);
                Marshal.Copy(maskData.Scan0, maskPixels, 0, maskPixels.Length);
                for (int y = 0; y < result.Height; y++)
                {
                    int frameRow = frameData.Stride >= 0 ? y * frameStride : (result.Height - 1 - y) * frameStride;
                    int maskRow = maskData.Stride >= 0 ? y * maskStride : (result.Height - 1 - y) * maskStride;
                    for (int x = 0; x < result.Width; x++)
                    {
                        int frameIndex = frameRow + x * 4, maskIndex = maskRow + x * 4;
                        int maskValue = Math.Max(maskPixels[maskIndex],
                            Math.Max(maskPixels[maskIndex + 1], maskPixels[maskIndex + 2]));
                        int alpha = maskValue * 150 / 255;
                        if (alpha == 0) continue;
                        int inverse = 255 - alpha;
                        framePixels[frameIndex] = (byte)(framePixels[frameIndex] * inverse / 255);
                        framePixels[frameIndex + 1] = (byte)((255 * alpha + framePixels[frameIndex + 1] * inverse) / 255);
                        framePixels[frameIndex + 2] = (byte)(framePixels[frameIndex + 2] * inverse / 255);
                    }
                }
                Marshal.Copy(framePixels, 0, frameData.Scan0, framePixels.Length);
            }
            finally
            {
                scaledMask.UnlockBits(maskData); result.UnlockBits(frameData);
            }
            return result;
        }
        catch
        {
            result.Dispose(); throw;
        }
    }

    static string DisplayDecision(string decision) => char.ToUpperInvariant(decision[0]) + decision[1..];

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        preview.Image?.Dispose(); base.OnFormClosed(e);
    }
}
