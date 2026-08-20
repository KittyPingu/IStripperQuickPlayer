using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class Sam2Client : IAsyncDisposable
{
    Process? process;
    Task<string>? errors;
    readonly string image, mask, preview;

    internal Sam2Client(string image, string temporary)
    {
        this.image = image; mask = Path.Combine(temporary, "sam-mask.png");
        preview = Path.Combine(temporary, "sam-preview.jpg");
    }

    internal string MaskPath => mask;

    internal async Task<string> StartAsync(CancellationToken token)
    {
        string python = RuntimePython();
        string worker = Path.Combine(AppContext.BaseDirectory, "custom-shows", "sam_mask_worker.py");
        string runtime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(python)!, "..", ".."));
        if (!File.Exists(python) || !File.Exists(worker) || !File.Exists(Path.Combine(runtime, "SAM2_COMMIT")))
            throw new InvalidOperationException("SAM2 is not installed in the QuickPlayer processing runtime.");
        ProcessStartInfo start = new(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { worker, "--runtime", runtime, "--image", image,
            "--mask", mask, "--preview", preview, "--model", "base-plus" }) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start SAM2.");
        errors = process.StandardError.ReadToEndAsync(token);
        JsonElement ready = await ReadAsync(token);
        if (ready.GetProperty("status").GetString() != "ready") throw new InvalidOperationException(Message(ready));
        return $"{ready.GetProperty("checkpoint").GetString()} on {ready.GetProperty("device").GetString()?.ToUpperInvariant()}";
    }

    internal async Task GenerateAsync(IEnumerable<PointF> points, IEnumerable<int> labels,
        RectangleF? box, string? seed, CancellationToken token)
    {
        PointF[] promptPoints = points.ToArray();
        int[] promptLabels = labels.ToArray();
        await EnsureRunningAsync(token);
        try
        {
            await GenerateOnceAsync(promptPoints, promptLabels, box, seed, token);
        }
        catch (Exception) when (process == null || process.HasExited)
        {
            await RestartAsync(token);
            await GenerateOnceAsync(promptPoints, promptLabels, box, seed, token);
        }
    }

    async Task GenerateOnceAsync(PointF[] points, int[] labels,
        RectangleF? box, string? seed, CancellationToken token)
    {
        object request = new
        {
            command = "predict",
            points = points.Select(value => new[] { value.X, value.Y }).ToArray(),
            labels = labels.ToArray(),
            box = box is RectangleF value ? new[] { value.Left, value.Top, value.Right, value.Bottom } : null,
            seedMask = seed
        };
        Process active = process ??
            throw new InvalidOperationException("SAM2 is not ready.");
        await active.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await active.StandardInput.FlushAsync(token);
        JsonElement response = await ReadAsync(token);
        if (response.GetProperty("status").GetString() != "mask")
            throw new InvalidOperationException(Message(response));
    }

    async Task EnsureRunningAsync(CancellationToken token)
    {
        if (process is { HasExited: false }) return;
        await RestartAsync(token);
    }

    async Task RestartAsync(CancellationToken token)
    {
        await StopAsync();
        await StartAsync(token);
    }

    internal async Task LoadAsync(string path, CancellationToken token)
    {
        if (process == null || process.HasExited) throw new InvalidOperationException("SAM2 is not ready.");
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            command = "load", image = path
        }));
        await process.StandardInput.FlushAsync(token);
        JsonElement response = await ReadAsync(token);
        if (response.GetProperty("status").GetString() != "loaded")
            throw new InvalidOperationException(Message(response));
    }

    async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        string? line = await process!.StandardOutput.ReadLineAsync(token);
        if (line == null)
        {
            string detail = errors == null ? "" : await errors;
            throw new InvalidOperationException(detail.Trim().Length == 0 ? "SAM2 stopped unexpectedly." : detail.Trim());
        }
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    static string Message(JsonElement response) => response.TryGetProperty("message", out JsonElement value)
        ? value.GetString() ?? "SAM2 failed." : "SAM2 failed.";

    internal static string RuntimePython() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "rvm-runtime", "venv", "Scripts", "python.exe");

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    async Task StopAsync()
    {
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("{\"command\":\"quit\"}");
                    process.StandardInput.Close();
                    if (!process.WaitForExit(1000)) process.Kill(true);
                }
            }
            catch { }
            process.Dispose();
            process = null;
            errors = null;
        }
    }
}

internal sealed class PersistentSam2Session : IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-training-sam2-" + Guid.NewGuid().ToString("N"));
    Sam2Client? client;
    string description = "SAM2";

    internal async Task<(Sam2Client Client, string Description)> LoadAsync(string image,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(temporary);
            if (client == null)
            {
                client = new Sam2Client(image, temporary);
                description = await client.StartAsync(CancellationToken.None);
                token.ThrowIfCancellationRequested();
                return (client, description);
            }
            try { await client.LoadAsync(image, CancellationToken.None); }
            catch
            {
                await client.DisposeAsync();
                client = new Sam2Client(image, temporary);
                description = await client.StartAsync(CancellationToken.None);
            }
            token.ThrowIfCancellationRequested();
            return (client, description + " (warm; image embedding refreshed)");
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (client != null) await client.DisposeAsync();
            client = null;
            try { Directory.Delete(temporary, true); } catch { }
        }
        finally { gate.Release(); gate.Dispose(); }
    }
}
