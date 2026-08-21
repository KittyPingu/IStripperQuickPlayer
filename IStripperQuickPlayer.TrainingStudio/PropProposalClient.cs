using System.Diagnostics;
using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed record PropProposalResult(string MaskPath, string Bucket, string ModelId,
    int Pixels, double Confidence, double Presence);

internal sealed class PropProposalClient : IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    Process? process;
    Task<string>? errors;
    string? loadedPackage;

    internal static string? FindLatestV2Package()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "rvm-runtime", "prop-segmenter", "models");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root).Where(IsValidV2)
            .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
    }

    static bool IsValidV2(string folder)
    {
        try
        {
            string manifestPath = Path.Combine(folder, "manifest.json");
            string checkpoint = Path.Combine(folder, "model.pth");
            if (!File.Exists(manifestPath) || !File.Exists(checkpoint)) return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2 ||
                root.GetProperty("architecture").GetString() != "rvm-conditioned-convnext-fpn-v2")
                return false;
            string expected = root.GetProperty("checkpointSha256").GetString()!;
            string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(checkpoint)));
            return expected.Equals(actual, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    async Task EnsureStartedAsync(string package, CancellationToken token)
    {
        package = Path.GetFullPath(package);
        if (process is { HasExited: false } && string.Equals(package, loadedPackage,
                StringComparison.OrdinalIgnoreCase)) return;
        await StopAsync();
        if (!IsValidV2(package)) throw new InvalidOperationException("The v2 proposal package is invalid.");
        string worker = Path.Combine(AppContext.BaseDirectory, "training", "prop_proposal_worker.py");
        ProcessStartInfo start = new(Sam2Client.RuntimePython())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { worker, "--package", package, "--session" })
            start.ArgumentList.Add(argument);
        process = Process.Start(start) ??
            throw new InvalidOperationException("Could not start the v2 proposal worker.");
        loadedPackage = package; errors = process.StandardError.ReadToEndAsync(token);
        JsonElement ready = await ReadAsync(token);
        if (ready.GetProperty("status").GetString() != "ready")
            throw new InvalidOperationException(Message(ready));
    }

    internal async Task<PropProposalResult> GenerateAsync(string package, string image,
        string alpha, string mask, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await EnsureStartedAsync(package, token);
                string preview = Path.ChangeExtension(mask, ".preview.jpg");
                try
                {
                    await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                        { image, alpha, mask, preview }));
                    JsonElement root = await ReadAsync(token);
                    if (root.GetProperty("status").GetString() != "mask")
                        throw new InvalidOperationException(Message(root));
                    return new(mask, root.GetProperty("activeLearningBucket").GetString() ?? "random",
                        root.GetProperty("modelId").GetString() ?? "unknown",
                        root.GetProperty("pixels").GetInt32(), root.GetProperty("confidence").GetDouble(),
                        root.GetProperty("presence").GetDouble());
                }
                catch (Exception) when (attempt == 0 && process is { HasExited: true })
                { await StopAsync(); }
            }
            throw new InvalidOperationException("The v2 proposal worker stopped unexpectedly.");
        }
        finally { gate.Release(); }
    }

    async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        string? line = await process!.StandardOutput.ReadLineAsync(token);
        if (line == null) throw new InvalidOperationException(await FailureAsync());
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    static string Message(JsonElement value) => value.TryGetProperty("message", out JsonElement message)
        ? message.GetString() ?? "v2 proposal failed." : "v2 proposal failed.";

    async Task<string> FailureAsync() => errors == null ? "v2 proposal worker stopped." :
        (await errors).Trim() is string value && value.Length > 0 ? value : "v2 proposal worker stopped.";

    async Task StopAsync()
    {
        Process? current = process; process = null; loadedPackage = null;
        if (current == null) return;
        try
        {
            if (!current.HasExited)
            {
                await current.StandardInput.WriteLineAsync("{\"command\":\"quit\"}");
                current.StandardInput.Close();
                if (!current.WaitForExit(1500)) current.Kill(true);
            }
        }
        catch { try { current.Kill(true); } catch { } }
        current.Dispose(); errors = null;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await StopAsync(); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
