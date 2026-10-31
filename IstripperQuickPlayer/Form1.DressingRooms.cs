using IStripperQuickPlayer.Interop;
using Persistence = IStripperQuickPlayer.DataModel.Persistence;
using Microsoft.Win32;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace IStripperQuickPlayer;

public partial class Form1
{
    private static string WetRoomCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "wet-room-cache.json");

    private sealed record DressingRoomCard(
        int Id, string Name, int Take, int Duration, string ResourceName, bool WetRoom = false)
    {
        public override string ToString() =>
            $"{Name} — Take {Take} ({TimeSpan.FromSeconds(Duration):m\\:ss})";
    }

    private async void dressingRoomsTestButton_Click(object? sender, EventArgs e)
    {
        dressingRoomsTestButton.Enabled = false;
        try
        {
            using Form chooser = new()
            {
                Text = "Dressing / Wet Rooms",
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(520, 330)
            };
            TabControl tabs = new() { Dock = DockStyle.Fill };
            TabPage dressingTab = new("Dressing Rooms");
            TabPage wetTab = new("Wet Rooms");
            ListBox dressingList = new() { Dock = DockStyle.Fill, AccessibleName = "Owned dressing rooms" };
            ListBox wetList = new() { Dock = DockStyle.Fill, AccessibleName = "Owned wet rooms" };
            Label dressingStatus = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            Label wetStatus = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Loading owned wet rooms…" };
            dressingTab.Controls.Add(dressingList);
            dressingTab.Controls.Add(dressingStatus);
            wetTab.Controls.Add(wetList);
            wetTab.Controls.Add(wetStatus);
            tabs.TabPages.AddRange([dressingTab, wetTab]);
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
            chooser.Controls.Add(tabs);
            chooser.Controls.Add(buttons);
            chooser.CancelButton = cancel;
            chooser.AcceptButton = open;
            using CancellationTokenSource loading = new();
            ListBox SelectedList() => tabs.SelectedTab == wetTab ? wetList : dressingList;
            void UpdateOpen() => open.Enabled = SelectedList().SelectedItem is DressingRoomCard;
            void Populate(ListBox list, Label status, List<DressingRoomCard> cards, string kind)
            {
                list.Items.Clear();
                list.Items.AddRange(cards.Cast<object>().ToArray());
                status.Text = $"No owned {kind} were found.";
                status.Visible = cards.Count == 0;
                list.Visible = cards.Count > 0;
                if (cards.Count > 0) list.SelectedIndex = 0;
                UpdateOpen();
            }
            Populate(dressingList, dressingStatus, ReadOwnedDressingRooms(), "dressing rooms");
            Populate(wetList, wetStatus, ReadCachedWetRooms(WetRoomCachePath), "wet rooms");
            if (wetList.Items.Count == 0)
                wetStatus.Text = AttachedVghdIsRunning()
                    ? "Loading owned wet rooms…"
                    : "Start iStripper to load your owned wet rooms.";
            open.Click += (_, _) => { if (open.Enabled) chooser.DialogResult = DialogResult.OK; };
            dressingList.DoubleClick += (_, _) => { if (open.Enabled) chooser.DialogResult = DialogResult.OK; };
            wetList.DoubleClick += (_, _) => { if (open.Enabled) chooser.DialogResult = DialogResult.OK; };
            dressingList.SelectedIndexChanged += (_, _) => UpdateOpen();
            wetList.SelectedIndexChanged += (_, _) => UpdateOpen();
            tabs.SelectedIndexChanged += (_, _) => UpdateOpen();
            chooser.FormClosed += (_, _) => loading.Cancel();
            chooser.Shown += async (_, _) =>
            {
                try
                {
                    List<DressingRoomCard> rooms = await ReadOwnedWetRoomsAsync(loading.Token);
                    if (!loading.IsCancellationRequested)
                        Populate(wetList, wetStatus, rooms, "wet rooms");
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    Debug.WriteLine($"Wet-room refresh failed; keeping the cached list: {error}");
                    if (!loading.IsCancellationRequested)
                        wetStatus.Text = AttachedVghdIsRunning()
                            ? "Could not refresh wet rooms.\r\n" + error.Message
                            : "Start iStripper to refresh your owned wet rooms.";
                }
            };
            if (chooser.ShowDialog(this) != DialogResult.OK ||
                SelectedList().SelectedItem is not DressingRoomCard selected)
                return;

            string? url = await RequestDressingRoomUrlAsync(
                selected.Id, selected.ResourceName, selected.WetRoom);
            if (url == null)
                throw new InvalidOperationException(
                    "iStripper did not provide a room stream URL. " +
                    "Diagnostics were written to:\r\n" +
                    Path.Combine(Path.GetTempPath(),
                        "IstripperQuickPlayer-dressingroom.log"));

            DressingRoomStreamRelay relay = await DressingRoomStreamRelay.StartAsync(url);
            dressingRoomStreamRelays.Add(relay);
            LaunchDefaultMp4Player(relay.StreamUrl);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Dressing / Wet Rooms",
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

    private static List<DressingRoomCard> ParseWetRoomCatalogue(string json)
    {
        using JsonDocument catalogue = JsonDocument.Parse(json);
        return catalogue.RootElement.GetProperty("wetroomcard").EnumerateArray()
            .Select(card => new DressingRoomCard(
                card.GetProperty("wrcId").GetInt32(),
                HumanizeDressingRoomName(card.GetProperty("video").GetString()!.Split('/')[^2]),
                card.GetProperty("takeNumber").GetInt32(),
                card.GetProperty("duration").GetInt32(),
                card.GetProperty("name").GetString()!, WetRoom: true))
            .OrderBy(card => card.Name).ThenBy(card => card.Take).ToList();
    }

    private async Task<List<DressingRoomCard>> ReadOwnedWetRoomsAsync(CancellationToken cancellation)
    {
        if (!AttachedVghdIsRunning()) return ReadCachedWetRooms(WetRoomCachePath);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\System", false);
        string path = Path.Combine(key?.GetValue("DataPath", "")?.ToString() ?? "", "staticWetRoomProperties.cds");
        if (!File.Exists(path)) return ReadCachedWetRooms(WetRoomCachePath);
        PlaybackBridgeClient bridge = playbackBridgeClient ??
            throw new InvalidOperationException("The iStripper playback bridge is not connected.");
        List<DressingRoomCard> owned = [];
        foreach (DressingRoomCard room in ParseWetRoomCatalogue(DecodeDressingRoomCds(File.ReadAllBytes(path))))
        {
            cancellation.ThrowIfCancellationRequested();
            int result = unchecked((int)0x80070015);
            for (int attempt = 0; attempt < 50 && result == unchecked((int)0x80070015); attempt++)
            {
                result = await Task.Run(() => bridge.Call("IStripperIsWetRoomOwned", (ulong)room.Id), cancellation);
                if (result == unchecked((int)0x80070015)) await Task.Delay(200, cancellation);
            }
            if (result < 0)
                throw new InvalidOperationException($"iStripper could not read wet-room ownership (0x{result:X8}).");
            if (result > 0) owned.Add(room);
        }
        // Publish only a complete ownership refresh, including a confirmed empty collection.
        cancellation.ThrowIfCancellationRequested();
        try { Persistence.Save(WetRoomCachePath, owned); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save the wet-room cache: {error}");
        }
        return owned;
    }

    private static List<DressingRoomCard> ReadCachedWetRooms(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return Persistence.Load<List<DressingRoomCard>>(path)
                .Where(room => room is { WetRoom: true, Id: > 0, Take: > 0, Duration: >= 0 } &&
                    !string.IsNullOrWhiteSpace(room.Name) && !string.IsNullOrWhiteSpace(room.ResourceName))
                .OrderBy(room => room.Name).ThenBy(room => room.Take).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or Newtonsoft.Json.JsonException)
        {
            Debug.WriteLine($"Could not read the wet-room cache: {error}");
            return [];
        }
    }

    internal static bool VerifyWetRoomCache()
    {
        string folder = Path.Combine(Path.GetTempPath(), "quickplayer-wet-room-cache-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "cache.json");
        try
        {
            if (ReadCachedWetRooms(path).Count != 0) return false;
            List<DressingRoomCard> owned = [new(1, "Anabel Busty", 1, 500, "anabelbusty_take1", true)];
            Persistence.Save(path, owned);
            if (!ReadCachedWetRooms(path).SequenceEqual(owned) || File.Exists(path + ".tmp")) return false;
            Persistence.Save(path, new List<DressingRoomCard>());
            if (ReadCachedWetRooms(path).Count != 0) return false;
            File.WriteAllText(path, "invalid json");
            return ReadCachedWetRooms(path).Count == 0;
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    internal static bool VerifyWetRoomCatalogue()
    {
        List<DressingRoomCard> rooms = ParseWetRoomCatalogue("""
            {"wetroomcard":[{"wrcId":1,"name":"anabelbusty_take1","video":"/fileaccess/wetroom/anabel_busty/anabelbusty_take1","takeNumber":1,"duration":500}]}
            """);
        return rooms is [{ Id: 1, Name: "Anabel Busty", Take: 1, Duration: 500,
            ResourceName: "anabelbusty_take1", WetRoom: true }] &&
            ParseWetRoomCatalogue("{\"wetroomcard\":[]}").Count == 0;
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
        int id, string resourceName, bool wetRoom = false)
    {
        PlaybackBridgeClient bridge = playbackBridgeClient ??
            throw new InvalidOperationException("The iStripper playback bridge is not connected.");
        byte[] packet = new byte[8192];
        Encoding.UTF8.GetBytes(resourceName + "\0").CopyTo(packet, 4);
        int start = unchecked((int)0x8007007E);
        for (int attempt = 0;
            attempt < 50 && (start == unchecked((int)0x8007007E) ||
                start == unchecked((int)0x80070015));
            attempt++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet, id);
            start = await Task.Run(() => bridge.CallRoundTrip(
                wetRoom ? "IStripperRequestWetRoomUrl" : "IStripperRequestDressingRoomUrl", packet));
            if (start == unchecked((int)0x8007007E) || start == unchecked((int)0x80070015))
                await Task.Delay(200);
        }
        if (start < 0)
            throw new InvalidOperationException($"Could not ask iStripper to open the room (0x{start:X8}).");
        Stopwatch waiting = Stopwatch.StartNew();
        while (waiting.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(50);
            Array.Clear(packet);
            int result = await Task.Run(() => bridge.CallRoundTrip(
                "IStripperConsumeDressingRoomUrl", packet));
            if (result < 0 && result != unchecked((int)0x80070015))
                throw new InvalidOperationException($"Could not read the room stream (0x{result:X8}).");
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
