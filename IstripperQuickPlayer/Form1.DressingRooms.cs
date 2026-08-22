using IStripperQuickPlayer.Interop;
using Microsoft.Win32;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace IStripperQuickPlayer;

public partial class Form1
{
    private sealed record DressingRoomCard(
        int Id, string Name, int Take, int Duration, string ResourceName)
    {
        public override string ToString() =>
            $"{Name} — Take {Take} ({TimeSpan.FromSeconds(Duration):m\\:ss})";
    }

    private async void dressingRoomsTestButton_Click(object? sender, EventArgs e)
    {
        dressingRoomsTestButton.Enabled = false;
        try
        {
            List<DressingRoomCard> cards = ReadOwnedDressingRooms();
            if (cards.Count == 0)
            {
                MessageBox.Show(this, "No owned dressing rooms were found in iStripper's local cache.",
                    "Dressing Rooms", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Task<int>? preparation = playbackBridgeClient is { } bridge
                ? Task.Run(() => bridge.Call("IStripperPrepareDressingRoomUrls"))
                : null;

            using Form chooser = new()
            {
                Text = "Dressing Rooms (temporary test)",
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(520, 330)
            };
            ListBox list = new() { Dock = DockStyle.Fill };
            list.Items.AddRange(cards.Cast<object>().ToArray());
            list.SelectedIndex = 0;
            Button open = new() { Text = "Open in default media player", AutoSize = true };
            Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom, AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(open);
            chooser.Controls.Add(list);
            chooser.Controls.Add(buttons);
            chooser.CancelButton = cancel;
            open.Click += (_, _) => chooser.DialogResult = DialogResult.OK;
            list.DoubleClick += (_, _) => chooser.DialogResult = DialogResult.OK;
            if (chooser.ShowDialog(this) != DialogResult.OK ||
                list.SelectedItem is not DressingRoomCard selected)
                return;

            if (preparation != null)
            {
                try { await preparation; }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Dressing Room preparation failed: {exception}");
                }
            }

            string? url = await RequestDressingRoomUrlAsync(
                selected.Id, selected.ResourceName);
            if (url == null)
                throw new InvalidOperationException(
                    "iStripper did not provide a Dressing Room stream URL. " +
                    "Diagnostics were written to:\r\n" +
                    Path.Combine(Path.GetTempPath(),
                        "IstripperQuickPlayer-dressingroom.log"));

            DressingRoomStreamRelay relay = await DressingRoomStreamRelay.StartAsync(url);
            dressingRoomStreamRelays.Add(relay);
            LaunchDefaultMp4Player(relay.StreamUrl);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Dressing Rooms test",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            dressingRoomsTestButton.Enabled = true;
        }
    }

    private static List<DressingRoomCard> ReadOwnedDressingRooms()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Totem\vghd\System", false);
        string dataPath = key?.GetValue("DataPath", "")?.ToString() ?? "";
        string cachePath = Path.Combine(dataPath, "dressingRoomCardsCache.cds");
        string cataloguePath = Path.Combine(dataPath, "staticDressingRoomProperties.cds");
        if (!File.Exists(cachePath) || !File.Exists(cataloguePath))
            return [];

        using JsonDocument cache = JsonDocument.Parse(DecodeDressingRoomCds(
            File.ReadAllBytes(cachePath)));
        HashSet<int> ownedIds = cache.RootElement.TryGetProperty("cleared", out JsonElement cleared)
            ? cleared.EnumerateArray().Select(value => value.GetInt32()).ToHashSet()
            : [];
        XDocument catalogue = XDocument.Parse(DecodeDressingRoomCds(
            File.ReadAllBytes(cataloguePath)));
        return catalogue.Descendants("dressingroomcard")
            .Where(card => ownedIds.Contains((int)card.Attribute("drcId")!))
            .Select(card => new DressingRoomCard(
                (int)card.Attribute("drcId")!,
                HumanizeDressingRoomName((string)card.Attribute("name")!),
                (int)card.Attribute("takeNumber")!,
                (int)card.Attribute("duration")!,
                (string)card.Attribute("name")!))
            .OrderBy(card => card.Name).ThenBy(card => card.Take).ToList();
    }

    private static string DecodeDressingRoomCds(byte[] source)
    {
        byte[] decoded = new byte[source.Length];
        int offset = 0;
        for (; offset + 1 < source.Length; offset += 2)
        {
            ushort cipher = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(decoded.AsSpan(offset, 2),
                unchecked((ushort)(cipher - (0x239 - offset))));
        }
        if (offset < source.Length)
            decoded[offset] = unchecked((byte)(source[offset] - (0x39 - offset)));
        return Encoding.UTF8.GetString(decoded);
    }

    private static string HumanizeDressingRoomName(string resourceName)
    {
        string value = System.Text.RegularExpressions.Regex.Replace(
            resourceName, "_take\\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        value = value.Replace('_', ' ');
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value);
    }

    private async Task<string?> RequestDressingRoomUrlAsync(
        int id, string resourceName)
    {
        PlaybackBridgeClient bridge = playbackBridgeClient ??
            throw new InvalidOperationException("The iStripper playback bridge is not connected.");
        byte[] packet = new byte[8192];
        Encoding.UTF8.GetBytes(resourceName + "\0").CopyTo(packet, 4);
        int start = unchecked((int)0x8007007E);
        for (int attempt = 0;
            attempt < 50 && start == unchecked((int)0x8007007E);
            attempt++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet, id);
            start = bridge.CallRoundTrip(
                "IStripperRequestDressingRoomUrl", packet);
            if (start == unchecked((int)0x8007007E))
                await Task.Delay(200);
        }
        if (start < 0)
            throw new InvalidOperationException($"Could not ask iStripper to open the Dressing Room (0x{start:X8}).");
        // The bridge waits for DressingRoomData::videoSourceChanged() and
        // performs the scan on its command thread, with a bounded fallback.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            await Task.Delay(50);
            Array.Clear(packet);
            int result = bridge.CallRoundTrip("IStripperConsumeDressingRoomUrl", packet);
            if (result >= 0)
            {
                int end = Array.IndexOf(packet, (byte)0);
                string url = Encoding.UTF8.GetString(packet, 0, end < 0 ? packet.Length : end);
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return url;
            }
        }
        return null;
    }

    private readonly List<DressingRoomStreamRelay> dressingRoomStreamRelays = [];

    private void DisposeDressingRoomStreams()
    {
        foreach (DressingRoomStreamRelay relay in dressingRoomStreamRelays)
            relay.Dispose();
        dressingRoomStreamRelays.Clear();
    }

    private static void LaunchDefaultMp4Player(string url)
    {
        uint length = 0;
        AssocQueryString(0, 2, ".mp4", "open", null, ref length);
        StringBuilder executable = new(checked((int)length));
        int result = AssocQueryString(0, 2, ".mp4", "open", executable,
            ref length);
        if (result != 0 || executable.Length == 0)
            throw new InvalidOperationException(
                "Windows has no default application registered for MP4 video.");
        Process.Start(new ProcessStartInfo(executable.ToString(), url)
        {
            UseShellExecute = false
        });
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint str,
        string assoc, string? extra, StringBuilder? output,
        ref uint outputLength);
}
