using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IStripperQuickPlayer;

internal sealed record CustomShowProgress(
    string Stage, double Percent, string Message,
    string? PreviewSource = null, string? PreviewComposite = null,
    string? PreviewSourceLabel = null, string? PreviewCompositeLabel = null);

internal sealed class CustomShowProcessResult
{
    public string? ClipId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string FrameRate { get; set; } = "";
    public string? TimeBase { get; set; }
    public long? DecodedFrameCount { get; set; }
    public long? ForegroundFrameCount { get; set; }
    public long? AlphaFrameCount { get; set; }
    public string? ForegroundCodec { get; set; }
    public string? AlphaCodec { get; set; }
    public string? AlphaPixelFormat { get; set; }
    public string? Tracker { get; set; }
    public long? FirstTimestamp { get; set; }
    public long? LastTimestamp { get; set; }
    public long? InitialMaskFrameMs { get; set; }
    public long DurationMs { get; set; }
    public string? ForegroundMode { get; set; }
    public int? RequestedSequenceChunk { get; set; }
    public int? EffectiveSequenceChunk { get; set; }
    public string? ExecutionMode { get; set; }
    public int? PipelineDepth { get; set; }
    public string? Encoder { get; set; }
    public string? EncoderPreset { get; set; }
    public CustomShowProcessResult[]? Clips { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

internal sealed record CustomShowProcessJob(string Output, long StartMs, long EndMs);

internal static class CustomShowProcessor
{
    internal sealed record NvidiaMemory(int TotalMiB, int FreeMiB);
    internal const string OmniShotCutRevision =
        "23ad6fb41b296fb9258b0e7825125a914573b906";
    internal const string StabiloRevision =
        "52ebd524d26fb940b868dc9d7eeb3e2602f895a3";
    internal static string WorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "rvm_worker.py");
    internal static string MatAnyoneWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "matanyone2_worker.py");
    internal static string VideoMaMaWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "videomama_worker.py");
    internal static string ViTMatteWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "vitmatte_worker.py");
    internal static string RvmViTMatteWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "rvm_vitmatte_worker.py");
    internal static string RvmInitialMaskWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "rvm_initial_mask_worker.py");
    internal static string ProPainterWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "propainter_worker.py");
    internal static string OmniShotCutWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "omnishotcut_worker.py");
    internal static string StabiloWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "stabilo_worker.py");
    internal static string Sam2MattingWorkerPath => Path.Combine(
        AppContext.BaseDirectory, "custom-shows", "sam2matting_worker.py");

    internal static string Sam2FrameCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "sam2-frame-cache");

    internal static NvidiaMemory? NvidiaMemoryInfo()
    {
        try
        {
            ProcessStartInfo start = new("nvidia-smi")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--query-gpu=memory.total,memory.free");
            start.ArgumentList.Add("--format=csv,noheader,nounits");
            using Process process = Process.Start(start)!;
            string? line = process.StandardOutput.ReadLine();
            if (!process.WaitForExit(3000)) { process.Kill(true); return null; }
            string[] values = line?.Split(',', StringSplitOptions.TrimEntries) ?? [];
            return process.ExitCode == 0 && values.Length >= 2 &&
                int.TryParse(values[0], out int total) &&
                int.TryParse(values[1], out int free) && total > 0 && free > 0
                ? new(total, free) : null;
        }
        catch { return null; }
    }

    internal static int VideoMaMaSafeBatch(NvidiaMemory memory)
    {
        int usable = Math.Min(memory.TotalMiB, memory.FreeMiB);
        return usable < 20 * 1024 ? 1 : usable < 32 * 1024 ? 2 : 4;
    }

    internal static int ViTMatteSafeBatch(NvidiaMemory memory, bool baseModel,
        int width, int height)
    {
        int usable = Math.Min(memory.TotalMiB, memory.FreeMiB);
        int maximum = baseModel
            ? usable < 24 * 1024 ? 1 : usable < 40 * 1024 ? 2 : 4
            : usable < 20 * 1024 ? 2 : usable < 32 * 1024 ? 4 : 8;
        long pixels = (long)width * height;
        const long referencePixels = 1920L * 1080;
        if (pixels < referencePixels)
            maximum = Math.Max(maximum, (int)Math.Floor(maximum *
                Math.Sqrt(referencePixels / (double)Math.Max(1, pixels))));
        else if (pixels > referencePixels)
            maximum = Math.Max(1, maximum / 2);
        return Math.Clamp(maximum, 1, 12);
    }

    internal static bool VerifyResultContract()
    {
        const string json = """
            {
              "width": 1280,
              "height": 720,
              "frameRate": "30/1",
              "durationMs": 1000,
              "initialMaskFrameMs": 1033,
              "foregroundMode": "source",
              "clips": [{
                "width": 1280,
                "height": 720,
                "frameRate": "30/1",
                "durationMs": 1000,
                "foregroundMode": "source"
              }],
              "futureWorkerMetadata": true
            }
            """;
        CustomShowProcessResult? result = JsonSerializer.Deserialize<CustomShowProcessResult>(
            json, CustomShowStore.JsonOptions);
        return result?.ForegroundMode == "source" && result.InitialMaskFrameMs == 1033 &&
            result.Clips?.Length == 1 &&
            result.AdditionalProperties?.ContainsKey("futureWorkerMetadata") == true &&
            VideoMaMaSafeBatch(new(16 * 1024, 15 * 1024)) == 1 &&
            VideoMaMaSafeBatch(new(48 * 1024, 40 * 1024)) == 4 &&
            ViTMatteSafeBatch(new(16 * 1024, 15 * 1024), true,
                1920, 1080) == 1 &&
            ViTMatteSafeBatch(new(24 * 1024, 23 * 1024), true,
                3840, 2160) == 1 &&
            WorkerErrorMessage(
                "{\"stage\":\"error\",\"message\":\"Initial mask could not detect a person\"}") ==
                "Initial mask could not detect a person" &&
            WorkerErrorMessage(
                "{\"stage\":\"error\",\"message\":\"RVM could not find a usable person mask in the opening frames\"}") ==
                "Initial mask could not detect a person in the opening frames." &&
            WorkerErrorMessage(
                "{\"stage\":\"error\",\"message\":\"CUDA error: out of memory\\nSearch for cudaErrorMemoryAllocation\"}") ==
                "GPU out of memory. Reduce Matting detail and retry." &&
            MatAnyoneFullResolution4kRisk("rvm-matanyone2", 0, 3840, 2160) &&
            MatAnyoneFullResolution4kRisk("matanyone2", 0, 2160, 3840) &&
            !MatAnyoneFullResolution4kRisk("rvm-matanyone2", 1024, 3840, 2160) &&
            !MatAnyoneFullResolution4kRisk("rvm-matanyone2", 0, 1920, 1080) &&
            !MatAnyoneFullResolution4kRisk("quality", 0, 3840, 2160) &&
            WorkerErrorMessage("{\"stage\":\"processing\",\"message\":\"Working\"}") == null;
    }

    internal static bool MatAnyoneFullResolution4kRisk(string algorithm,
        int mattingDetailPx, int width, int height) =>
        algorithm is ("matanyone2" or "rvm-matanyone2") &&
        mattingDetailPx == 0 && (long)width * height >= 3840L * 2160;

    internal static string? WorkerErrorMessage(string line)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty("stage", out JsonElement stage) ||
                !string.Equals(stage.GetString(), "error", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("message", out JsonElement message))
                return null;
            string? value = message.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Contains("CUDA error: out of memory",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Contains("CUDA out of memory",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Contains("cudaErrorMemoryAllocation",
                    StringComparison.OrdinalIgnoreCase))
                return "GPU out of memory. Reduce Matting detail and retry.";
            return string.Equals(value,
                "RVM could not find a usable person mask in the opening frames",
                StringComparison.Ordinal)
                ? "Initial mask could not detect a person in the opening frames."
                : value;
        }
        catch (JsonException) { return null; }
    }

    internal static string? WorkerErrorFromLog(string path)
    {
        try
        {
            string? result = null;
            foreach (string line in File.ReadLines(path))
                result = WorkerErrorMessage(line) ?? result;
            return result;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static IReadOnlyList<string> InstalledSam2Models(
        CustomShowConfiguration configuration)
    {
        try
        {
            string checkpoints = Path.Combine(RuntimeRoot(configuration), "checkpoints");
            List<string> result = [];
            foreach ((string name, string file, long size) in new[]
            {
                ("base-plus", "sam2.1_hiera_base_plus.pt", 323606802L),
                ("small", "sam2.1_hiera_small.pt", 184416285L),
                ("tiny", "sam2.1_hiera_tiny.pt", 156008466L)
            })
            {
                FileInfo checkpoint = new(Path.Combine(checkpoints, file));
                if (checkpoint.Exists && checkpoint.Length == size) result.Add(name);
            }
            return result;
        }
        catch { return []; }
    }

    internal static string RuntimeRoot(CustomShowConfiguration configuration) =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(configuration.PythonExecutable)!, "..", ".."));

    internal static bool IsEdgeTamInstalled(CustomShowConfiguration configuration)
    {
        try
        {
            FileInfo checkpoint = new(Path.Combine(RuntimeRoot(configuration),
                "edgetam", "checkpoints", "edgetam.pt"));
            return OptionalToolInstalled(configuration, "EDGETAM_COMMIT", "edgetam") &&
                checkpoint.Exists && checkpoint.Length == 56116523L;
        }
        catch { return false; }
    }

    internal static bool IsTransNetV2Installed(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "TRANSNETV2_COMMIT", "transnetv2");

    internal static bool IsOmniShotCutInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "OMNISHOTCUT_COMMIT", "omnishotcut") &&
        MarkerMatches(configuration, "OMNISHOTCUT_COMMIT", OmniShotCutRevision) &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "checkpoints",
            "OmniShotCut_ckpt.pth")) && File.Exists(OmniShotCutWorkerPath);

    internal static bool IsMatAnyone2Installed(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "MATANYONE2_COMMIT", "matanyone2") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(MatAnyoneWorkerPath);

    internal static bool IsRvmMatAnyone2Installed(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "MATANYONE2_COMMIT", "matanyone2") &&
        OptionalToolInstalled(configuration, "RVM_COMMIT", "rvm") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "checkpoints",
            "matanyone2.pth")) &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "checkpoints",
            "rvm_resnet50.pth")) && File.Exists(MatAnyoneWorkerPath);

    internal static bool IsVideoMaMaInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "VIDEOMAMA_COMMIT", "videomama") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "videomama-base", "image_encoder",
            "model.fp16.safetensors")) &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "videomama-base", "vae",
            "diffusion_pytorch_model.fp16.safetensors")) &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "videomama-model", "unet",
            "diffusion_pytorch_model.safetensors")) && File.Exists(VideoMaMaWorkerPath);

    internal static bool IsViTMatteSmallInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "VITMATTE_S_REVISION", "vitmatte-s") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "vitmatte-s",
            "model.safetensors")) && File.Exists(ViTMatteWorkerPath);

    internal static bool IsRvmViTMatteSmallInstalled(
        CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "VITMATTE_S_REVISION", "vitmatte-s") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "vitmatte-s",
            "model.safetensors")) && File.Exists(ViTMatteWorkerPath) &&
        OptionalToolInstalled(configuration, "RVM_COMMIT", "rvm") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "checkpoints",
            "rvm_resnet50.pth")) && File.Exists(RvmViTMatteWorkerPath);

    internal static bool IsViTMatteBaseInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "VITMATTE_B_REVISION", "vitmatte-b") &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "vitmatte-b",
            "pytorch_model.bin")) && File.Exists(ViTMatteWorkerPath);

    internal static bool IsProPainterInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "PROPAINTER_STREAMING_COMMIT", "propainter-streaming") &&
        File.Exists(Path.Combine(RuntimeRoot(configuration), "propainter-streaming-weights",
            "propainter-0000-5f3cc1e7.pth")) && File.Exists(ProPainterWorkerPath);

    internal static bool IsStabiloInstalled(CustomShowConfiguration configuration) =>
        OptionalToolInstalled(configuration, "STABILO_COMMIT", "stabilo") &&
        MarkerMatches(configuration, "STABILO_COMMIT", StabiloRevision) &&
        OptionalToolInstalled(configuration, "SAM2_COMMIT", "sam2") &&
        File.Exists(StabiloWorkerPath);

    static bool OptionalToolInstalled(CustomShowConfiguration configuration,
        string marker, string folder)
    {
        try
        {
            string runtime = RuntimeRoot(configuration);
            return File.Exists(Path.Combine(runtime, marker)) &&
                Directory.Exists(Path.Combine(runtime, folder));
        }
        catch { return false; }
    }

    static bool MarkerMatches(CustomShowConfiguration configuration,
        string marker, string expected)
    {
        try
        {
            return string.Equals(File.ReadAllText(Path.Combine(
                RuntimeRoot(configuration), marker)).Trim(), expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool VerifyOptionalToolDetection()
    {
        string runtime = Path.Combine(Path.GetTempPath(),
            "iqp-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            CustomShowConfiguration configuration = new()
            {
                PythonExecutable = Path.Combine(runtime, "venv", "Scripts", "python.exe")
            };
            if (IsTransNetV2Installed(configuration) || IsOmniShotCutInstalled(configuration) ||
                IsMatAnyone2Installed(configuration) ||
                IsRvmMatAnyone2Installed(configuration) || IsVideoMaMaInstalled(configuration) ||
                IsViTMatteSmallInstalled(configuration) ||
                IsRvmViTMatteSmallInstalled(configuration) ||
                IsViTMatteBaseInstalled(configuration) || IsProPainterInstalled(configuration) ||
                IsStabiloInstalled(configuration))
                return false;
            foreach (string directory in new[] { "checkpoints", "transnetv2", "omnishotcut",
                "matanyone2", "rvm",
                "videomama", "sam2", "vitmatte-s", "vitmatte-b",
                "propainter-streaming", "stabilo",
                Path.Combine("videomama-base", "image_encoder"),
                Path.Combine("videomama-base", "vae"),
                Path.Combine("videomama-model", "unet"),
                "propainter-streaming-weights" })
                Directory.CreateDirectory(Path.Combine(runtime, directory));
            foreach (string marker in new[] { "TRANSNETV2_COMMIT", "OMNISHOTCUT_COMMIT",
                "MATANYONE2_COMMIT", "RVM_COMMIT",
                "VIDEOMAMA_COMMIT", "SAM2_COMMIT",
                "VITMATTE_S_REVISION", "VITMATTE_B_REVISION",
                "PROPAINTER_STREAMING_COMMIT", "STABILO_COMMIT" })
                File.WriteAllText(Path.Combine(runtime, marker), "installed");
            File.WriteAllText(Path.Combine(runtime, "OMNISHOTCUT_COMMIT"),
                OmniShotCutRevision);
            File.WriteAllText(Path.Combine(runtime, "STABILO_COMMIT"),
                StabiloRevision);
            foreach (string model in new[] {
                Path.Combine("videomama-base", "image_encoder", "model.fp16.safetensors"),
                Path.Combine("videomama-base", "vae", "diffusion_pytorch_model.fp16.safetensors"),
                Path.Combine("videomama-model", "unet", "diffusion_pytorch_model.safetensors") })
                File.WriteAllBytes(Path.Combine(runtime, model), []);
            File.WriteAllBytes(Path.Combine(runtime, "propainter-streaming-weights",
                "propainter-0000-5f3cc1e7.pth"), []);
            File.WriteAllBytes(Path.Combine(runtime, "vitmatte-s", "model.safetensors"), []);
            File.WriteAllBytes(Path.Combine(runtime, "vitmatte-b", "pytorch_model.bin"), []);
            File.WriteAllBytes(Path.Combine(runtime, "checkpoints", "OmniShotCut_ckpt.pth"), []);
            File.WriteAllBytes(Path.Combine(runtime, "checkpoints", "matanyone2.pth"), []);
            File.WriteAllBytes(Path.Combine(runtime, "checkpoints", "rvm_resnet50.pth"), []);
            return IsTransNetV2Installed(configuration) &&
                IsOmniShotCutInstalled(configuration) &&
                IsMatAnyone2Installed(configuration) &&
                IsRvmMatAnyone2Installed(configuration) && IsVideoMaMaInstalled(configuration) &&
                IsViTMatteSmallInstalled(configuration) &&
                IsRvmViTMatteSmallInstalled(configuration) &&
                IsViTMatteBaseInstalled(configuration) &&
                IsProPainterInstalled(configuration) && IsStabiloInstalled(configuration);
        }
        finally { try { Directory.Delete(runtime, true); } catch { } }
    }

    internal static async Task<CustomShowProcessResult> RunSam2MattingAsync(
        CustomShowConfiguration configuration, CustomShowQueueJob job,
        CustomShowClip[] clips, string stagingFolder, string jobAssets,
        string logPath, IProgress<CustomShowProgress>? progress,
        CancellationToken cancellationToken)
    {
        CustomShowProcessing options = job.Manifest.Processing ??
            throw new InvalidDataException("SAM2Matting processing settings are missing.");
        if (!File.Exists(configuration.Sam2MattingPythonExecutable))
            throw new FileNotFoundException(
                "Install the SAM2Matting Python 3.10 environment first.",
                configuration.Sam2MattingPythonExecutable);
        if (!File.Exists(Sam2MattingWorkerPath))
            throw new FileNotFoundException(
                "The SAM2Matting adapter worker is missing.", Sam2MattingWorkerPath);
        Directory.CreateDirectory(stagingFolder);
        string cancelPath = Path.Combine(stagingFolder, ".sam2matting-cancel");
        try { File.Delete(cancelPath); } catch { }
        HashSet<string> included = clips.Select(clip => clip.Id).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        string requestPath = Path.Combine(stagingFolder, "sam2matting-request.json");
        CustomShowStore.WriteJsonAtomic(requestPath, new
        {
            protocolVersion = 1,
            type = "run",
            jobId = job.Id,
            sourcePath = job.SourcePath,
            outputPath = stagingFolder,
            runtimePath = Sam2MattingSupport.RuntimeRoot,
            cancelPath,
            tracker = options.Tracker,
            sourceRevision = options.SourceRevision,
            checkpointRevision = options.CheckpointRevision,
            checkpointSha256 = options.CheckpointSha256,
            concepts = options.ForegroundConcepts,
            clips = clips.Select(clip => new
            {
                id = clip.Id, startMs = clip.StartMs, endMs = clip.EndMs
            }).ToArray(),
            scenes = options.Scenes.Where(scene => included.Contains(scene.ClipId)),
            prompts = job.ScenePrompts.Where(prompt => options.Scenes.Any(scene =>
                    included.Contains(scene.ClipId) && scene.Id.Equals(prompt.SceneId,
                        StringComparison.OrdinalIgnoreCase)))
                .Select(prompt => new
                {
                    sceneId = prompt.SceneId,
                    promptFrame = prompt.PromptFrame,
                    promptFrameMs = prompt.PromptFrameMs,
                    initialMaskPath = Path.Combine(jobAssets,
                        prompt.InitialMaskAsset.Replace('/', Path.DirectorySeparatorChar))
                }).ToArray()
        });
        using IDisposable gpuLease = await CustomShowGpuScheduler.AcquireAsync(
            cancellationToken);
        CustomShowProcessResult result;
        try
        {
            try
            {
                result = await Sam2MattingWorkerHost.RunAsync(configuration,
                    options.Tracker!, requestPath, cancelPath, logPath, progress,
                    cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                error is EndOfStreamException or IOException)
            {
                progress?.Report(new CustomShowProgress("worker-restart", 0,
                    "Persistent worker unavailable; using the isolated fallback"));
                result = await RunSam2MattingOneShotAsync(configuration, requestPath,
                    cancelPath, logPath, progress, cancellationToken);
            }
        }
        finally
        {
            try { File.Delete(cancelPath); } catch { }
        }
        if (result.Tracker != options.Tracker || result.Clips == null ||
            result.Clips.Any(clip => clip.ForegroundFrameCount !=
                    clip.AlphaFrameCount || clip.DecodedFrameCount !=
                    clip.AlphaFrameCount || clip.AlphaCodec != "ffv1" ||
                    clip.AlphaPixelFormat != "gray16le"))
            throw new InvalidDataException(
                "SAM2Matting output validation failed; foreground and linear alpha media do not agree.");
        return result;
    }

    static async Task<CustomShowProcessResult> RunSam2MattingOneShotAsync(
        CustomShowConfiguration configuration, string requestPath,
        string cancelPath, string logPath, IProgress<CustomShowProgress>? progress,
        CancellationToken token)
    {
        ProcessStartInfo start = new(configuration.Sam2MattingPythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Sam2MattingSupport.RuntimeRoot
        };
        foreach (string argument in new[]
        {
            Sam2MattingWorkerPath, "--request", requestPath
        }) start.ArgumentList.Add(argument);
        start.Environment["IQP_FFMPEG"] = Path.Combine(
            AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(
            AppContext.BaseDirectory, "ffprobe.exe");
        start.Environment["PYTHONUNBUFFERED"] = "1";
        ConfigureProcessingPriorities(start, configuration);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException(
                "The isolated SAM2Matting worker could not be started.");
        StringBuilder log = new();
        Task stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
                lock (log) log.AppendLine(line);
        });
        using CancellationTokenRegistration cancellation = token.Register(() =>
        {
            try { File.WriteAllText(cancelPath, "cancel"); } catch { }
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });
        });
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
            {
                lock (log) log.AppendLine(line);
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (!root.TryGetProperty("stage", out JsonElement stageNode))
                        continue;
                    string stage = stageNode.GetString() ?? "processing";
                    string message = root.TryGetProperty("message",
                        out JsonElement messageNode)
                        ? messageNode.GetString() ?? stage : stage;
                    progress?.Report(new CustomShowProgress(stage,
                        root.TryGetProperty("percent", out JsonElement percent)
                            ? percent.GetDouble() : 0, message));
                }
                catch (JsonException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await stderr;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"SAM2Matting processing failed (exit code {process.ExitCode}).");
            return JsonSerializer.Deserialize<CustomShowProcessResult>(
                await File.ReadAllTextAsync(Path.Combine(
                    Path.GetDirectoryName(requestPath)!, "result.json"), token),
                CustomShowStore.JsonOptions) ?? throw new InvalidDataException(
                    "The isolated SAM2Matting worker returned an invalid result.");
        }
        finally
        {
            string text;
            lock (log) text = log.ToString();
            await File.AppendAllTextAsync(logPath, text, CancellationToken.None);
        }
    }

    internal static async Task<CustomShowProcessResult> RunAsync(
        CustomShowConfiguration configuration, string source,
        string stagingFolder, string preset, string? initialMask,
        string? maskFolder,
        int mattingResolution, int sequenceChunk, long startMs, long? endMs,
        string? logPath, bool appendLog,
        IProgress<CustomShowProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<CustomShowProcessJob>? jobs = null,
        long? maskFrameMs = null,
        int rvmInitializerAlphaThresholdPercent = 40,
        int vitMatteInferenceDetailPx = 1024)
    {
        using IDisposable gpuLease = await CustomShowGpuScheduler.AcquireAsync(
            cancellationToken);
        string python = configuration.PythonExecutable;
        if (!File.Exists(python))
            throw new FileNotFoundException(
                "Configure the Python 3.11-3.14 custom-show environment first.", python);
        string worker = preset switch
        {
            "matanyone2" or "rvm-matanyone2" => MatAnyoneWorkerPath,
            "rvm-vitmatte-s" => RvmViTMatteWorkerPath,
            "videomama" => VideoMaMaWorkerPath,
            "vitmatte-s" or "vitmatte-b" => ViTMatteWorkerPath,
            _ => WorkerPath
        };
        if (!File.Exists(worker))
            throw new FileNotFoundException("The processing worker is missing.", worker);
        if (preset == "matanyone2" && !File.Exists(initialMask))
            throw new FileNotFoundException("Create the initial foreground mask first.", initialMask);
        if (preset is "videomama" or "vitmatte-s" or "vitmatte-b" &&
            !Directory.Exists(maskFolder))
            throw new DirectoryNotFoundException(
                "Review and accept the SAM2 video masks before processing.");
        Directory.CreateDirectory(stagingFolder);
        string runtime = RuntimeRoot(configuration);
        ProcessStartInfo start = new(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            worker, "--source", source, "--output", stagingFolder,
            "--runtime", runtime
        }) start.ArgumentList.Add(argument);
        if (preset is "matanyone2" or "rvm-matanyone2")
        {
            if (preset == "rvm-matanyone2" && !File.Exists(initialMask))
            {
                start.ArgumentList.Add("--auto-rvm-init");
                start.ArgumentList.Add("--rvm-alpha-threshold");
                start.ArgumentList.Add((Math.Clamp(rvmInitializerAlphaThresholdPercent,
                    10, 90) / 100d).ToString("0.00",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                start.ArgumentList.Add("--mask");
                start.ArgumentList.Add(initialMask!);
            }
            start.ArgumentList.Add("--max-size");
            start.ArgumentList.Add(mattingResolution.ToString());
            if (preset == "matanyone2" || File.Exists(initialMask))
            {
                start.ArgumentList.Add("--mask-frame-ms");
                start.ArgumentList.Add((maskFrameMs ?? startMs).ToString());
            }
            start.ArgumentList.Add("--compile-cutoff-frames");
            start.ArgumentList.Add(Math.Max(1,
                configuration.MatAnyone2CompileCutoffFrames).ToString());
        }
        else if (preset is "videomama" or "vitmatte-s" or "vitmatte-b")
        {
            start.ArgumentList.Add("--mask-folder");
            start.ArgumentList.Add(maskFolder!);
            start.ArgumentList.Add("--batch-size");
            start.ArgumentList.Add((sequenceChunk == 0 ? 0 :
                Math.Clamp(sequenceChunk, 1, 12)).ToString());
            if (preset.StartsWith("vitmatte", StringComparison.Ordinal))
            {
                start.ArgumentList.Add("--max-size");
                start.ArgumentList.Add(vitMatteInferenceDetailPx.ToString());
                start.ArgumentList.Add("--model");
                start.ArgumentList.Add(preset[^1..]);
                start.ArgumentList.Add("--compile-cutoff-frames");
                start.ArgumentList.Add(Math.Max(1, preset.EndsWith("-b",
                    StringComparison.Ordinal) ? configuration.VitMatteBaseCompileCutoffFrames :
                    configuration.VitMatteSmallCompileCutoffFrames).ToString());
            }
        }
        else if (preset == "rvm-vitmatte-s")
        {
            if (endMs == null)
                throw new InvalidDataException(
                    "RVM-ViTMatte processing requires a bounded clip range.");
            if (Directory.Exists(maskFolder))
            {
                start.ArgumentList.Add("--mask-folder");
                start.ArgumentList.Add(maskFolder!);
            }
            start.ArgumentList.Add("--batch-size");
            start.ArgumentList.Add((sequenceChunk == 0 ? 0 :
                Math.Clamp(sequenceChunk, 1, 12)).ToString());
            start.ArgumentList.Add("--max-size");
            start.ArgumentList.Add(vitMatteInferenceDetailPx.ToString());
            start.ArgumentList.Add("--rvm-alpha-threshold");
            start.ArgumentList.Add((Math.Clamp(rvmInitializerAlphaThresholdPercent,
                10, 90) / 100d).ToString("0.00",
                System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--compile-cutoff-frames");
            start.ArgumentList.Add(Math.Max(1,
                configuration.VitMatteSmallCompileCutoffFrames).ToString());
        }
        else
        {
            int preferredChunk = preset == "fast" ?
                configuration.RvmFastPreferredChunk :
                configuration.RvmQualityPreferredChunk;
            int compileCutoff = preset == "fast" ?
                configuration.RvmFastCompileCutoffFrames :
                configuration.RvmQualityCompileCutoffFrames;
            if (mattingResolution != 512 || sequenceChunk == 0 ||
                sequenceChunk != preferredChunk)
                compileCutoff = 0;
            foreach (string argument in new[]
            {
                "--preset", preset, "--matting-resolution", mattingResolution.ToString(),
                "--sequence-chunk", (sequenceChunk == 0 ? 0 :
                    Math.Clamp(sequenceChunk, 1, 24)).ToString(),
                "--compile-cutoff-frames", Math.Max(0, compileCutoff).ToString()
            }) start.ArgumentList.Add(argument);
            if (jobs != null)
                foreach (CustomShowProcessJob job in jobs)
                {
                    start.ArgumentList.Add("--job");
                    start.ArgumentList.Add(JsonSerializer.Serialize(new
                    {
                        output = job.Output,
                        startMs = job.StartMs,
                        endMs = job.EndMs
                    }));
                }
        }
        start.ArgumentList.Add("--encoder-preset");
        start.ArgumentList.Add(ValidNvencPreset(configuration.RvmNvencPreset));
        if (jobs == null)
        {
            start.ArgumentList.Add("--start-ms");
            start.ArgumentList.Add(Math.Max(0, startMs).ToString());
            if (endMs != null)
            {
                start.ArgumentList.Add("--end-ms");
                start.ArgumentList.Add(endMs.Value.ToString());
            }
        }
        start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        ConfigureProcessingPriorities(start, configuration);

        using Process process = new() { StartInfo = start };
        StringBuilder log = new();
        string? workerError = null;
        process.Start();
        await using ProcessCancellationScope cancellationScope =
            new(process, cancellationToken);
        Task stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
                lock (log) log.AppendLine(line);
        });
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
            {
                lock (log) log.AppendLine(line);
                try
                {
                    using JsonDocument json = JsonDocument.Parse(line);
                    JsonElement root = json.RootElement;
                    string progressStage = root.TryGetProperty("stage", out var stage)
                        ? stage.GetString() ?? "processing" : "processing";
                    string progressMessage = root.TryGetProperty("message", out var message)
                        ? message.GetString() ?? "" : "";
                    if (string.Equals(progressStage, "error",
                            StringComparison.OrdinalIgnoreCase))
                        workerError = WorkerErrorMessage(line) ?? workerError;
                    bool showingRvmMask =
                        (preset == "rvm-vitmatte-s" &&
                            progressStage == "rvm-masks") ||
                        (preset == "rvm-matanyone2" &&
                            progressStage == "initializing");
                    progress?.Report(new CustomShowProgress(
                        progressStage,
                        root.TryGetProperty("percent", out var percent) ? percent.GetDouble() : 0,
                        progressMessage,
                        File.Exists(Path.Combine(stagingFolder, "preview-source.jpg"))
                            ? Path.Combine(stagingFolder, "preview-source.jpg") : null,
                        File.Exists(Path.Combine(stagingFolder, "preview-composite.jpg"))
                            ? Path.Combine(stagingFolder, "preview-composite.jpg") : null,
                        preset is "rvm-vitmatte-s" or "rvm-matanyone2"
                            ? "Original input" : null,
                        preset is "rvm-vitmatte-s" or "rvm-matanyone2"
                            ? showingRvmMask ? "RVM mask" : "Composited result"
                            : null));
                }
                catch (JsonException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await stderr;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    workerError ??
                    $"Foreground processing failed (exit code {process.ExitCode}).");
            string resultPath = Path.Combine(stagingFolder, "result.json");
            return JsonSerializer.Deserialize<CustomShowProcessResult>(
                await File.ReadAllTextAsync(resultPath, cancellationToken),
                CustomShowStore.JsonOptions) ??
                throw new InvalidDataException("The worker did not return media information.");
        }
        finally
        {
            string text;
            lock (log) text = log.ToString();
            string destination = logPath ?? Path.Combine(stagingFolder, "processing.log");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (appendLog)
                await File.AppendAllTextAsync(destination, text, CancellationToken.None);
            else
                await File.WriteAllTextAsync(destination, text, CancellationToken.None);
            foreach (string preview in new[] { "preview-source.jpg", "preview-composite.jpg" })
                try { File.Delete(Path.Combine(stagingFolder, preview)); } catch { }
        }
    }

    internal static void ConfigureProcessingPriorities(ProcessStartInfo start,
        CustomShowConfiguration configuration)
    {
        start.Environment["IQP_CPU_PRIORITY"] = CustomShowConfiguration.IsProcessingPriority(
            configuration.ProcessingCpuPriority) ? configuration.ProcessingCpuPriority : "normal";
        start.Environment["IQP_GPU_PRIORITY"] = CustomShowConfiguration.IsProcessingPriority(
            configuration.ProcessingGpuPriority) ? configuration.ProcessingGpuPriority : "normal";
    }

    internal static async Task GenerateRvmInitialMaskAsync(
        CustomShowConfiguration configuration, string source, string destination,
        long frameMs, int thresholdPercent,
        IProgress<CustomShowProgress>? progress, CancellationToken token)
    {
        if (!File.Exists(configuration.PythonExecutable))
            throw new FileNotFoundException(
                "Configure the Python custom-show environment first.",
                configuration.PythonExecutable);
        if (!File.Exists(RvmInitialMaskWorkerPath))
            throw new FileNotFoundException(
                "The RVM initial-mask worker is missing.",
                RvmInitialMaskWorkerPath);
        ProcessStartInfo start = new(configuration.PythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            RvmInitialMaskWorkerPath, "--source", source,
            "--runtime", RuntimeRoot(configuration), "--mask", destination,
            "--frame-ms", frameMs.ToString(CultureInfo.InvariantCulture),
            "--alpha-threshold", (Math.Clamp(thresholdPercent, 10, 90) / 100d)
                .ToString("0.00", CultureInfo.InvariantCulture)
        }) start.ArgumentList.Add(argument);
        start.Environment["IQP_FFMPEG"] = Path.Combine(
            AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(
            AppContext.BaseDirectory, "ffprobe.exe");
        ConfigureProcessingPriorities(start, configuration);
        using Process process = new() { StartInfo = start };
        StringBuilder errors = new();
        process.Start();
        await using ProcessCancellationScope cancellation = new(process, token);
        Task stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
                errors.AppendLine(line);
        });
        while (await process.StandardOutput.ReadLineAsync() is string line)
            try
            {
                using JsonDocument json = JsonDocument.Parse(line);
                JsonElement root = json.RootElement;
                if (root.TryGetProperty("status", out JsonElement status) &&
                    status.GetString() == "error")
                    throw new InvalidOperationException(
                        root.GetProperty("message").GetString());
                progress?.Report(new CustomShowProgress(
                    "rvm-initial-mask",
                    root.TryGetProperty("percent", out JsonElement percent)
                        ? percent.GetDouble() : 0,
                    root.TryGetProperty("message", out JsonElement message)
                        ? message.GetString() ?? "Creating RVM person mask..."
                        : "Creating RVM person mask..."));
            }
            catch (JsonException) { }
        await process.WaitForExitAsync(CancellationToken.None);
        await stderr;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || !File.Exists(destination))
            throw new InvalidOperationException(errors.Length == 0
                ? "RVM could not create the SAM2 initialization mask."
                : errors.ToString().Trim());
    }

    internal static string ValidNvencPreset(string? value) => value is
        "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7" ? value : "p5";

    internal static async Task GenerateCoverAsync(string source, string destination,
        long positionMs, string modelName, string showTitle, Color titleColor,
        CancellationToken token)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-y", "-v", "error", "-ss",
            (Math.Max(0, positionMs) / 1000d).ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture),
            "-i", source, "-frames:v", "1", "-vf",
            "scale=600:900:force_original_aspect_ratio=increase,crop=600:900", destination })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg could not generate the cover image.");
        string error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Cover generation failed: " + error.Trim());
        using Image image = Image.FromFile(destination);
        using Bitmap cover = new(image);
        DrawCoverText(cover, modelName, showTitle, titleColor);
        image.Dispose();
        cover.Save(destination, System.Drawing.Imaging.ImageFormat.Jpeg);
    }

    static void DrawCoverText(Bitmap cover, string modelName, string showTitle,
        Color titleColor)
    {
        using Graphics graphics = Graphics.FromImage(cover);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using Font modelFont = FittedFont(graphics, modelName, "Segoe Script", 74, 540);
        using Font titleFont = FittedFont(graphics, showTitle.ToUpperInvariant(),
            "Segoe UI", 42, 540, FontStyle.Bold);
        DrawCentered(graphics, modelName, modelFont, Color.White, 700);
        DrawCentered(graphics, showTitle.ToUpperInvariant(), titleFont, titleColor, 810);
    }

    static Font FittedFont(Graphics graphics, string text, string family,
        float size, float width, FontStyle style = FontStyle.Regular)
    {
        using Font measure = new(family, size, style, GraphicsUnit.Pixel);
        float measured = graphics.MeasureString(text, measure).Width;
        return new Font(family, measured > width ? size * width / measured : size,
            style, GraphicsUnit.Pixel);
    }

    static void DrawCentered(Graphics graphics, string text, Font font,
        Color color, float y)
    {
        using StringFormat format = new() { Alignment = StringAlignment.Center };
        RectangleF area = new(20, y, 560, font.Height + 12);
        using Brush shadow = new SolidBrush(Color.FromArgb(210, Color.Black));
        using Brush foreground = new SolidBrush(color);
        graphics.DrawString(text, font, shadow,
            new RectangleF(area.X + 3, area.Y + 3, area.Width, area.Height), format);
        graphics.DrawString(text, font, foreground, area, format);
    }

    internal static bool VerifyCoverOverlay()
    {
        using Bitmap cover = new(600, 900);
        DrawCoverText(cover, "Model", "Show", Color.Magenta);
        for (int y = 740; y < cover.Height; y += 5)
            for (int x = 0; x < cover.Width; x += 5)
                if (cover.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) return true;
        return false;
    }

    internal static async Task<string> ValidateAsync(
        CustomShowConfiguration configuration, CancellationToken token,
        IProgress<string>? progress = null)
    {
        progress?.Report("Checking bundled FFmpeg libraries...");
        if (!FfmpegCpuDecoder.VerifyRuntime())
            throw new InvalidOperationException(
                "QuickPlayer requires the bundled FFmpeg 8 libavcodec 62 runtime.");
        string temporary = Path.Combine(Path.GetTempPath(),
            "iqp-custom-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string runtime = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(configuration.PythonExecutable)!, "..", ".."));
            foreach (string argument in new[] { WorkerPath, "--validate", "--runtime", runtime })
                start.ArgumentList.Add(argument);
            start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("Python could not be started.");
            StringBuilder output = new(), error = new();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data?.StartsWith("STATUS:") == true)
                    progress?.Report(e.Data[7..]);
                else if (e.Data != null) lock (output) output.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { await process.WaitForExitAsync(token); }
            catch
            {
                if (!process.HasExited) process.Kill(true);
                throw;
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error.ToString());
            progress?.Report("Checking custom library write access...");
            using FileStream writeCheck = new(Path.Combine(configuration.LibraryRoot,
                ".write-check"), FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return output.ToString().Trim();
        }
        finally
        {
            try { Directory.Delete(temporary, true); } catch { }
        }
    }
}

