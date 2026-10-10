using System.Diagnostics;
using System.Text.Json;
using IStripperQuickPlayer.Interop;
using Microsoft.Win32;

namespace IStripperQuickPlayer;

internal static class DesktopPlaybackVerification
{
    internal static async Task<int> RunLiveAsync()
    {
        using Process? host = Process.GetProcessesByName("vghd").FirstOrDefault();
        if (host == null) return 2;
        using var bridge = PlaybackBridgeClient.Attach(host.Id,
            Path.Combine(AppContext.BaseDirectory, "IStripperPlaybackBridge64.dll"), (_, _) => false);
        int version = bridge.Call("IStripperPlaybackBridgeVersion");
        if (version != 145) return 3;
        foreach (string operation in new[] { "IStripperBeginDesktopAttachment", "IStripperStartRegistryHook", "IStripperStartFullscreenHook",
            "IStripperInstallMovieCaptureHook", "IStripperDiscoverMovie", "IStripperSetPlayerLocked" })
        {
            int result = bridge.Call(operation, operation == "IStripperSetPlayerLocked" ? 0UL : null);
            Console.WriteLine($"{operation}: 0x{result:X8}");
        }
        using RegistryKey? parameters = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters");
        string original = parameters?.GetValue("CurrentAnim")?.ToString() ?? "";
        var durations = new List<double>();
        DesktopBridgeSnapshot? snapshot = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            byte[] packet = DesktopBridgeSnapshot.CreatePacket(DesktopBridgeSnapshot.PacketSize);
            long started = Stopwatch.GetTimestamp();
            int result = bridge.CallRoundTrip("IStripperGetDesktopSnapshot", packet);
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (result >= 0) snapshot = DesktopBridgeSnapshot.Parse(packet);
            if (snapshot?.Path.Length > 0) break;
            if (attempt == 5 && original.Length > 0)
                bridge.CallRoundTrip("IStripperRequestDesktopSelection", DesktopBridgeSnapshot.Selection(0, 1, original));
            await Task.Delay(200);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { snapshot, durations }));
        if (snapshot?.Path.Length is not > 0 || snapshot.State is not 3 and not 4) return 4;
        long discoveryStarted = Stopwatch.GetTimestamp();
        if (bridge.Call("IStripperDiscoverMovie") < 0 ||
            Stopwatch.GetElapsedTime(discoveryStarted).TotalMilliseconds >= 1500) return 12;
        byte[] discovered = DesktopBridgeSnapshot.CreatePacket(DesktopBridgeSnapshot.PacketSize);
        if (bridge.CallRoundTrip("IStripperGetDesktopSnapshot", discovered) < 0 ||
            DesktopBridgeSnapshot.Parse(discovered).Instance != snapshot.Instance) return 13;
        Console.WriteLine("Existing movie discovery stayed within its budget and preserved playback identity.");
        ulong instance = snapshot.Instance;
        byte[] request = DesktopBridgeSnapshot.Selection(instance, 2, snapshot.Path);
        if (bridge.CallRoundTrip("IStripperRequestDesktopSelection", request) < 0) return 5;
        bool replay = false;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            byte[] packet = DesktopBridgeSnapshot.CreatePacket(DesktopBridgeSnapshot.PacketSize);
            if (bridge.CallRoundTrip("IStripperGetDesktopSnapshot", packet) >= 0)
            {
                var observation = DesktopBridgeSnapshot.Parse(packet);
                if (observation.Instance != instance && observation.Path == snapshot.Path && observation.Request == 2)
                { snapshot = observation; replay = true; break; }
            }
            await Task.Delay(200);
        }
        Console.WriteLine("Same-clip replay confirmed: " + replay);
        if (!replay) return 6;
        int originalState = snapshot.State;
        if (bridge.Call("IStripperPause") < 0 || bridge.Call("IStripperGetState") != 4) return 7;
        if (originalState == 3 && (bridge.Call("IStripperResume") < 0 || bridge.Call("IStripperGetState") != 3)) return 8;
        byte[] afterResume = DesktopBridgeSnapshot.CreatePacket(DesktopBridgeSnapshot.PacketSize);
        bridge.CallRoundTrip("IStripperGetDesktopSnapshot", afterResume);
        if (DesktopBridgeSnapshot.Parse(afterResume).Instance != snapshot.Instance) return 9;
        instance = snapshot.Instance;
        if (bridge.CallRoundTrip("IStripperPrepareDesktopSelection",
            DesktopBridgeSnapshot.Selection(instance, 3, snapshot.Path)) < 0) return 10;
        bool natural = false;
        try
        {
            bridge.Call("IStripperSetPlayRate", unchecked((ulong)BitConverter.DoubleToInt64Bits(4)));
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                byte[] packet = DesktopBridgeSnapshot.CreatePacket(DesktopBridgeSnapshot.PacketSize);
                if (bridge.CallRoundTrip("IStripperGetDesktopSnapshot", packet) >= 0)
                {
                    var observation = DesktopBridgeSnapshot.Parse(packet);
                    if (observation.Instance != instance && observation.CompletedInstance == instance &&
                        observation.Path == snapshot.Path && observation.Request == 3)
                    { natural = true; snapshot = observation; break; }
                }
                await Task.Delay(250);
            }
        }
        finally
        {
            bridge.Call("IStripperSetPlayRate", unchecked((ulong)BitConverter.DoubleToInt64Bits(1)));
            bridge.CallRoundTrip("IStripperPrepareDesktopSelection", DesktopBridgeSnapshot.Selection(0, 4, ""));
        }
        Console.WriteLine("Natural completion and prepared selection confirmed: " + natural);
        Console.WriteLine(JsonSerializer.Serialize(snapshot));
        if (!natural) return 11;
        return 0;
    }
}
