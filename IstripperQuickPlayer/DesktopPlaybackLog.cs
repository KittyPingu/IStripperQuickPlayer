using System.Text.Json;
using System.Threading.Channels;

namespace IStripperQuickPlayer;

internal static class DesktopPlaybackLog
{
    static readonly Channel<string> records = Channel.CreateBounded<string>(
        new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    static DesktopPlaybackLog() => _ = Task.Run(WriteAsync);

    internal static void Record(string action, object? details = null)
    {
        try
        {
            records.Writer.TryWrite(JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, process = Environment.ProcessId, action, details
            }));
        }
        catch { /* Diagnostics must never interrupt playback. */ }
    }

    static async Task WriteAsync()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer");
        string path = Path.Combine(directory, "desktop-playback.jsonl");
        await foreach (string record in records.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= 8 * 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                await File.AppendAllTextAsync(path, record + Environment.NewLine);
            }
            catch { /* A full or unavailable disk must not block the player. */ }
        }
    }
}