internal sealed class CustomShowProcessingForm : Form
{
    readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 28 };
    readonly Label processDescription = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold)
    };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    readonly Button cancel = new() { Dock = DockStyle.Bottom, Text = "Cancel", Height = 36 };
    readonly DxgiMaskPreviewControl source = new() { Dock = DockStyle.Fill,
        AccessibleName = "Original input preview" };
    readonly DxgiMaskPreviewControl composite = new() { Dock = DockStyle.Fill,
        AccessibleName = "Composited result preview" };
    readonly GroupBox sourceGroup;
    readonly GroupBox compositeGroup;
    readonly CancellationTokenSource cancellation = new();
    readonly Stopwatch elapsed = new();
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    readonly Func<IProgress<CustomShowProgress>, CancellationToken,
        Task<CustomShowProcessResult>> operation;
    readonly List<(double Seconds, double Percent)> progressSamples = [];
    readonly List<(double Seconds, long Frames)> frameSamples = [];
    string statusMessage = "Starting processing tools...";
    double percent;
    long frameSampleTotal = -1;
    DateTime lastPreviewUtc;
    internal CustomShowProcessResult? Result { get; private set; }

    internal CustomShowProcessingForm(Func<IProgress<CustomShowProgress>,
        CancellationToken, Task<CustomShowProcessResult>> operation,
        string text = "Processing Custom Show", string sourceLabel = "Original input",
        string compositeLabel = "Composited result", string? processDescription = null,
        bool showPreviews = true)
    {
        this.operation = operation;
        Text = text;
        ClientSize = showPreviews ? new Size(1000, 680) : new Size(620, 190);
        MinimumSize = showPreviews ? new Size(700, 500) : new Size(500, 180);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill,
            RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,
            processDescription == null ? 0 : 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(showPreviews ? SizeType.Percent :
            SizeType.Absolute, showPreviews ? 100 : 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        this.processDescription.Text = processDescription ?? "";
        TableLayoutPanel previews = new() { Dock = DockStyle.Fill,
            RowCount = 1, ColumnCount = 2, Padding = new Padding(8) };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sourceGroup = PreviewGroup(sourceLabel, source);
        compositeGroup = PreviewGroup(compositeLabel, composite);
        previews.Controls.Add(sourceGroup, 0, 0);
        previews.Controls.Add(compositeGroup, 1, 0);
        layout.Controls.Add(this.processDescription, 0, 0);
        layout.Controls.Add(bar, 0, 1);
        layout.Controls.Add(previews, 0, 2);
        layout.Controls.Add(status, 0, 3);
        layout.Controls.Add(cancel, 0, 4);
        Controls.Add(layout);
        cancel.Click += (_, _) => { cancel.Enabled = false; status.Text = "Cancelling..."; cancellation.Cancel(); };
        clock.Tick += (_, _) => RefreshStatus();
        Shown += (_, e) =>
        {
            elapsed.Start();
            RefreshStatus();
            clock.Start();
            Run(this, e);
        };
        FormClosing += (_, e) => { if (Result == null && !cancellation.IsCancellationRequested) { e.Cancel = true; cancel.PerformClick(); } };
    }

    async void Run(object? sender, EventArgs e)
    {
        try
        {
            Progress<CustomShowProgress> progress = new(value =>
            {
                percent = Math.Clamp(value.Percent, 0, 100);
                AddProgressSample(progressSamples,
                    elapsed.Elapsed.TotalSeconds, percent);
                statusMessage = string.IsNullOrWhiteSpace(value.Message) ? value.Stage : value.Message;
                AddFrameSample(statusMessage, elapsed.Elapsed.TotalSeconds);
                bool compiling = value.Stage == "preparing" ||
                    statusMessage.StartsWith("Compiling optimized SAM2",
                    StringComparison.Ordinal) || statusMessage.StartsWith(
                    "Loading cached optimized SAM2", StringComparison.Ordinal);
                bar.Style = compiling ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
                if (!compiling) bar.Value = (int)Math.Round(percent);
                if (!string.IsNullOrWhiteSpace(value.PreviewSourceLabel))
                    sourceGroup.Text = value.PreviewSourceLabel;
                if (!string.IsNullOrWhiteSpace(value.PreviewCompositeLabel))
                    compositeGroup.Text = value.PreviewCompositeLabel;
                UpdatePreview(value.PreviewSource, value.PreviewComposite);
            });
            Result = await operation(progress, cancellation.Token);
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException) { DialogResult = DialogResult.Cancel; }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Abort;
        }
        finally { clock.Stop(); elapsed.Stop(); Close(); }
    }

    static GroupBox PreviewGroup(string text, Control picture)
    {
        GroupBox group = new() { Text = text, Dock = DockStyle.Fill,
            Padding = new Padding(6) };
        group.Controls.Add(picture);
        return group;
    }

    void UpdatePreview(string? sourcePath, string? compositePath)
    {
        if (sourcePath == null || compositePath == null) return;
        try
        {
            DateTime sourceWritten = File.GetLastWriteTimeUtc(sourcePath),
                compositeWritten = File.GetLastWriteTimeUtc(compositePath),
                written = sourceWritten > compositeWritten ? sourceWritten : compositeWritten;
            if (written <= lastPreviewUtc) return;
            Bitmap nextSource = LoadPreview(sourcePath),
                nextComposite = LoadPreview(compositePath);
            source.SetSourceOwned(nextSource);
            composite.SetSourceOwned(nextComposite);
            lastPreviewUtc = written;
        }
        catch (IOException) { }
        catch (ArgumentException) { }
    }

    static Bitmap LoadPreview(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using Image source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cancellation.Dispose();
            clock.Dispose();
        }
        base.Dispose(disposing);
    }

    void RefreshStatus()
    {
        TimeSpan? remaining = EstimateRemaining(progressSamples, percent);
        double? fps = EstimateFps(frameSamples);
        status.Text = $"{statusMessage}{Environment.NewLine}Elapsed {FormatDuration(elapsed.Elapsed)}  •  " +
            (fps == null ? "FPS: estimating...  •  " : $"FPS: {fps.Value:0.0}  •  ") +
            (remaining == null ? "Remaining: estimating..." :
                $"Remaining: ~{FormatDuration(remaining.Value)}");
    }

    void AddFrameSample(string message, double seconds)
    {
        if (!TryParseFrameProgress(message, out long frames, out long total)) return;
        if (total != frameSampleTotal || frameSamples.Count > 0 &&
            frames < frameSamples[^1].Frames)
        {
            frameSamples.Clear();
            frameSampleTotal = total;
        }
        if (frameSamples.Count > 0 && frames <= frameSamples[^1].Frames) return;
        frameSamples.Add((seconds, frames));
        double cutoff = seconds - 30;
        while (frameSamples.Count > 2 && frameSamples[1].Seconds < cutoff)
            frameSamples.RemoveAt(0);
    }

    static bool TryParseFrameCount(string value, out long result)
    {
        string digits = new(value.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None,
            CultureInfo.InvariantCulture, out result);
    }

    static bool TryParseFrameProgress(string message, out long frames, out long total)
    {
        frames = total = 0;
        Match match = Regex.Match(message,
            @"\b([\d,._\s]+)\s*/\s*([\d,._\s]+)\s+frames\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success &&
            TryParseFrameCount(match.Groups[1].Value, out frames) &&
            TryParseFrameCount(match.Groups[2].Value, out total);
    }

    static double? EstimateFps(IReadOnlyList<(double Seconds, long Frames)> samples)
    {
        if (samples.Count < 2) return null;
        double seconds = samples[^1].Seconds - samples[0].Seconds;
        long frames = samples[^1].Frames - samples[0].Frames;
        if (seconds < 3 || frames <= 0) return null;
        double fps = frames / seconds;
        return double.IsFinite(fps) && fps > 0 ? fps : null;
    }

    static TimeSpan? EstimateRemaining(
        IReadOnlyList<(double Seconds, double Percent)> samples,
        double percent)
    {
        if (samples.Count < 4 || percent >= 100) return null;
        double elapsedSeconds = samples[^1].Seconds;
        if (elapsedSeconds < 3 || percent <= 0) return null;
        // Percent is a global job-work fraction. Use the whole elapsed run,
        // including setup and earlier chunks, rather than extrapolating from a
        // fast local chunk or concept transition.
        double seconds = elapsedSeconds * (100 - percent) / percent;
        return double.IsFinite(seconds) && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds) : null;
    }

    static void AddProgressSample(
        List<(double Seconds, double Percent)> samples,
        double seconds, double percent)
    {
        if (percent <= 0 || percent >= 100 ||
            samples.Count > 0 && percent <= samples[^1].Percent)
            return;
        samples.Add((seconds, percent));
        double cutoff = seconds - 30;
        while (samples.Count > 2 && samples[1].Seconds < cutoff)
            samples.RemoveAt(0);
    }

    static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    internal static double AggregateClipPercent(long completedDuration,
        long clipDuration, long totalDuration, double clipPercent) =>
        totalDuration <= 0 ? 0 : 100 * (completedDuration +
            Math.Max(0, clipDuration) * Math.Clamp(clipPercent, 0, 100) / 100) /
            totalDuration;

    internal static bool VerifyEstimate()
    {
        List<(double Seconds, double Percent)> fast = [];
        for (int index = 1; index <= 100; index++)
            AddProgressSample(fast, index * 0.05, index * 0.5);
        return EstimateRemaining(
                   [(5, 20), (10, 30), (15, 40), (20, 50)], 50) ==
               TimeSpan.FromSeconds(20) &&
            EstimateRemaining([(5, 20), (10, 30), (15, 40)], 40) == null &&
            EstimateRemaining(fast, 50) is not null &&
            Math.Abs(EstimateFps([(2, 10L), (7, 60L)])!.Value - 10) < .001 &&
            TryParseFrameCount("8,590", out long formattedFrames) &&
            formattedFrames == 8_590 &&
            TryParseFrameProgress("Removed object from 6,200/14,960 frames",
                out long removedFrames, out long removalTotal) &&
            removedFrames == 6_200 && removalTotal == 14_960 &&
            AggregateClipPercent(0, 10_000, 100_000, 100) == 10 &&
            AggregateClipPercent(10_000, 90_000, 100_000, 50) == 55;
    }
}
