using System.Diagnostics;
using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class RvmPreviewClient : IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    Process? process;
    Task<string>? errors;

    async Task EnsureStartedAsync(CancellationToken token)
    {
        if (process is { HasExited: false }) return;
        await StopAsync();
        string python = Sam2Client.RuntimePython();
        string worker = Path.Combine(AppContext.BaseDirectory, "custom-shows", "rvm_preview_worker.py");
        string runtime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(python)!, "..", ".."));
        ProcessStartInfo start = new(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { worker, "--runtime", runtime }) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start RVM preview worker.");
        errors = process.StandardError.ReadToEndAsync(token);
        JsonElement ready = await ReadAsync(token);
        if (ready.GetProperty("status").GetString() != "ready") throw new InvalidOperationException(Message(ready));
    }

    internal async Task<string> GenerateAsync(string source, long frameMs, double threshold,
        string destination, CancellationToken token, string? alphaDestination = null,
        int outputWidth = 0, int outputHeight = 0)
    {
        await gate.WaitAsync(token);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await EnsureStartedAsync(token);
                try
                {
                    await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {
                        source, mask = destination, alpha = alphaDestination, frameMs,
                        alphaThreshold = threshold, outputWidth, outputHeight }));
                    JsonElement response = await ReadAsync(token);
                    if (response.GetProperty("status").GetString() != "mask")
                        throw new InvalidOperationException(Message(response));
                    return destination;
                }
                catch (Exception) when (attempt == 0 && process is { HasExited: true }) { await StopAsync(); }
            }
            throw new InvalidOperationException("RVM preview worker stopped unexpectedly.");
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
        ? message.GetString() ?? "RVM preview failed." : "RVM preview failed.";
    async Task<string> FailureAsync() => errors == null ? "RVM preview worker stopped."
        : (await errors).Trim() is string value && value.Length > 0 ? value : "RVM preview worker stopped.";

    async Task StopAsync()
    {
        Process? current = process; process = null;
        if (current == null) return;
        try { if (!current.HasExited) { await current.StandardInput.WriteLineAsync("{\"command\":\"quit\"}"); current.StandardInput.Close(); if (!current.WaitForExit(1500)) current.Kill(true); } }
        catch { try { current.Kill(true); } catch { } }
        current.Dispose(); errors = null;
    }

    public async ValueTask DisposeAsync() { await gate.WaitAsync(); try { await StopAsync(); } finally { gate.Release(); gate.Dispose(); } }
}
