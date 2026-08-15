using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class Sam2MattingWorkerHost : IDisposable
{
    static readonly Lazy<Sam2MattingWorkerHost> Instance = new(() => new());
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object diagnosticsGate = new();
    readonly List<string> diagnostics = [];
    readonly System.Threading.Timer idleTimer;
    Process? process;
    Task? stderrPump;
    string? python;
    string? loadedTracker;
    DateTime environmentWriteUtc;
    DateTime lastActivityUtc;
    bool busy;

    Sam2MattingWorkerHost() => idleTimer = new(_ => _ = ShutdownIfIdleAsync(),
        null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    internal static async Task<CustomShowProcessResult> RunAsync(
        CustomShowConfiguration configuration, string tracker, string requestPath,
        string cancelPath, string logPath, IProgress<CustomShowProgress>? progress,
        CancellationToken token) => await Instance.Value.RunCoreAsync(configuration,
            tracker, requestPath, cancelPath, logPath, progress, token);

    async Task<CustomShowProcessResult> RunCoreAsync(
        CustomShowConfiguration configuration, string tracker, string requestPath,
        string cancelPath, string logPath, IProgress<CustomShowProgress>? progress,
        CancellationToken token)
    {
        await gate.WaitAsync(token);
        int diagnosticStart = 0;
        StringBuilder jobLog = new();
        try
        {
            busy = true;
            lastActivityUtc = DateTime.UtcNow;
            EnsureHost(configuration);
            lock (diagnosticsGate)
            {
                diagnostics.Clear();
                diagnosticStart = 0;
            }
            if (loadedTracker != tracker)
            {
                progress?.Report(new CustomShowProgress("model-switching", 0,
                    $"Loading {Sam2MattingSupport.DisplayName(tracker)}"));
                await SendAsync(new { protocolVersion = 1, type = "loadVariant",
                    tracker }, "variantLoaded", jobLog, progress, token);
                loadedTracker = tracker;
            }
            using CancellationTokenRegistration cancellation = token.Register(() =>
            {
                try { File.WriteAllText(cancelPath, "cancel"); } catch { }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (process is { HasExited: false }) process.Kill(true);
                    }
                    catch { }
                });
            });
            JsonElement completed = await SendAsync(new
            {
                protocolVersion = 1, type = "run",
                jobId = Path.GetFileName(Path.GetDirectoryName(requestPath)),
                requestPath
            }, "completed", jobLog, progress, CancellationToken.None);
            token.ThrowIfCancellationRequested();
            if (!completed.TryGetProperty("result", out JsonElement resultNode))
                throw new InvalidDataException(
                    "The persistent SAM2Matting worker omitted its result contract.");
            return resultNode.Deserialize<CustomShowProcessResult>(
                CustomShowStore.JsonOptions) ?? throw new InvalidDataException(
                    "The persistent SAM2Matting result contract is invalid.");
        }
        catch (OperationCanceledException)
        {
            DiscardHost();
            throw;
        }
        catch (Exception) when (process == null || process.HasExited)
        {
            DiscardHost();
            token.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            lock (diagnosticsGate)
                for (int index = Math.Min(diagnosticStart, diagnostics.Count);
                     index < diagnostics.Count; index++)
                    jobLog.AppendLine(diagnostics[index]);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllTextAsync(logPath, jobLog.ToString(),
                CancellationToken.None);
            busy = false;
            lastActivityUtc = DateTime.UtcNow;
            gate.Release();
        }
    }

    void EnsureHost(CustomShowConfiguration configuration)
    {
        string environment = Path.Combine(Sam2MattingSupport.RuntimeRoot,
            "environment.json");
        DateTime writeUtc = File.GetLastWriteTimeUtc(environment);
        string requestedPython = Path.GetFullPath(
            configuration.Sam2MattingPythonExecutable);
        if (process is { HasExited: false } && python == requestedPython &&
            environmentWriteUtc == writeUtc) return;
        DiscardHost();
        ProcessStartInfo start = new(requestedPython)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Sam2MattingSupport.RuntimeRoot
        };
        foreach (string argument in new[]
        {
            CustomShowProcessor.Sam2MattingWorkerPath, "--host", "--runtime",
            Sam2MattingSupport.RuntimeRoot
        }) start.ArgumentList.Add(argument);
        start.Environment["IQP_FFMPEG"] = Path.Combine(
            AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(
            AppContext.BaseDirectory, "ffprobe.exe");
        start.Environment["PYTHONUNBUFFERED"] = "1";
        CustomShowProcessor.ConfigureProcessingPriorities(start, configuration);
        process = Process.Start(start) ??
            throw new InvalidOperationException(
                "The persistent SAM2Matting worker could not be started.");
        python = requestedPython;
        environmentWriteUtc = writeUtc;
        loadedTracker = null;
        Process captured = process;
        stderrPump = Task.Run(async () =>
        {
            while (await captured.StandardError.ReadLineAsync() is string line)
                lock (diagnosticsGate) diagnostics.Add(line);
        });
    }

    async Task<JsonElement> SendAsync(object command, string responseType,
        StringBuilder log, IProgress<CustomShowProgress>? progress,
        CancellationToken token)
    {
        Process running = process is { HasExited: false } value ? value :
            throw new InvalidOperationException(
                "The persistent SAM2Matting worker is unavailable.");
        string request = JsonSerializer.Serialize(command, CustomShowStore.JsonOptions);
        await running.StandardInput.WriteLineAsync(request.AsMemory(), token);
        await running.StandardInput.FlushAsync(token);
        while (await running.StandardOutput.ReadLineAsync(token) is string line)
        {
            log.AppendLine(line);
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("stage", out JsonElement stageNode))
                {
                    string stage = stageNode.GetString() ?? "processing";
                    string message = root.TryGetProperty("message",
                        out JsonElement messageNode)
                        ? messageNode.GetString() ?? stage : stage;
                    progress?.Report(new CustomShowProgress(stage,
                        root.TryGetProperty("percent", out JsonElement percent)
                            ? percent.GetDouble() : 0, message));
                    if (stage == "error")
                        throw new InvalidOperationException(message);
                    continue;
                }
                string? type = root.TryGetProperty("type", out JsonElement typeNode)
                    ? typeNode.GetString() : null;
                if (type == "error")
                    throw new InvalidOperationException(root.TryGetProperty("message",
                        out JsonElement message) ? message.GetString() :
                        "SAM2Matting worker protocol failure.");
                if (type == responseType) return root.Clone();
            }
        }
        throw new EndOfStreamException(
            "The persistent SAM2Matting worker exited unexpectedly.");
    }

    async Task ShutdownIfIdleAsync()
    {
        if (busy || process is not { HasExited: false } ||
            DateTime.UtcNow - lastActivityUtc < TimeSpan.FromMinutes(5) ||
            !await gate.WaitAsync(0)) return;
        try
        {
            if (process is not { HasExited: false }) return;
            try
            {
                await process.StandardInput.WriteLineAsync(
                    "{\"protocolVersion\":1,\"type\":\"shutdown\"}");
                await process.StandardInput.FlushAsync();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch { try { process.Kill(true); } catch { } }
            finally { DiscardHost(); }
        }
        finally { gate.Release(); }
    }

    void DiscardHost()
    {
        Process? prior = process;
        process = null;
        loadedTracker = null;
        python = null;
        try { if (prior is { HasExited: false }) prior.Kill(true); } catch { }
        try { prior?.Dispose(); } catch { }
        stderrPump = null;
        lock (diagnosticsGate) diagnostics.Clear();
    }

    public void Dispose()
    {
        idleTimer.Dispose();
        DiscardHost();
        gate.Dispose();
    }
}
