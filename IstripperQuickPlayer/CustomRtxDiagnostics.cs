using System.Diagnostics;
using System.Text;

namespace IStripperQuickPlayer;

internal static class CustomRtxDiagnostics
{
    static readonly object sync = new();
    static readonly string folder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer");
    internal static readonly string Path = System.IO.Path.Combine(folder,
        "custom-rtx-diagnostic.log");
    static long nextId;

    internal static long NewId() => Interlocked.Increment(ref nextId);

    internal static double ElapsedMilliseconds(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    internal static void Write(string component, long id, string action,
        string? details = null, Exception? error = null)
    {
        try
        {
            StringBuilder line = new();
            line.Append(DateTimeOffset.Now.ToString("O"));
            line.Append(" seq=").Append(NewId());
            line.Append(" pid=").Append(Environment.ProcessId);
            line.Append(" tid=").Append(Environment.CurrentManagedThreadId);
            line.Append(' ').Append(component).Append('=').Append(id);
            line.Append(" action=").Append(action);
            if (!string.IsNullOrWhiteSpace(details))
                line.Append(' ').Append(details.Replace('\r', ' ')
                    .Replace('\n', ' '));
            if (error != null)
                line.Append(" error=").Append(error.ToString()
                    .Replace('\r', ' ').Replace('\n', ' '));
            line.AppendLine();
            lock (sync)
            {
                Directory.CreateDirectory(folder);
                RotateIfNeeded();
                using FileStream stream = new(Path, FileMode.Append,
                    FileAccess.Write, FileShare.ReadWrite, 4096,
                    FileOptions.WriteThrough);
                byte[] bytes = Encoding.UTF8.GetBytes(line.ToString());
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Diagnostics must never affect playback.
        }
    }

    static void RotateIfNeeded()
    {
        const long maximumBytes = 20 * 1024 * 1024;
        if (!File.Exists(Path) || new FileInfo(Path).Length < maximumBytes)
            return;
        string previous = System.IO.Path.Combine(folder,
            "custom-rtx-diagnostic.previous.log");
        File.Move(Path, previous, overwrite: true);
    }
}
