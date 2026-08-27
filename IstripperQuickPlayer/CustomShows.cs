using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IStripperQuickPlayer.DataModel;
using Microsoft.VisualBasic.FileIO;
using static IStripperQuickPlayer.DataModel.Enums;

namespace IStripperQuickPlayer;

internal sealed class CustomShowConfiguration
{
    internal const int MatAnyoneLongTermMinimumMemoryFrames = 6;
    internal const int MatAnyoneLongTermMaximumMemoryFrames = 14;

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "custom-shows-settings.json");

    public string LibraryRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "custom-shows");
    public string PythonExecutable { get; set; } = FindPythonExecutable();
    public string Sam2MattingPythonExecutable { get; set; } =
        FindSam2MattingPythonExecutable();
    public int SmallPlayerVolume { get; set; } = 100;
    public int LargePlayerVolume { get; set; } = 100;
    public int DefaultAlphaThreshold { get; set; } =
        CustomShowClip.DefaultAlphaThreshold;
    public int FullOpacityThreshold { get; set; } = 200;
    public int Sam2FrameCacheSizeGb { get; set; } = 10;
    public int TransNetPreferredBatchSize { get; set; } = 8;
    public int TransNetCompileCutoffFrames { get; set; } = 16000;
    public string TransNetDecodeMode { get; set; } = "auto";
    public string LastClipDetector { get; set; } = "transnetv2";
    public int FastClipDetectionSensitivity { get; set; } = 65;
    public int TransNetClipDetectionSensitivity { get; set; } = 50;
    public int OmniShotCutClipDetectionSensitivity { get; set; } = 100;
    public int RvmQualityPreferredChunk { get; set; } = 12;
    public int RvmFastPreferredChunk { get; set; } = 12;
    public int RvmQualityCompileCutoffFrames { get; set; }
    public int RvmFastCompileCutoffFrames { get; set; }
    public string RvmNvencPreset { get; set; } = "p5";
    public string ProcessingCpuPriority { get; set; } = "normal";
    public string ProcessingGpuPriority { get; set; } = "normal";
    public int MatAnyone2CompileCutoffFrames { get; set; } = 16000;
    public int Sam2BasePlusCompileCutoffFrames { get; set; } = 16000;
    public int Sam2SmallCompileCutoffFrames { get; set; } = 16000;
    public int Sam2TinyCompileCutoffFrames { get; set; } = 16000;
    public int VitMatteSmallCompileCutoffFrames { get; set; } = 16000;
    public int VitMatteBaseCompileCutoffFrames { get; set; } = 16000;
    public int VitMatteSmallPreferredBatchSize { get; set; } = 2;
    public int VitMatteBasePreferredBatchSize { get; set; } = 1;
    public string LastProcessingAlgorithm { get; set; } = "quality";
    public string LastRvmOnnxModel { get; set; } = "mobilenetv3";
    public string LastRvmOnnxQuality { get; set; } = "balanced";
    public bool LastRvmOnnxTemporal { get; set; } = true;
    public string LastSam2MattingTracker { get; set; } = "sam2.1-base-plus";
    public string LastMaskEngine { get; set; } = "sam2";
    public string LastSam2Model { get; set; } = "base-plus";
    public int LastMattingDetailPx { get; set; } = 512;
    public int LastVitMatteInferenceDetailPx { get; set; } = 1024;
    // Zero means the worker should choose and learn an adaptive batch size.
    public int LastProcessingBatchSize { get; set; }
    public int LastRvmInitializerAlphaThresholdPercent { get; set; } = 40;
    public bool LastRvmMatAnyoneMaskRefresh { get; set; }
    public bool LastPropSegmenterEveryFrame { get; set; }
    // null means a pre-dropdown configuration that should inherit the former
    // global default once; an empty string explicitly means no prop model.
    public string? LastPropSegmenterModelId { get; set; }
    public bool LastDebugPropContribution { get; set; }
    public int LastRvmMatAnyoneRefreshStrengthPercent { get; set; } = 100;
    public int MatAnyoneMemoryDefaultsVersion { get; set; } = 1;
    public int LastMatAnyoneMaxMemoryFrames { get; set; } = 14;
    public bool LastMatAnyoneUseLongTermMemory { get; set; } = true;
    public bool LastTemporalAlphaCleanup { get; set; }
    public int LastTemporalAlphaCleanupWindowFrames { get; set; } = 3;
    public int LastTemporalAlphaCleanupStrengthPercent { get; set; } = 100;
    public int LastTemporalAlphaTrackingStrengthPercent { get; set; } = 100;
    public int LastTemporalAlphaCleanupAlphaThreshold { get; set; } =
        CustomShowClip.DefaultAlphaThreshold;
    public bool LastAutoAcceptAlphaThreshold { get; set; }
    public bool PropSegmenterEnabled { get; set; }
    public string? ActivePropSegmenterModelId { get; set; }

    internal int Sam2CompileCutoffFrames(string model) => model switch
    {
        "small" => Sam2SmallCompileCutoffFrames,
        "tiny" => Sam2TinyCompileCutoffFrames,
        _ => Sam2BasePlusCompileCutoffFrames
    };

    internal static CustomShowConfiguration Load()
    {
        try
        {
            JsonNode? settings = JsonNode.Parse(File.ReadAllText(FilePath));
            // Retired processing options are removed before strict deserialization
            // so settings written by older releases remain usable.
            if (settings is JsonObject settingsObject)
            {
                settingsObject.Remove("videoMaMaPreferredBatchSize");
                settingsObject.Remove("nvidiaVfxSdkRoot");
                if (settingsObject["lastProcessingAlgorithm"]?.GetValue<string>() ==
                    "nvidia-aigs")
                    settingsObject["lastProcessingAlgorithm"] =
                        CustomClipMedia.RvmOnnxMode;
            }
            CustomShowConfiguration configuration =
                settings?.Deserialize<CustomShowConfiguration>(
                    CustomShowStore.JsonOptions) ?? new();
            if (string.IsNullOrWhiteSpace(configuration.PythonExecutable))
                configuration.PythonExecutable = FindPythonExecutable();
            if (string.IsNullOrWhiteSpace(configuration.Sam2MattingPythonExecutable))
                configuration.Sam2MattingPythonExecutable =
                    FindSam2MattingPythonExecutable();
            configuration.DefaultAlphaThreshold = Math.Clamp(
                configuration.DefaultAlphaThreshold, 0, 255);
            int[] rvmChunks = [0, 1, 2, 3, 4, 6, 8, 12, 16, 24];
            if (!rvmChunks.Contains(configuration.RvmQualityPreferredChunk))
                configuration.RvmQualityPreferredChunk = 12;
            if (!rvmChunks.Contains(configuration.RvmFastPreferredChunk))
                configuration.RvmFastPreferredChunk = 12;
            configuration.RvmQualityCompileCutoffFrames = Math.Max(0,
                configuration.RvmQualityCompileCutoffFrames);
            configuration.RvmFastCompileCutoffFrames = Math.Max(0,
                configuration.RvmFastCompileCutoffFrames);
            if (configuration.RvmNvencPreset is not
                ("p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7"))
                configuration.RvmNvencPreset = "p5";
            if (!IsProcessingPriority(configuration.ProcessingCpuPriority))
                configuration.ProcessingCpuPriority = "normal";
            if (!IsProcessingPriority(configuration.ProcessingGpuPriority))
                configuration.ProcessingGpuPriority = "normal";
            if (configuration.TransNetDecodeMode is not ("auto" or "legacy" or "cpu"))
                configuration.TransNetDecodeMode = "auto";
            if (configuration.LastClipDetector is not
                ("ffmpeg" or "transnetv2" or "omnishotcut"))
                configuration.LastClipDetector = "transnetv2";
            configuration.FastClipDetectionSensitivity = Math.Clamp(
                configuration.FastClipDetectionSensitivity, 1, 99);
            configuration.TransNetClipDetectionSensitivity = Math.Clamp(
                configuration.TransNetClipDetectionSensitivity, 1, 99);
            configuration.OmniShotCutClipDetectionSensitivity = Math.Clamp(
                configuration.OmniShotCutClipDetectionSensitivity, 1, 100);
            if (configuration.LastProcessingAlgorithm is not
                ("quality" or "fast" or "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b" or
                 "matanyone2" or "vitmatte-s" or "vitmatte-b" or
                 "sam2matting" or "rvm-onnx"))
                configuration.LastProcessingAlgorithm = "quality";
            if (configuration.LastRvmOnnxQuality is not
                ("fast" or "balanced" or "quality" or
                    RvmOnnxSupport.FullResolution))
                configuration.LastRvmOnnxQuality = "balanced";
            if (configuration.LastRvmOnnxModel is not
                ("mobilenetv3" or "resnet50"))
                configuration.LastRvmOnnxModel = "mobilenetv3";
            if (!Sam2MattingSupport.Trackers.Contains(
                    configuration.LastSam2MattingTracker))
                configuration.LastSam2MattingTracker = "sam2.1-base-plus";
            if (configuration.LastMaskEngine is not
                ("sam2" or "edgetam" or "rvm" or "rvm-sam2"))
                configuration.LastMaskEngine = "sam2";
            if (configuration.LastSam2Model is not ("base-plus" or "small" or "tiny"))
                configuration.LastSam2Model = "base-plus";
            if (configuration.LastMattingDetailPx is not
                (0 or 256 or 384 or 512 or 768 or 1024))
                configuration.LastMattingDetailPx = 512;
            if (configuration.LastVitMatteInferenceDetailPx is not
                (0 or 512 or 768 or 1024))
                configuration.LastVitMatteInferenceDetailPx = 1024;
            if (!rvmChunks.Contains(configuration.LastProcessingBatchSize))
                configuration.LastProcessingBatchSize = 0;
            configuration.LastRvmInitializerAlphaThresholdPercent = Math.Clamp(
                configuration.LastRvmInitializerAlphaThresholdPercent, 10, 90);
            configuration.LastRvmMatAnyoneRefreshStrengthPercent = Math.Clamp(
                configuration.LastRvmMatAnyoneRefreshStrengthPercent, 25, 100);
            if (configuration.ActivePropSegmenterModelId is string propModel &&
                !PropSegmenterPackage.ValidModelId(propModel))
                configuration.ActivePropSegmenterModelId = null;
            if (!string.IsNullOrEmpty(configuration.LastPropSegmenterModelId) &&
                !PropSegmenterPackage.ValidModelId(
                    configuration.LastPropSegmenterModelId))
                configuration.LastPropSegmenterModelId = "";
            if (configuration.MatAnyoneMemoryDefaultsVersion < 1)
            {
                configuration.MatAnyoneMemoryDefaultsVersion = 1;
                configuration.LastMatAnyoneMaxMemoryFrames = 14;
                configuration.LastMatAnyoneUseLongTermMemory = true;
            }
            configuration.LastMatAnyoneMaxMemoryFrames =
                configuration.LastMatAnyoneUseLongTermMemory &&
                    configuration.LastMatAnyoneMaxMemoryFrames <
                        MatAnyoneLongTermMinimumMemoryFrames
                    ? MatAnyoneLongTermMaximumMemoryFrames
                    : Math.Clamp(configuration.LastMatAnyoneMaxMemoryFrames,
                        configuration.LastMatAnyoneUseLongTermMemory
                            ? MatAnyoneLongTermMinimumMemoryFrames : 2,
                        configuration.LastMatAnyoneUseLongTermMemory
                            ? MatAnyoneLongTermMaximumMemoryFrames : 30);
            configuration.LastTemporalAlphaCleanupWindowFrames = Math.Clamp(
                configuration.LastTemporalAlphaCleanupWindowFrames, 1, 30);
            configuration.LastTemporalAlphaCleanupStrengthPercent = Math.Clamp(
                configuration.LastTemporalAlphaCleanupStrengthPercent, 25, 100);
            configuration.LastTemporalAlphaTrackingStrengthPercent = Math.Clamp(
                configuration.LastTemporalAlphaTrackingStrengthPercent, 0, 100);
            configuration.LastTemporalAlphaCleanupAlphaThreshold = Math.Clamp(
                configuration.LastTemporalAlphaCleanupAlphaThreshold, 0, 255);
            return configuration;
        }
        catch { return new(); }
    }

    internal void Save() => CustomShowStore.WriteJsonAtomic(FilePath, this);

    internal static bool IsProcessingPriority(string? value) => value is
        "idle" or "below-normal" or "normal" or "above-normal" or "high";

    internal static string FindPythonExecutable()
    {
        string rvmPython = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "rvm-runtime", "venv", "Scripts", "python.exe");
        if (File.Exists(rvmPython)) return rvmPython;

        return TryPython("py") ?? TryPython("py", "-3.11") ??
            TryPython("python") ?? "";
    }

    internal static string FindSam2MattingPythonExecutable()
    {
        string python = Path.Combine(Sam2MattingSupport.RuntimeRoot,
            "venv", "Scripts", "python.exe");
        return File.Exists(python) ? python : "";
    }

    static string? TryPython(string command, string? launcherVersion = null)
    {
        try
        {
            ProcessStartInfo start = new(command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            if (launcherVersion is not null) start.ArgumentList.Add(launcherVersion);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import sys; print(sys.executable)");
            using Process process = Process.Start(start)!;
            if (!process.WaitForExit(3000))
            {
                process.Kill(true);
                return null;
            }
            string path = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && File.Exists(path)
                ? Path.GetFullPath(path) : null;
        }
        catch { return null; }
    }
}

internal sealed class CustomPerformerProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? IstripperModelId { get; set; }
    public string ModelName { get; set; } = "";
    public string? Gender { get; set; }
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(IstripperModelId)
        ? ModelName : $"{ModelName} (iStripper)";
    public DateOnly? BirthDate { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? BustCm { get; set; }
    public decimal? WaistCm { get; set; }
    public decimal? HipsCm { get; set; }
    public string Hair { get; set; } = "";
    public string Ethnicity { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
}

internal sealed class CustomShowManifest
{
    public int SchemaVersion { get; set; } = 3;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PerformerId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public DateOnly ReleaseDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ShowDate { get; set; }
    public int? AgeAtReleaseOverride { get; set; }
    public string Gender { get; set; } = "Female";
    public decimal? OfficialRating { get; set; }
    public string Hotness { get; set; } = "NoNudity";
    public bool Exclusive { get; set; }
    public int PerformerCount { get; set; } = 1;
    public string[] ClipTypes { get; set; } = ["Standing"];
    public CustomShowClip[] Clips { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public CustomShowSource Source { get; set; } = new();
    public CustomShowMedia Media { get; set; } = new();
    public CustomShowProcessing? Processing { get; set; }
    public CustomShowClipDetection? ClipDetection { get; set; }
    public CustomVirtualGreenScreen? VirtualGreenScreen { get; set; }
}

internal sealed class CustomShowClipDetection
{
    public string Method { get; set; } = "";
    public string? ToolRevision { get; set; }
    public int? OverlapFrames { get; set; }
    public int? SensitivityPercent { get; set; }
    public string? SensitivityDataFormat { get; set; }
    public string? SensitivityData { get; set; }
    public double? DetectionFrameRate { get; set; }
    public long TransitionBufferMs { get; set; }
    public long MinimumClipMs { get; set; } = 20_000;
    public bool ManuallyEdited { get; set; }
}

internal sealed class CustomShowProcessing
{
    public string Algorithm { get; set; } = "";
    public int ProcessingOptionVersion { get; set; }
    public string? Tracker { get; set; }
    public string? PromptMode { get; set; }
    public string[] ForegroundConcepts { get; set; } = [];
    public string? EnvironmentSpecVersion { get; set; }
    public string? SourceRevision { get; set; }
    public string? CheckpointRevision { get; set; }
    public string? CheckpointSha256 { get; set; }
    public string? AttentionPolicy { get; set; }
    public string? EncoderPolicy { get; set; }
    public string? AlphaEncodingPolicy { get; set; }
    public int? ScenePlanVersion { get; set; }
    public CustomShowProcessingScene[] Scenes { get; set; } = [];
    public int MattingDetailPx { get; set; }
    public int? VitMatteInferenceDetailPx { get; set; }
    public int BatchSize { get; set; }
    public int? RvmInitializerAlphaThresholdPercent { get; set; }
    public string? PropSegmenterModelId { get; set; }
    public string? PropSegmenterCheckpointSha256 { get; set; }
    public double? PropSegmenterConfidenceThreshold { get; set; }
    public int? PropSegmenterProximityRadiusAt512 { get; set; }
    public int? PropSegmenterManifestSchemaVersion { get; set; }
    public string? PropSegmenterArchitecture { get; set; }
    public string? PropSegmenterManifestSha256 { get; set; }
    public string? PropSegmenterPostprocessingContract { get; set; }
    public bool RvmMatAnyoneMaskRefresh { get; set; }
    public bool PropSegmenterEveryFrame { get; set; }
    public bool DebugPropContribution { get; set; }
    public int RvmMatAnyoneRefreshStrengthPercent { get; set; } = 100;
    public int MatAnyoneMaxMemoryFrames { get; set; } = 5;
    public bool MatAnyoneUseLongTermMemory { get; set; }
    public bool TemporalAlphaCleanup { get; set; }
    public int TemporalAlphaCleanupWindowFrames { get; set; } = 3;
    public int TemporalAlphaCleanupStrengthPercent { get; set; } = 100;
    public int TemporalAlphaTrackingStrengthPercent { get; set; } = 100;
    public int TemporalAlphaCleanupAlphaThreshold { get; set; } =
        CustomShowClip.DefaultAlphaThreshold;
    public int? AutoAcceptedAlphaThreshold { get; set; }
    public string? Sam2Model { get; set; }
    public string? MaskEngine { get; set; }
    public string ExecutionPolicy { get; set; } = "auto";
    public string? ResolvedExecutionMode { get; set; }
    public int? EffectiveBatchSize { get; set; }
    public int? PipelineDepth { get; set; }
    public string? Encoder { get; set; }
    public string? EncoderPreset { get; set; }
    public string PrecisionPolicy { get; set; } = "fp16-autocast-fp32-cpu-fallback";
    public int RecurrentRefinementSteps { get; set; }
    public DateTime ProcessedUtc { get; set; } = DateTime.UtcNow;
    public string QuickPlayerVersion { get; set; } = "";
    public Dictionary<string, string> ToolRevisions { get; set; } = [];
    public CustomClipProcessing[] Clips { get; set; } = [];
}

internal sealed record PropSegmenterPackage(string ModelId, string Folder,
    string CheckpointSha256, double ConfidenceThreshold, int ProximityRadiusAt512,
    int ManifestSchemaVersion, string Architecture, string ManifestSha256,
    string PostprocessingContract, string DisplayName)
{
    internal static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "rvm-runtime", "prop-segmenter", "models");

    internal static bool ValidModelId(string value) => value.Length is > 0 and <= 120 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    static string PackageDisplayName(JsonElement root, string modelId)
    {
        if (root.TryGetProperty("displayName", out JsonElement configured) &&
            configured.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(configured.GetString()))
            return configured.GetString()!.Trim();
        return modelId;
    }

    internal static IReadOnlyList<PropSegmenterPackage> Installed()
    {
        if (!Directory.Exists(ModelsRoot)) return [];
        List<PropSegmenterPackage> result = [];
        foreach (string folder in Directory.EnumerateDirectories(ModelsRoot))
            if (TryLoad(Path.GetFileName(folder), false, out PropSegmenterPackage? package))
                result.Add(package);
        return result.OrderByDescending(value => value.ModelId).ToArray();
    }

    internal static bool TryLoad(string id, bool verifyCheckpoint,
        out PropSegmenterPackage? package)
    {
        package = null;
        try
        {
            if (!ValidModelId(id)) return false;
            string folder = Path.Combine(ModelsRoot, id);
            string manifestPath = Path.Combine(folder, "manifest.json");
            string checkpoint = Path.Combine(folder, "model.pth");
            if (!File.Exists(manifestPath) || !File.Exists(checkpoint)) return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            string? architecture = root.GetProperty("architecture").GetString();
            if (root.GetProperty("modelId").GetString() != id ||
                !((schemaVersion == 1 && architecture is
                    "deeplabv3-resnet50-binary-v1" or
                    "deeplabv3-resnet50-v1.1-dildo-butt-plug") ||
                  (schemaVersion == 2 && architecture == "rvm-conditioned-convnext-fpn-v2")))
                return false;
            string hash = root.GetProperty("checkpointSha256").GetString()!;
            if (hash.Length != 64 || hash.Any(value => !Uri.IsHexDigit(value))) return false;
            if (verifyCheckpoint)
            {
                string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(checkpoint)));
                if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
                string packageRoot = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
                foreach (JsonProperty item in root.GetProperty("files").EnumerateObject())
                {
                    string file = Path.GetFullPath(Path.Combine(folder,
                        item.Name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!file.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(file)) return false;
                    string fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(file)));
                    if (!fileHash.Equals(item.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            double threshold = root.GetProperty("confidenceThreshold").GetDouble();
            int radius = root.GetProperty("proximityRadiusAt512").GetInt32();
            if (threshold is < .1 or > .9 || radius is < 1 or > 128) return false;
            if (schemaVersion == 2)
            {
                JsonElement input = root.GetProperty("input");
                JsonElement runtime = root.GetProperty("runtime");
                string[] channels = input.GetProperty("channels").EnumerateArray()
                    .Select(value => value.GetString() ?? "").ToArray();
                if (input.GetProperty("cropSize").GetInt32() != 768 ||
                    !channels.SequenceEqual(new[] { "red", "green", "blue", "rvmAlpha",
                        "rvmSignedDistance" }) ||
                    runtime.GetProperty("contract").GetString() !=
                        "rvm-conditioned-temporal-union-v2" ||
                    runtime.GetProperty("pixelThreshold").GetDouble() != threshold ||
                    runtime.GetProperty("presenceThreshold").GetDouble() is < .1 or > .9 ||
                    runtime.GetProperty("temporalWindow").GetInt32() != 3 ||
                    runtime.GetProperty("temporalRequired").GetInt32() != 2 ||
                    runtime.GetProperty("discoveryIntervalSeconds").GetDouble() != 2)
                    return false;
            }
            string contract = schemaVersion == 2
                ? root.GetProperty("runtime").GetProperty("contract").GetString()!
                : root.GetProperty("postprocessing").GetProperty("contract").GetString()!;
            string manifestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(manifestPath))).ToLowerInvariant();
            package = new(id, folder, hash.ToLowerInvariant(), threshold, radius,
                schemaVersion, architecture!, manifestHash, contract,
                PackageDisplayName(root, id)); return true;
        }
        catch { return false; }
    }
}

internal sealed class CustomShowProcessingScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClipId { get; set; } = "";
    public long StartFrame { get; set; }
    public long EndFrameExclusive { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
}

internal sealed class CustomClipProcessing
{
    public string ClipId { get; set; } = "";
    public long? InitialMaskFrameMs { get; set; }
    public bool Sam2MaskTracking { get; set; }
}

internal sealed class CustomShowClip
{
    public const int DefaultAlphaThreshold = 120;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public bool Included { get; set; } = true;
    public string Hotness { get; set; } = "NoNudity";
    public string[] ClipTypes { get; set; } = ["Standing"];
    public int AlphaThreshold { get; set; } = DefaultAlphaThreshold;
    public float EdgeChokePixels { get; set; } = 1;
    public string[] DetectionLabels { get; set; } = [];
    public CustomShowSource? Source { get; set; }
    public long? SourceStartMs { get; set; }
    public long? SourceEndMs { get; set; }
    public CustomClipMedia? Media { get; set; }
    public CustomRvmOnnxSettings? RvmOnnx { get; set; }
    public CustomVirtualGreenScreen? VirtualGreenScreen { get; set; }
}

internal sealed class CustomRvmOnnxSettings
{
    public string Model { get; set; } = "mobilenetv3";
    public string Quality { get; set; } = "balanced";
    public bool Temporal { get; set; } = true;

    internal CustomRvmOnnxSettings Clone() => new()
    {
        Model = Model,
        Quality = Quality,
        Temporal = Temporal
    };
}

internal sealed class CustomVirtualGreenScreen
{
    internal const int MaximumSmallPatchSize = 64;
    public bool Enabled { get; set; } = true;
    public int SmallPatchSize { get; set; }
    public int MinimumTransparentAreaRadius { get; set; }
    public string ColorDomain { get; set; } = "ycbcr";
    public int KeyOverLockedThreshold { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeyFeather { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LockedFeather { get; set; }
    public CustomVirtualGreenScreenColor[] Colors { get; set; } = [];

    internal CustomVirtualGreenScreen Clone() => new()
    {
        Enabled = Enabled,
        SmallPatchSize = SmallPatchSize,
        MinimumTransparentAreaRadius = MinimumTransparentAreaRadius,
        ColorDomain = ColorDomain,
        KeyOverLockedThreshold = KeyOverLockedThreshold,
        KeyFeather = KeyFeather,
        LockedFeather = LockedFeather,
        Colors = Colors.Select(value => new CustomVirtualGreenScreenColor
        {
            Color = value.Color,
            Tolerance = value.Tolerance,
            Feather = value.Feather,
            Locked = value.Locked,
            Disabled = value.Disabled
        }).ToArray()
    };
}

internal sealed class CustomVirtualGreenScreenColor
{
    public string Color { get; set; } = "#00FF00";
    public int Tolerance { get; set; } = 18;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Feather { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
}

internal enum VirtualGreenScreenColorDomain
{
    YCbCr,
    Rgb,
    Hsv,
    OkLab
}

internal readonly record struct VirtualGreenScreenSample(
    float X, float Y, float Z, float Tolerance, float Feather, bool Locked,
    VirtualGreenScreenColorDomain Domain, float KeyOverLockedThreshold)
{
    internal float Cb => X;
    internal float Cr => Y;
}

internal static class VirtualGreenScreenMath
{
    internal static readonly string[] ColorDomains =
        ["ycbcr", "rgb", "hsv", "oklab"];
    internal const int SmallPatchMinimumDiskSupport = 4;
    internal const int SmallPatchMinimumConnectedRays = 2;
    internal const int MinimumTransparentDiskSupport = 8;
    internal const int MinimumTransparentConnectedRays = 3;
    internal const int TransparentAreaSamplesPerRay = 4;
    static readonly PointF[] smallPatchDiskOffsets =
    [
        new(.1767767f, 0), new(-.2257722f, .2068258f),
        new(.0345581f, -.3937712f), new(.2845712f, .3711728f),
        new(-.5222232f, -.0923739f), new(.4946954f, -.3146847f),
        new(-.1654659f, .6155250f), new(-.3155615f, -.6075944f),
        new(.6846422f, .2500302f), new(-.7122561f, .2940090f),
        new(.3433545f, -.7337286f), new(.2537302f, .8089320f),
        new(-.7647459f, -.4431859f), new(.8971340f, -.1972324f),
        new(-.5475069f, .7787722f), new(-.1264868f, -.9760897f)
    ];
    static readonly PointF[] transparentAreaDirections =
    [
        new(1, 0), new(.7071068f, .7071068f), new(0, 1),
        new(-.7071068f, .7071068f), new(-1, 0),
        new(-.7071068f, -.7071068f), new(0, -1),
        new(.7071068f, -.7071068f)
    ];

    internal static ReadOnlySpan<PointF> SmallPatchDiskOffsets =>
        smallPatchDiskOffsets;
    internal static ReadOnlySpan<PointF> TransparentAreaDirections =>
        transparentAreaDirections;

    internal static CustomVirtualGreenScreen Resolve(
        CustomVirtualGreenScreen? show, CustomVirtualGreenScreen? clip) =>
        (clip ?? show)?.Clone() ?? new CustomVirtualGreenScreen { Enabled = false };

    internal static int SmallPatchSize(CustomVirtualGreenScreen? settings) =>
        settings?.Enabled == true ? Math.Clamp(settings.SmallPatchSize, 0,
            CustomVirtualGreenScreen.MaximumSmallPatchSize) : 0;

    internal static int MinimumTransparentAreaRadius(
        CustomVirtualGreenScreen? settings) => settings?.Enabled == true
            ? Math.Clamp(settings.MinimumTransparentAreaRadius, 0,
                CustomVirtualGreenScreen.MaximumSmallPatchSize) : 0;

    internal static VirtualGreenScreenSample[] Samples(
        CustomVirtualGreenScreen? settings)
    {
        if (settings?.Enabled != true || settings.Colors.Length == 0) return [];
        VirtualGreenScreenColorDomain domain = Domain(settings);
        float keyFeather = EffectiveFeather(settings, false);
        float lockedFeather = EffectiveFeather(settings, true);
        return settings.Colors
            .Where(value => !value.Disabled)
            .OrderByDescending(value => value.Locked)
            .Select(value =>
            {
                Color color = ParseColor(value.Color);
                (float x, float y, float z) = Components(color, domain);
                float tolerance = Math.Clamp(value.Tolerance, 0, 100);
                float feather = value.Locked ? lockedFeather : keyFeather;
                return new VirtualGreenScreenSample(
                    x, y, z, tolerance / 255f, feather / 255f, value.Locked,
                    domain, Math.Clamp(settings.KeyOverLockedThreshold, 0, 100) /
                        100f);
            }).ToArray();
    }

    internal static VirtualGreenScreenColorDomain Domain(
        CustomVirtualGreenScreen? settings) =>
        (settings?.ColorDomain ?? "ycbcr").Trim().ToLowerInvariant() switch
        {
            "rgb" => VirtualGreenScreenColorDomain.Rgb,
            "hsv" => VirtualGreenScreenColorDomain.Hsv,
            "oklab" => VirtualGreenScreenColorDomain.OkLab,
            _ => VirtualGreenScreenColorDomain.YCbCr
        };

    internal static (float Cb, float Cr) Chroma(Color color)
    {
        float red = color.R, green = color.G, blue = color.B;
        float cb = 128 + (-.114572f * red - .385428f * green + .5f * blue);
        float cr = 128 + (.5f * red - .454153f * green - .045847f * blue);
        return (Math.Clamp(cb, 0, 255) / 255f,
            Math.Clamp(cr, 0, 255) / 255f);
    }

    internal static float MatchStrength(Color source,
        CustomVirtualGreenScreenColor target,
        CustomVirtualGreenScreen settings)
    {
        VirtualGreenScreenColorDomain domain = Domain(settings);
        (float sourceX, float sourceY, float sourceZ) = Components(source, domain);
        (float targetX, float targetY, float targetZ) =
            Components(ParseColor(target.Color), domain);
        float distance = Distance(sourceX, sourceY, sourceZ,
            targetX, targetY, targetZ);
        float tolerance = Math.Clamp(target.Tolerance, 0, 100) / 255f;
        float feather = EffectiveFeather(settings, target.Locked) / 255f;
        return 1 - SmoothStep(tolerance, tolerance + feather, distance);
    }

    internal static int EffectiveFeather(CustomVirtualGreenScreenColor value) =>
        value.Feather is int feather ? Math.Clamp(feather, 0, 100) :
        Math.Max(3, (int)MathF.Ceiling(Math.Clamp(value.Tolerance, 0, 100) * .25f));

    internal static int EffectiveFeather(CustomVirtualGreenScreen settings,
        bool locked)
    {
        int? configured = locked ? settings.LockedFeather : settings.KeyFeather;
        if (configured is int feather) return Math.Clamp(feather, 0, 100);
        CustomVirtualGreenScreenColor? legacy = settings.Colors.FirstOrDefault(
            value => value.Locked == locked && value.Feather.HasValue) ??
            settings.Colors.FirstOrDefault(value => value.Locked == locked);
        return legacy == null ? 5 : EffectiveFeather(legacy);
    }

    internal static float Keep(float x, float y,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
        => KeepComponents(x, y, 0, samples);

    internal static float Keep(Color source,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        if (samples.Length == 0) return 1;
        (float x, float y, float z) = Components(source, samples[0].Domain);
        return KeepComponents(x, y, z, samples);
    }

    internal static float Keep(float y, float cb, float cr,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        if (samples.Length == 0) return 1;
        (float x, float componentY, float z) = ComponentsFromYcbcr(
            y, cb, cr, samples[0].Domain);
        return KeepComponents(x, componentY, z, samples);
    }

    static float KeepComponents(float x, float y, float z,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        if (samples.Length == 0) return 1;
        float keyStrength = 0, lockedStrength = 0;
        foreach (VirtualGreenScreenSample sample in samples)
        {
            float distance = Distance(x, y, z, sample.X, sample.Y, sample.Z);
            float strength = 1 - SmoothStep(sample.Tolerance,
                sample.Tolerance + sample.Feather, distance);
            if (sample.Locked)
                lockedStrength = Math.Max(lockedStrength, strength);
            else
                keyStrength = Math.Max(keyStrength, strength);
        }
        return keyStrength - lockedStrength > samples[0].KeyOverLockedThreshold
            ? 0 : 1;
    }

    internal static byte Apply(byte alpha, byte cb, byte cr, int lower,
        int upper, CustomVirtualGreenScreen? settings)
        => Apply(alpha, cb, cr, lower, upper, Samples(settings));

    internal static byte Apply(byte alpha, byte cb, byte cr, int lower,
        int upper, ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        float keyed = alpha * Keep(cb / 255f, cr / 255f, samples);
        return ApplyThresholds(keyed, lower, upper);
    }

    internal static byte Apply(byte alpha, byte y, byte cb, byte cr, int lower,
        int upper, ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        float keyed = alpha * Keep(y / 255f, cb / 255f, cr / 255f, samples);
        return ApplyThresholds(keyed, lower, upper);
    }

    static byte ApplyThresholds(float keyed, int lower, int upper)
    {
        return keyed < Math.Clamp(lower, 0, 255) ? (byte)0 :
            keyed >= Math.Clamp(upper, 1, 255) ? (byte)255 :
            (byte)Math.Clamp((int)MathF.Round(keyed), 0, 255);
    }

    static float Distance(float x1, float y1, float z1,
        float x2, float y2, float z2)
    {
        float dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    static (float X, float Y, float Z) Components(Color color,
        VirtualGreenScreenColorDomain domain)
    {
        float red = color.R / 255f, green = color.G / 255f,
            blue = color.B / 255f;
        return Components(red, green, blue, domain);
    }

    static (float X, float Y, float Z) ComponentsFromYcbcr(float y, float cb,
        float cr, VirtualGreenScreenColorDomain domain)
    {
        if (domain == VirtualGreenScreenColorDomain.YCbCr)
            return (cb, cr, 0);
        float scaledY = 1.16438356f * (y - 16f / 255);
        float u = cb - .5f, v = cr - .5f;
        return Components(Math.Clamp(scaledY + 1.79274107f * v, 0, 1),
            Math.Clamp(scaledY - .21324861f * u - .53290933f * v, 0, 1),
            Math.Clamp(scaledY + 2.11240179f * u, 0, 1), domain);
    }

    static (float X, float Y, float Z) Components(float red, float green,
        float blue, VirtualGreenScreenColorDomain domain)
    {
        if (domain == VirtualGreenScreenColorDomain.YCbCr)
        {
            (float cb, float cr) = Chroma(Color.FromArgb(
                (int)MathF.Round(red * 255), (int)MathF.Round(green * 255),
                (int)MathF.Round(blue * 255)));
            return (cb, cr, 0);
        }
        if (domain == VirtualGreenScreenColorDomain.Rgb)
            return (red, green, blue);
        if (domain == VirtualGreenScreenColorDomain.Hsv)
        {
            float maximum = Math.Max(red, Math.Max(green, blue));
            float minimum = Math.Min(red, Math.Min(green, blue));
            float delta = maximum - minimum, hue = 0;
            if (delta > .000001f)
            {
                hue = maximum == red ? (green - blue) / delta :
                    maximum == green ? 2 + (blue - red) / delta :
                    4 + (red - green) / delta;
                hue = (hue / 6 + 1) % 1;
            }
            float saturation = maximum <= 0 ? 0 : delta / maximum;
            float angle = hue * MathF.PI * 2;
            return (.5f + MathF.Cos(angle) * saturation * .5f,
                .5f + MathF.Sin(angle) * saturation * .5f, maximum);
        }
        red = Linear(red); green = Linear(green); blue = Linear(blue);
        float l = MathF.Cbrt(.4122214708f * red + .5363325363f * green +
            .0514459929f * blue);
        float m = MathF.Cbrt(.2119034982f * red + .6806995451f * green +
            .1073969566f * blue);
        float s = MathF.Cbrt(.0883024619f * red + .2817188376f * green +
            .6299787005f * blue);
        return (.2104542553f * l + .793617785f * m - .0040720468f * s,
            1.9779984951f * l - 2.428592205f * m + .4505937099f * s + .5f,
            .0259040371f * l + .7827717662f * m - .808675766f * s + .5f);
    }

    static float Linear(float value) => value <= .04045f
        ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);

    internal static bool ReducesAlpha(byte before, byte after) => after < before;

    internal static Color ParseColor(string value)
    {
        if (!IsColor(value)) throw new InvalidDataException(
            "Virtual green-screen colours must use #RRGGBB.");
        return Color.FromArgb(
            int.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture),
            int.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture),
            int.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture));
    }

    internal static string FormatColor(Color value) =>
        $"#{value.R:X2}{value.G:X2}{value.B:X2}";

    internal static bool IsColor(string? value) => value?.Length == 7 &&
        value[0] == '#' && int.TryParse(value.AsSpan(1), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out _);

    static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0) return value <= edge0 ? 0 : 1;
        float amount = Math.Clamp((value - edge0) / Math.Max(.000001f,
            edge1 - edge0), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }
}

internal sealed class CustomClipMedia
{
    public const string PairedAlphaMode = "paired-alpha";
    public const string RvmOnnxMode = "rvm-onnx";
    public static bool IsRealtimeMode(string? mode) =>
        mode == RvmOnnxMode;
    public string Mode { get; set; } = PairedAlphaMode;
    public string Foreground { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alpha { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string FrameRate { get; set; } = "";
    public long DurationMs { get; set; }
    public long PlaybackStartMs { get; set; }
    public long? PlaybackEndMs { get; set; }
}

internal sealed class CustomShowSource
{
    public string Mode { get; set; } = "reference";
    public string Path { get; set; } = "";
}

internal sealed class CustomShowMedia
{
    public string Cover { get; set; } = "cover.jpg";
    public string? CoverSource { get; set; }
    public bool AutoGeneratedCover { get; set; } = true;
    public string CoverTitleColor { get; set; } = "#FF1493";
    public string CoverModelFont { get; set; } = "Segoe Script";
    public string CoverTitleFont { get; set; } = "Segoe UI";
    public string PhotosFolder { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string FrameRate { get; set; } = "";
    public long DurationMs { get; set; }
}

internal sealed record CustomShowCheckIssue(string Folder, string? ShowId,
    string Show, string Error, bool CanRepair);

internal sealed record CustomShowCheckReport(int TotalShows, int ValidShows,
    IReadOnlyList<CustomShowCheckIssue> Issues);

internal sealed class CustomShowStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static readonly string[] HotnessOptions =
        ["Public", "NoNudity", "Topless", "Nudity", "FullNudity", "XXX"];
    static readonly HashSet<string> HotnessValues = [.. HotnessOptions];
    internal static readonly string[] GenderValues =
        ["Male", "Female", "Transgender", "Femboy", "Sissy", "Non-Binary"];
    internal static readonly string[] ClipTypeOptions =
        ["Standing", "Table", "Behind Table", "Swing", "Cage", "Pole",
         "Glass", "Sign", "Prop", "Full Legs", "Side"];
    static readonly HashSet<string> ClipTypeValues = [.. ClipTypeOptions];
    static readonly HashSet<string> ProcessingAlgorithms =
        ["quality", "fast", "matanyone2", "rvm-matanyone2",
         "vitmatte-s", "vitmatte-b", "rvm-vitmatte-s", "rvm-vitmatte-b", "sam2matting",
         "rvm-onnx",
         // Retained only so already-published shows remain playable.
         "videomama"];
    static readonly HashSet<int> MattingDetailValues = [0, 256, 384, 512, 768, 1024];
    static readonly HashSet<int> BatchSizeValues = [0, 1, 2, 3, 4, 6, 8, 12, 16, 24];
    static readonly HashSet<string> Sam2Models = ["base-plus", "small", "tiny"];
    static readonly HashSet<string> ClipDetectionMethods =
        ["ffmpeg", "transnetv2", "omnishotcut"];
    static readonly HashSet<string> DetectionLabels =
        ["General", "Skipped", "Dissolve", "Wipes", "Push", "Slide", "Zoom", "Fade", "Doorway", "Padding",
         "Hard Cut", "Sudden Jump", "Scene change buffer", "Hard Cut buffer",
         "Sudden Jump buffer", "Dissolve buffer", "Wipes buffer", "Push buffer",
         "Slide buffer", "Zoom buffer", "Fade buffer", "Doorway buffer", "Short (<10s)"];
    static readonly Regex ShortDetectionLabel = new(
        @"\AShort \(<(?:0|[1-9]\d*)(?:\.\d{1,3})?s\)\z",
        RegexOptions.CultureInvariant);

    internal static bool IsValidDetectionLabel(string label) =>
        DetectionLabels.Contains(label) || ShortDetectionLabel.IsMatch(label);

    readonly string root;
    internal string Root => root;
    internal string PerformersFolder => Path.Combine(root, "performers");
    internal string ShowsFolder => Path.Combine(root, "shows");
    internal List<string> LoadWarnings { get; } = [];

    internal CustomShowStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The custom library folder is required.");
        this.root = Path.GetFullPath(root);
    }

    internal void EnsureCreated()
    {
        Directory.CreateDirectory(PerformersFolder);
        Directory.CreateDirectory(ShowsFolder);
    }

    internal IReadOnlyList<CustomPerformerProfile> LoadPerformers()
    {
        EnsureCreated();
        List<CustomPerformerProfile> profiles = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(
                     PerformersFolder, "*.json", System.IO.SearchOption.TopDirectoryOnly))
        {
            try
            {
                CustomPerformerProfile profile = ReadJson<CustomPerformerProfile>(path);
                ValidateProfile(profile);
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), profile.Id,
                        StringComparison.OrdinalIgnoreCase) || !ids.Add(profile.Id))
                    throw new InvalidDataException("Duplicate or mismatched performer ID.");
                profiles.Add(profile);
            }
            catch (Exception error)
            {
                LoadWarnings.Add($"{path}: {error.Message}");
            }
        }
        return profiles.OrderBy(profile => profile.ModelName).ToArray();
    }

    internal IReadOnlyList<ModelCard> LoadCards(bool loadImages = true)
    {
        LoadWarnings.Clear();
        Dictionary<string, CustomPerformerProfile> performers =
            LoadPerformers().ToDictionary(profile => profile.Id,
                StringComparer.OrdinalIgnoreCase);
        List<ModelCard> cards = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Directory.EnumerateDirectories(ShowsFolder))
        {
            string manifestPath = Path.Combine(folder, "show.json");
            if (!File.Exists(manifestPath))
                continue;
            try
            {
                (CustomShowManifest show, CustomPerformerProfile performer) =
                    LoadValidatedManifest(folder, performers);
                if (!ids.Add(show.Id))
                    throw new InvalidDataException("Duplicate show ID.");
                cards.Add(ToModelCard(show, performer, folder, manifestPath, loadImages));
            }
            catch (Exception error)
            {
                LoadWarnings.Add($"{manifestPath}: {error.Message}");
            }
        }
        return cards;
    }

    internal CustomShowCheckReport CheckShows(
        IProgress<CustomShowProgress>? progress = null,
        CancellationToken token = default)
    {
        LoadWarnings.Clear();
        Dictionary<string, CustomPerformerProfile> performers =
            LoadPerformers().ToDictionary(profile => profile.Id,
                StringComparer.OrdinalIgnoreCase);
        string[] folders = Directory.EnumerateDirectories(ShowsFolder)
            .Where(folder => File.Exists(Path.Combine(folder, "show.json")))
            .ToArray();
        List<CustomShowCheckIssue> issues = [];
        int valid = 0;
        for (int index = 0; index < folders.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            string folder = folders[index];
            CustomShowManifest? show = null;
            bool canRepair = false;
            try
            {
                (show, _) = LoadValidatedManifest(folder, performers);
                canRepair = true;
                ValidateMediaCompatibility(show, folder);
                valid++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                string folderId = Path.GetFileName(folder);
                string? showId = canRepair ? show?.Id : null;
                issues.Add(new(folder, showId,
                    show?.Title ?? ReadShowTitleBestEffort(folder) ??
                        "Unknown show (" + ShortId(folderId) + ")",
                    error.Message, canRepair));
            }
            progress?.Report(new CustomShowProgress("checking",
                folders.Length == 0 ? 100 : 100d * (index + 1) / folders.Length,
                $"Checked {index + 1:N0}/{folders.Length:N0} custom shows"));
        }
        if (folders.Length == 0)
            progress?.Report(new CustomShowProgress("checking", 100,
                "No custom shows found"));
        return new(folders.Length, valid, issues);
    }

    static (CustomShowManifest Show, CustomPerformerProfile Performer)
        LoadValidatedManifest(string folder,
            IReadOnlyDictionary<string, CustomPerformerProfile> performers)
    {
        CustomShowManifest show = ReadManifest(Path.Combine(folder, "show.json"));
        ValidateManifest(show, folder);
        if (!string.Equals(Path.GetFileName(folder), show.Id,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mismatched show ID.");
        if (!performers.TryGetValue(show.PerformerId,
                out CustomPerformerProfile? performer))
            throw new InvalidDataException("The linked performer profile is missing.");
        ValidateLink(show, performer);
        return (show, performer);
    }

    static string? ReadShowTitleBestEffort(string folder)
    {
        try
        {
            JsonNode? document = JsonNode.Parse(File.ReadAllText(
                Path.Combine(folder, "show.json")));
            return document?["title"]?.GetValue<string>();
        }
        catch { return null; }
    }

    static string ShortId(string id) => id.Length <= 10 ? id : id[..10] + "…";

    internal ModelCard LoadCard(string showId, bool loadImage = true)
    {
        CustomShowManifest show = LoadManifest(showId);
        CustomPerformerProfile performer = LoadPerformer(show.PerformerId);
        string folder = Path.Combine(ShowsFolder, show.Id);
        ValidateLink(show, performer);
        ValidateMediaCompatibility(show, folder);
        return ToModelCard(show, performer, folder,
            Path.Combine(folder, "show.json"), loadImage);
    }

    internal CustomShowManifest LoadManifest(string showId)
    {
        ValidateId(showId, "show");
        string folder = Path.Combine(ShowsFolder, showId);
        CustomShowManifest show = ReadManifest(Path.Combine(folder, "show.json"));
        ValidateManifest(show, folder);
        return show;
    }

    static CustomShowManifest ReadManifest(string path)
    {
        // Keep media produced by the retired experimental option playable.
        // Strict deserialization otherwise rejects its retired provenance fields
        // before the supported legacy algorithm can be selected.
        JsonNode? document = JsonNode.Parse(File.ReadAllText(path));
        if (document is JsonObject root)
        {
            if (root["schemaVersion"]?.GetValue<int>() == 2)
                root["schemaVersion"] = 3;
            if (root["processing"] is JsonObject processing &&
                processing["algorithm"]?.GetValue<string>() ==
                    "rvm-vitmatte-s-temporal")
            {
                processing["algorithm"] = "rvm-vitmatte-s";
                foreach (string property in new[]
                {
                    "temporalTrimapProfile", "temporalTrimapAlgorithmVersion",
                    "temporalTrimapStateResetCount", "temporalTrimapForegroundErosionPx",
                    "temporalTrimapUnknownDilationPx", "temporalTrimapSettings"
                }) processing.Remove(property);
            }
            MigrateRetiredRealtimeRenderer(root);
        }
        return document?.Deserialize<CustomShowManifest>(JsonOptions) ??
            throw new InvalidDataException("The JSON document is empty.");
    }

    internal static bool MigrateRetiredRealtimeRenderer(JsonNode? document)
    {
        bool changed = false;
        if (document is JsonObject queue && queue["jobs"] is JsonArray jobs)
        {
            foreach (JsonObject job in jobs.OfType<JsonObject>())
            {
                changed |= MigrateRetiredRealtimeRenderer(job["manifest"]);
                if (job["clips"] is JsonArray queuedClips)
                    changed |= MigrateRetiredRealtimeClips(queuedClips);
            }
            return changed;
        }
        if (document is not JsonObject root) return false;
        if (root["clips"] is JsonArray clips)
            changed |= MigrateRetiredRealtimeClips(clips);
        if (root["processing"] is JsonObject processing &&
            processing["algorithm"]?.GetValue<string>() == "nvidia-aigs")
        {
            processing["algorithm"] = CustomClipMedia.RvmOnnxMode;
            processing["precisionPolicy"] = "onnx-directml-fp32";
            changed = true;
        }
        return changed;
    }

    static bool MigrateRetiredRealtimeClips(JsonArray clips)
    {
        bool changed = false;
        foreach (JsonObject clip in clips.OfType<JsonObject>())
        {
            if (clip["media"] is JsonObject media &&
                media["mode"]?.GetValue<string>() == "nvidia-aigs")
            {
                media["mode"] = CustomClipMedia.RvmOnnxMode;
                clip["rvmOnnx"] ??= new JsonObject
                {
                    ["model"] = RvmOnnxSupport.MobileNetV3,
                    ["quality"] = "balanced",
                    ["temporal"] = true
                };
                changed = true;
            }
            if (clip.Remove("nvidia")) changed = true;
        }
        return changed;
    }

    internal CustomPerformerProfile LoadPerformer(string performerId)
    {
        ValidateId(performerId, "performer");
        CustomPerformerProfile profile = ReadJson<CustomPerformerProfile>(
            Path.Combine(PerformersFolder, performerId + ".json"));
        ValidateProfile(profile);
        return profile;
    }

    internal void SavePerformer(CustomPerformerProfile profile)
    {
        ValidateProfile(profile);
        EnsureCreated();
        foreach (string path in Directory.EnumerateFiles(
                     PerformersFolder, "*.json", System.IO.SearchOption.TopDirectoryOnly))
        {
            CustomPerformerProfile existing;
            try { existing = ReadJson<CustomPerformerProfile>(path); }
            catch { continue; }
            if (!string.Equals(existing.Id, profile.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.ModelName.Trim(), profile.ModelName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"A custom model named '{profile.ModelName.Trim()}' already exists.");
        }
        WriteJsonAtomic(Path.Combine(PerformersFolder, profile.Id + ".json"), profile);
    }

    internal void SaveManifest(CustomShowManifest show)
    {
        string folder = Path.Combine(ShowsFolder, show.Id);
        ValidateManifest(show, folder);
        WriteJsonAtomic(Path.Combine(folder, "show.json"), show);
    }

    internal void Publish(string stagingFolder, CustomShowManifest show)
    {
        ValidateId(show.Id, "show");
        string destination = Path.Combine(ShowsFolder, show.Id);
        if (Directory.Exists(destination))
            throw new IOException("A show with this ID already exists.");
        ValidateManifest(show, stagingFolder);
        WriteJsonAtomic(Path.Combine(stagingFolder, "show.json"), show);
        EnsureCreated();
        Directory.Move(stagingFolder, destination);
    }

    internal void Replace(string stagingFolder, CustomShowManifest show)
    {
        ValidateId(show.Id, "show");
        string destination = Path.Combine(ShowsFolder, show.Id);
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException("The custom show being reprocessed no longer exists.");
        ValidateManifest(show, stagingFolder);
        WriteJsonAtomic(Path.Combine(stagingFolder, "show.json"), show);
        string backupRoot = Path.Combine(root, ".replaced");
        Directory.CreateDirectory(backupRoot);
        string backup = Path.Combine(backupRoot,
            show.Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(destination, backup);
        try
        {
            Directory.Move(stagingFolder, destination);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw;
        }
        try { Directory.Delete(backup, true); }
        catch { }
    }

    internal void DeleteShow(string showId)
    {
        ValidateId(showId, "show");
        string folder = Path.Combine(ShowsFolder, showId);
        if (Directory.Exists(folder))
            FileSystem.DeleteDirectory(folder, UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
    }

    internal void DeleteCheckedShowFolder(string folder)
    {
        string fullFolder = Path.GetFullPath(folder).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string showsRoot = Path.GetFullPath(ShowsFolder).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullFolder.StartsWith(showsRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(fullFolder),
                Path.TrimEndingDirectorySeparator(ShowsFolder),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Only a direct child of the custom-show library can be deleted.");
        if (Directory.Exists(fullFolder))
            FileSystem.DeleteDirectory(fullFolder, UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
    }

    internal string? DeleteClip(string showId, string clipId,
        bool recycleMedia = true)
    {
        ValidateId(showId, "show");
        ValidateId(clipId, "clip");
        CustomShowManifest show = LoadManifest(showId);
        CustomShowClip clip = show.Clips.FirstOrDefault(value =>
            string.Equals(value.Id, clipId, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException("The custom clip no longer exists.");
        if (!clip.Included || clip.Media == null)
            throw new InvalidDataException("The custom clip is not playable.");
        if (show.Clips.Count(value => value.Included) <= 1)
            throw new InvalidOperationException(
                "The final clip must be removed by deleting the custom show.");

        string showFolder = Path.Combine(ShowsFolder, show.Id);
        string foreground = ResolveRelative(showFolder, clip.Media.Foreground);
        string? alpha = CustomClipMedia.IsRealtimeMode(clip.Media.Mode)
            ? null : ResolveRelative(showFolder, clip.Media.Alpha!);
        clip.Included = false;
        clip.Media = null;
        if (show.Processing != null)
            show.Processing.Clips = show.Processing.Clips.Where(value =>
                !string.Equals(value.ClipId, clipId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
        SaveManifest(show);

        try
        {
            DeleteClipMedia(showFolder, clipId, foreground, alpha,
                recycleMedia);
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    static void DeleteClipMedia(string showFolder, string clipId,
        string foreground, string? alpha, bool recycle)
    {
        string? foregroundFolder = Path.GetDirectoryName(foreground);
        string? alphaFolder = alpha == null ? foregroundFolder :
            Path.GetDirectoryName(alpha);
        string clipsFolder = Path.GetFullPath(Path.Combine(showFolder, "clips"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (foregroundFolder != null && string.Equals(foregroundFolder,
                alphaFolder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(foregroundFolder), clipId,
                StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFullPath(foregroundFolder) + Path.DirectorySeparatorChar)
                .StartsWith(clipsFolder, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(foregroundFolder))
        {
            if (recycle)
                FileSystem.DeleteDirectory(foregroundFolder,
                    UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                Directory.Delete(foregroundFolder, true);
            return;
        }

        IEnumerable<string> paths = alpha == null ? [foreground] :
            [foreground, alpha, CleanedAlphaPath(alpha)];
        foreach (string path in paths
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            if (recycle)
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }
    }

    internal static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ??
        throw new InvalidDataException("The JSON document is empty.");

    internal static void ValidateProfile(CustomPerformerProfile profile)
    {
        if (profile.SchemaVersion != 1) throw new InvalidDataException("Unsupported performer schema version.");
        ValidateId(profile.Id, "performer");
        if (string.IsNullOrWhiteSpace(profile.ModelName)) throw new InvalidDataException("Model name is required.");
        if (!string.IsNullOrWhiteSpace(profile.Gender) &&
            !GenderValues.Contains(profile.Gender))
            throw new InvalidDataException("Unknown model gender value.");
        ValidateMeasurement(profile.HeightCm, 80, 250, "height");
        ValidateMeasurement(profile.BustCm, 30, 250, "bust");
        ValidateMeasurement(profile.WaistCm, 30, 250, "waist");
        ValidateMeasurement(profile.HipsCm, 30, 250, "hips");
        if (profile.BirthDate > DateOnly.FromDateTime(DateTime.Today))
            throw new InvalidDataException("Birth date cannot be in the future.");
    }

    internal static void ValidateManifest(CustomShowManifest show, string folder)
    {
        if (show.SchemaVersion != 3) throw new InvalidDataException(
            "This custom show uses the old shared-media format and must be reprocessed.");
        if (show.Source == null || show.Media == null || show.Tags == null ||
            show.ClipTypes == null || show.Clips == null)
            throw new InvalidDataException("Required show metadata is missing.");
        ValidateId(show.Id, "show");
        ValidateId(show.PerformerId, "performer");
        if (string.IsNullOrWhiteSpace(show.Title)) throw new InvalidDataException("Show title is required.");
        if (show.ReleaseDate == default) throw new InvalidDataException("Release date is required.");
        if (show.CreatedUtc == default || show.CreatedUtc.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("createdUtc must be an ISO-8601 UTC value.");
        if (show.OfficialRating is < 0 or > 5) throw new InvalidDataException("Official rating must be 0–5.");
        if (show.AgeAtReleaseOverride is < 18 or > 120) throw new InvalidDataException("Age at release must be 18–120.");
        if (!GenderValues.Contains(show.Gender))
            throw new InvalidDataException("Unknown gender value.");
        if (!HotnessValues.Contains(show.Hotness)) throw new InvalidDataException("Unknown hotness value.");
        if (show.PerformerCount < 1) throw new InvalidDataException("Performer count must be at least one.");
        if (show.ClipTypes.Length == 0 || show.ClipTypes.Any(type => !ClipTypeValues.Contains(type)))
            throw new InvalidDataException("At least one valid clip type is required.");
        if (show.Media.CoverTitleColor.Length != 7 ||
            show.Media.CoverTitleColor[0] != '#' ||
            !int.TryParse(show.Media.CoverTitleColor.AsSpan(1),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("Cover title colour must be #RRGGBB.");
        if (string.IsNullOrWhiteSpace(show.Media.CoverModelFont) ||
            show.Media.CoverModelFont.Length > 100 ||
            string.IsNullOrWhiteSpace(show.Media.CoverTitleFont) ||
            show.Media.CoverTitleFont.Length > 100)
            throw new InvalidDataException("Cover font names are invalid.");
        ValidateMediaFields(show.Media.Width, show.Media.Height,
            show.Media.FrameRate, show.Media.DurationMs, "Show media");
        ValidateVirtualGreenScreen(show.VirtualGreenScreen);
        ValidateClips(show.Clips, show.Media.DurationMs);
        ValidateClipDetection(show.ClipDetection);
        ValidateProcessing(show.Processing, show.Clips);
        foreach (CustomShowClip clip in show.Clips)
        {
            ValidateVirtualGreenScreen(clip.VirtualGreenScreen);
            if (clip.Included && clip.Media == null)
                throw new InvalidDataException("Every included clip needs processed media.");
            ValidateClipSource(clip, folder);
            if (clip.Media is not CustomClipMedia media) continue;
            if (media.Mode is not (CustomClipMedia.PairedAlphaMode or
                    CustomClipMedia.RvmOnnxMode))
                throw new InvalidDataException("Unknown custom clip media mode.");
            bool rvmOnnx = media.Mode == CustomClipMedia.RvmOnnxMode;
            if (rvmOnnx != (clip.RvmOnnx != null))
                throw new InvalidDataException(
                    "RVM ONNX settings are required only for RVM ONNX clips.");
            if (rvmOnnx)
            {
                if (!string.IsNullOrWhiteSpace(media.Alpha))
                    throw new InvalidDataException(
                        "Real-time foreground clip media must not contain an alpha file.");
                ValidateRvmOnnxSettings(clip.RvmOnnx!);
            }
            else if (string.IsNullOrWhiteSpace(media.Alpha))
                throw new InvalidDataException(
                    "Paired-alpha clip media requires an alpha file.");
            ValidateMediaFields(media.Width, media.Height, media.FrameRate,
                media.DurationMs, "Clip media");
            long playbackEnd = PlaybackEnd(media);
            if (media.PlaybackStartMs < 0 || playbackEnd <= media.PlaybackStartMs ||
                playbackEnd > media.DurationMs)
                throw new InvalidDataException(
                    "Clip playback trim must be inside the processed media duration.");
        }
        IEnumerable<string> paths = show.Clips.Where(clip => clip.Included)
            .SelectMany(clip => RequiredMediaPaths(clip.Media!))
            .Append(show.Media.Cover)
            .Concat(string.IsNullOrWhiteSpace(show.Media.CoverSource)
                ? [] : [show.Media.CoverSource]);
        foreach (string relative in paths)
        {
            string mediaPath = ResolveRelative(folder, relative);
            if (!File.Exists(mediaPath)) throw new FileNotFoundException("Required custom-show media is missing.", mediaPath);
        }
        if (show.Source.Mode is not "copy" and not "reference")
            throw new InvalidDataException("Source mode must be copy or reference.");
        if (show.Source.Mode == "copy" && !File.Exists(ResolveRelative(folder, show.Source.Path)))
            throw new FileNotFoundException("The copied source video is missing.");
        if (show.Source.Mode == "reference" && string.IsNullOrWhiteSpace(show.Source.Path))
            throw new InvalidDataException("The referenced source path is required.");
    }

    static IEnumerable<string> RequiredMediaPaths(CustomClipMedia media)
    {
        yield return media.Foreground;
        if (!CustomClipMedia.IsRealtimeMode(media.Mode))
            yield return media.Alpha!;
    }

    internal static void ValidateRvmOnnxSettings(CustomRvmOnnxSettings settings)
    {
        if (settings.Model is not ("mobilenetv3" or "resnet50"))
            throw new InvalidDataException(
                "RVM ONNX model must be MobileNetV3 or ResNet50.");
        if (settings.Quality is not ("fast" or "balanced" or "quality" or
                RvmOnnxSupport.FullResolution))
            throw new InvalidDataException(
                "RVM ONNX quality must be fast, balanced, quality, or " +
                "full-resolution refinement.");
    }

    static void ValidateClipDetection(CustomShowClipDetection? detection)
    {
        if (detection == null) return;
        if (!ClipDetectionMethods.Contains(detection.Method))
            throw new InvalidDataException("Unknown clip-detection method.");
        if (detection.ToolRevision is string revision &&
            (string.IsNullOrWhiteSpace(revision) || revision.Length > 200))
            throw new InvalidDataException("Invalid clip-detection tool revision.");
        if (detection.SensitivityDataFormat is string format &&
            (format.Length > 100 || string.IsNullOrWhiteSpace(format)))
            throw new InvalidDataException("Invalid clip-detection sensitivity-data format.");
        if (detection.SensitivityData is string data && data.Length > 50_000_000)
            throw new InvalidDataException("Clip-detection sensitivity data is too large.");
        if (detection.DetectionFrameRate is <= 0 or > 1000)
            throw new InvalidDataException("Invalid clip-detection frame rate.");
        if (detection.OverlapFrames is < 0 or > 10_000 ||
            detection.SensitivityPercent is < 1 or > 100 ||
            detection.TransitionBufferMs is < 0 or > 30_000 ||
            detection.MinimumClipMs is < 0 or > 3_600_000)
            throw new InvalidDataException("Invalid clip-detection parameters.");
    }

    static void ValidateProcessing(CustomShowProcessing? processing,
        CustomShowClip[] clips)
    {
        if (processing == null) return;
        if (!ProcessingAlgorithms.Contains(processing.Algorithm))
            throw new InvalidDataException("Unknown custom-show processing algorithm.");
        bool sam2Matting = processing.Algorithm == Sam2MattingSupport.Algorithm;
        bool rvmOnnx = processing.Algorithm == CustomClipMedia.RvmOnnxMode;
        if (sam2Matting)
        {
            if (processing.Tracker == null ||
                !Sam2MattingSupport.Trackers.Contains(processing.Tracker) ||
                !Sam2MattingSupport.IsSupportedOptionContract(
                    processing.ProcessingOptionVersion, processing.Tracker,
                    processing.PromptMode) ||
                processing.EnvironmentSpecVersion !=
                    Sam2MattingSupport.EnvironmentVersion ||
                processing.SourceRevision != Sam2MattingSupport.SourceRevision ||
                processing.CheckpointRevision !=
                    Sam2MattingSupport.CheckpointRevision ||
                !string.Equals(processing.CheckpointSha256,
                    Sam2MattingSupport.CheckpointSha256(processing.Tracker),
                    StringComparison.OrdinalIgnoreCase) ||
                processing.AttentionPolicy != "pytorch-sdpa" ||
                processing.PrecisionPolicy != "bf16-autocast" ||
                processing.EncoderPolicy != "quickplayer-h264-aac" ||
                processing.AlphaEncodingPolicy != Sam2MattingSupport.AlphaEncodingPolicy ||
                processing.ScenePlanVersion != Sam2MattingSupport.ScenePlanVersion)
                throw new InvalidDataException(
                    "The SAM2Matting processing contract is invalid or unsupported.");
            if ((processing.ForegroundConcepts?.Length ?? 0) != 0)
                throw new InvalidDataException(
                    "SAM2.1 trackers do not accept text concepts.");
            if (processing.Scenes == null)
                throw new InvalidDataException(
                    "SAM2Matting scene metadata is missing.");
            Sam2MattingScenePlanner.Validate(processing.Scenes, clips);
        }
        else if (processing.ProcessingOptionVersion != 0 ||
            processing.Tracker != null || processing.PromptMode != null ||
            (processing.ForegroundConcepts?.Length ?? 0) != 0 ||
            processing.EnvironmentSpecVersion != null ||
            processing.SourceRevision != null ||
            processing.CheckpointRevision != null ||
            processing.CheckpointSha256 != null ||
            processing.AttentionPolicy != null ||
            processing.EncoderPolicy != null ||
            processing.AlphaEncodingPolicy != null ||
            processing.ScenePlanVersion != null ||
            (processing.Scenes?.Length ?? 0) != 0)
            throw new InvalidDataException(
                "SAM2Matting metadata is not valid for this algorithm.");
        if (!MattingDetailValues.Contains(processing.MattingDetailPx))
            throw new InvalidDataException("Unknown matting-detail resolution.");
        if (processing.VitMatteInferenceDetailPx is int vitMatteDetail &&
            (processing.Algorithm is not ("vitmatte-s" or "vitmatte-b" or
                "rvm-vitmatte-s" or "rvm-vitmatte-b") || vitMatteDetail is not (0 or 512 or 768 or 1024)))
            throw new InvalidDataException("Unknown ViTMatte inference-detail resolution.");
        if (!BatchSizeValues.Contains(processing.BatchSize))
            throw new InvalidDataException("Unknown processing batch size.");
        bool sam2MattingRvmInitializer = sam2Matting &&
            processing.PromptMode == "rvm-initial-mask";
        if (sam2Matting && sam2MattingRvmInitializer !=
                (processing.RvmInitializerAlphaThresholdPercent is int) ||
            processing.RvmInitializerAlphaThresholdPercent is int initializerThreshold &&
            (processing.Algorithm is not ("rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b") &&
                processing.MaskEngine != "rvm-sam2" && !sam2MattingRvmInitializer ||
                initializerThreshold is < 10 or > 90))
            throw new InvalidDataException("Invalid RVM initializer alpha threshold.");
        bool propCompatible = PropSegmenterCompatible(
            processing.Algorithm, sam2MattingRvmInitializer,
            processing.MaskEngine);
        bool hasProp = processing.PropSegmenterModelId != null;
        if (hasProp != (processing.PropSegmenterCheckpointSha256 != null) ||
            hasProp != (processing.PropSegmenterConfidenceThreshold != null) ||
            hasProp != (processing.PropSegmenterProximityRadiusAt512 != null) ||
            hasProp && (!propCompatible ||
                !PropSegmenterPackage.ValidModelId(processing.PropSegmenterModelId!) ||
                processing.PropSegmenterCheckpointSha256!.Length != 64 ||
                processing.PropSegmenterCheckpointSha256.Any(value => !Uri.IsHexDigit(value)) ||
                processing.PropSegmenterConfidenceThreshold is < .1 or > .9 ||
                processing.PropSegmenterProximityRadiusAt512 is < 1 or > 128))
            throw new InvalidDataException("Invalid prop-segmenter processing contract.");
        bool hasExtendedPropIdentity = processing.PropSegmenterManifestSha256 != null ||
            processing.PropSegmenterManifestSchemaVersion != null ||
            processing.PropSegmenterArchitecture != null ||
            processing.PropSegmenterPostprocessingContract != null;
        if (hasExtendedPropIdentity && (!hasProp ||
            processing.PropSegmenterManifestSha256?.Length != 64 ||
            processing.PropSegmenterManifestSha256.Any(value => !Uri.IsHexDigit(value)) ||
            processing.PropSegmenterManifestSchemaVersion is not (1 or 2) ||
            string.IsNullOrWhiteSpace(processing.PropSegmenterArchitecture) ||
            string.IsNullOrWhiteSpace(processing.PropSegmenterPostprocessingContract)))
            throw new InvalidDataException("Invalid extended prop-segmenter identity.");
        if (processing.PropSegmenterManifestSchemaVersion == 2 &&
            processing.Algorithm is "rvm-vitmatte-s" or "rvm-vitmatte-b")
            throw new InvalidDataException(
                "RVM-conditioned v2 augmentation does not alter RVM-ViTMatte temporal masks.");
        if (processing.PropSegmenterManifestSchemaVersion == 2 &&
            processing.PropSegmenterEveryFrame)
            throw new InvalidDataException(
                "RVM-conditioned v2 uses its pinned sparse discovery interval, not per-frame discovery.");
        if (processing.RvmMatAnyoneMaskRefresh &&
            processing.Algorithm != "rvm-matanyone2")
            throw new InvalidDataException(
                "RVM mask refresh is only valid for RVM-MatAnyone processing.");
        bool propEveryFrameCompatible = processing.Algorithm is
            "matanyone2" or "rvm-matanyone2" or
            "rvm-vitmatte-s" or "rvm-vitmatte-b";
        if (processing.PropSegmenterEveryFrame &&
            (!hasProp || !propEveryFrameCompatible ||
                processing.Algorithm == "rvm-matanyone2" &&
                    !processing.RvmMatAnyoneMaskRefresh))
            throw new InvalidDataException(
                "Per-frame prop segmentation requires a compatible pipeline and an enabled trained prop model.");
        if (processing.DebugPropContribution &&
            !processing.PropSegmenterEveryFrame)
            throw new InvalidDataException(
                "Prop contribution diagnostics require per-frame prop segmentation.");
        if (processing.RvmMatAnyoneRefreshStrengthPercent is < 25 or > 100 ||
            processing.Algorithm != "rvm-matanyone2" &&
                processing.RvmMatAnyoneRefreshStrengthPercent != 100)
            throw new InvalidDataException("Invalid RVM mask refresh strength.");
        bool matAnyone = processing.Algorithm is "matanyone2" or "rvm-matanyone2";
        if (processing.MatAnyoneMaxMemoryFrames is < 2 or > 30 ||
            processing.MatAnyoneUseLongTermMemory &&
                processing.MatAnyoneMaxMemoryFrames is <
                    CustomShowConfiguration.MatAnyoneLongTermMinimumMemoryFrames or >
                    CustomShowConfiguration.MatAnyoneLongTermMaximumMemoryFrames ||
            !matAnyone && (processing.MatAnyoneMaxMemoryFrames != 5 ||
                processing.MatAnyoneUseLongTermMemory))
            throw new InvalidDataException("Invalid MatAnyone memory configuration.");
        if (processing.TemporalAlphaCleanupWindowFrames is < 1 or > 30 ||
            processing.TemporalAlphaCleanupStrengthPercent is < 25 or > 100 ||
            processing.TemporalAlphaTrackingStrengthPercent is < 0 or > 100 ||
            processing.TemporalAlphaCleanupAlphaThreshold is < 0 or > 255)
            throw new InvalidDataException(
                "Invalid temporal alpha-cleanup window, strength, tracking strength, " +
                "or alpha threshold.");
        if (processing.AutoAcceptedAlphaThreshold is int acceptedThreshold &&
            acceptedThreshold is < 0 or > 255)
            throw new InvalidDataException("Invalid automatically accepted alpha threshold.");
        bool trackedMasks = processing.Algorithm is
            "vitmatte-s" or "vitmatte-b" or "videomama";
        string? maskEngine = trackedMasks ? processing.MaskEngine ?? "sam2" : null;
        if (trackedMasks && maskEngine is not
            ("sam2" or "rvm" or "edgetam" or "rvm-sam2"))
            throw new InvalidDataException("Unknown mask-generation algorithm.");
        if (!trackedMasks && processing.MaskEngine != null)
            throw new InvalidDataException("Mask-engine metadata is not valid for this algorithm.");
        bool needsSam2 = processing.Algorithm == "matanyone2" ||
            trackedMasks && maskEngine != "rvm";
        bool allowsInitialMask = processing.Algorithm is
            "matanyone2" or "rvm-matanyone2" ||
            trackedMasks && maskEngine == "rvm-sam2";
        if (needsSam2 && (processing.Sam2Model == null ||
            !Sam2Models.Contains(processing.Sam2Model)))
            throw new InvalidDataException("A valid SAM2 model is required for this algorithm.");
        if (!needsSam2 && processing.Sam2Model != null)
            throw new InvalidDataException("SAM2 model metadata is not valid for this algorithm.");
        string expectedExecutionPolicy = sam2Matting ? "eager" :
            rvmOnnx ? "realtime-playback" : "auto";
        if (processing.ExecutionPolicy != expectedExecutionPolicy ||
            string.IsNullOrWhiteSpace(processing.PrecisionPolicy) ||
            rvmOnnx && processing.PrecisionPolicy != "onnx-directml-fp32")
            throw new InvalidDataException("Invalid processing execution policy.");
        if (processing.EffectiveBatchSize is < 1 or > 24)
            throw new InvalidDataException("Invalid effective processing batch size.");
        if (processing.PipelineDepth is < 1 or > 3)
            throw new InvalidDataException("Invalid processing pipeline depth.");
        if (processing.ResolvedExecutionMode is string mode &&
            mode is not ("eager" or "compiled" or "eager-fallback" or
                "eager-oom-fallback" or "eager-bf16-sdpa" or
                "eager-bf16-sdpa-bounded" or "realtime-playback" or
                "metadata-only-reuse"))
            throw new InvalidDataException("Invalid resolved processing execution mode.");
        if (processing.Encoder is string encoder &&
            encoder is not ("h264_nvenc" or "libx264"))
            throw new InvalidDataException("Invalid processing encoder.");
        if (processing.EncoderPreset is string encoderPreset &&
            encoderPreset is not ("p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7" or "slow/medium" or "medium"))
            throw new InvalidDataException("Invalid processing encoder preset.");
        if (processing.RecurrentRefinementSteps is < 0 or > 100)
            throw new InvalidDataException("Invalid recurrent-refinement step count.");
        if (processing.ProcessedUtc == default ||
            processing.ProcessedUtc.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("processedUtc must be an ISO-8601 UTC value.");
        if (processing.Clips == null)
            throw new InvalidDataException("Processing clip metadata is missing.");
        if (processing.ToolRevisions == null || processing.ToolRevisions.Any(pair =>
            string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
            pair.Key.Length > 80 || pair.Value.Length > 200))
            throw new InvalidDataException("Invalid processing tool revision metadata.");

        Dictionary<string, CustomShowClip> included = clips.Where(value => value.Included)
            .ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (CustomClipProcessing item in processing.Clips)
        {
            ValidateId(item.ClipId, "processing clip");
            if (!seen.Add(item.ClipId) || !included.TryGetValue(item.ClipId, out CustomShowClip? clip))
                throw new InvalidDataException("Processing metadata must reference each included clip at most once.");
            if (item.InitialMaskFrameMs is long frame &&
                (frame < clip.StartMs || frame >= clip.EndMs))
                throw new InvalidDataException("Initial mask frame lies outside its clip.");
            if (!allowsInitialMask && item.InitialMaskFrameMs != null ||
                !needsSam2 && item.Sam2MaskTracking)
                throw new InvalidDataException("Mask metadata is not valid for this algorithm.");
            if (item.Sam2MaskTracking && maskEngine is not
                ("sam2" or "rvm-sam2"))
                throw new InvalidDataException("SAM2 tracking metadata conflicts with the mask engine.");
        }
        if (seen.Count != included.Count)
            throw new InvalidDataException("Processing metadata is required for every included clip.");
    }

    static void ValidateMediaFields(int width, int height, string frameRate,
        long durationMs, string description)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0 ||
            durationMs <= 0 || !TryFrameRate(frameRate, out _))
            throw new InvalidDataException($"{description} dimensions, frame rate, or duration are invalid.");
    }

    internal static void ValidateClips(CustomShowClip[] clips, long durationMs)
    {
        if (clips.Length == 0) throw new InvalidDataException(
            "A custom show must contain at least one clip.");
        if (!clips.Any(clip => clip.Included))
            throw new InvalidDataException("At least one segment must be included as a clip.");
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        long expectedStart = 0;
        foreach (CustomShowClip clip in clips)
        {
            ValidateId(clip.Id, "clip");
            if (!ids.Add(clip.Id)) throw new InvalidDataException("Clip IDs must be unique.");
            if (clip.StartMs != expectedStart || clip.EndMs <= clip.StartMs || clip.EndMs > durationMs)
                throw new InvalidDataException("Clip ranges must be ordered, contiguous, and inside the video.");
            if (!HotnessValues.Contains(clip.Hotness)) throw new InvalidDataException("Unknown clip hotness value.");
            if (clip.AlphaThreshold is < 0 or > 255)
                throw new InvalidDataException("Clip alpha threshold must be 0–255.");
            if (!float.IsFinite(clip.EdgeChokePixels) ||
                clip.EdgeChokePixels is < 0 or > 4)
                throw new InvalidDataException(
                    "Clip edge cleanup must be 0–4 pixels.");
            if (clip.DetectionLabels == null ||
                clip.DetectionLabels.Any(label =>
                    !IsValidDetectionLabel(label)) ||
                clip.DetectionLabels.Distinct(StringComparer.Ordinal).Count() !=
                    clip.DetectionLabels.Length)
                throw new InvalidDataException("Invalid clip-detection labels.");
            if (clip.ClipTypes == null || clip.ClipTypes.Length == 0 ||
                clip.ClipTypes.Any(type => !ClipTypeValues.Contains(type)))
                throw new InvalidDataException("Every clip needs at least one valid clip type.");
            expectedStart = clip.EndMs;
        }
        if (expectedStart != durationMs)
            throw new InvalidDataException("Clip ranges must cover the complete video.");
    }

    internal static string ResolveRelative(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Media paths must be relative.");
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A media path leaves its show folder.");
        return full;
    }

    static void ValidateVirtualGreenScreen(CustomVirtualGreenScreen? settings)
    {
        if (settings == null) return;
        if (!VirtualGreenScreenMath.ColorDomains.Contains(
                settings.ColorDomain ?? "", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Virtual green-screen colour domain must be ycbcr, rgb, hsv, or oklab.");
        if (settings.Colors == null)
            throw new InvalidDataException(
                "Virtual green-screen colours cannot be null.");
        if (settings.SmallPatchSize is < 0 or >
            CustomVirtualGreenScreen.MaximumSmallPatchSize)
            throw new InvalidDataException(
                $"Virtual green-screen small-patch size must be 0–" +
                $"{CustomVirtualGreenScreen.MaximumSmallPatchSize} pixels.");
        if (settings.MinimumTransparentAreaRadius is < 0 or >
            CustomVirtualGreenScreen.MaximumSmallPatchSize)
            throw new InvalidDataException(
                $"Virtual green-screen minimum transparent-area radius must " +
                $"be 0–{CustomVirtualGreenScreen.MaximumSmallPatchSize} pixels.");
        if (settings.KeyFeather is < 0 or > 100 ||
            settings.LockedFeather is < 0 or > 100)
            throw new InvalidDataException(
                "Virtual green-screen key and locked feather values must be 0–100.");
        if (settings.KeyOverLockedThreshold is < 0 or > 100)
            throw new InvalidDataException(
                "Virtual green-screen key-over-lock threshold must be 0–100.");
        foreach (CustomVirtualGreenScreenColor color in settings.Colors)
            if (!VirtualGreenScreenMath.IsColor(color.Color) ||
                color.Tolerance is < 0 or > 100 || color.Feather is < 0 or > 100)
                throw new InvalidDataException(
                    "Virtual green-screen colours require #RRGGBB and 0–100 " +
                    "tolerance and feather values.");
    }

    internal static string CleanedAlphaPath(string alphaPath)
    {
        string full = Path.GetFullPath(alphaPath);
        string stem = Path.GetFileNameWithoutExtension(full);
        if (stem.EndsWith("_cleaned", StringComparison.OrdinalIgnoreCase))
            return full;
        return Path.Combine(Path.GetDirectoryName(full)!,
            stem + "_cleaned" + Path.GetExtension(full));
    }

    internal static string PreferredAlphaPath(string alphaPath)
    {
        string original = Path.GetFullPath(alphaPath);
        string cleaned = CleanedAlphaPath(original);
        return !string.Equals(cleaned, original, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(cleaned) ? cleaned : original;
    }

    internal static string ResolvePlaybackAlpha(string folder, string relative) =>
        PreferredAlphaPath(ResolveRelative(folder, relative));

    internal static int AgeAt(DateOnly birthDate, DateOnly releaseDate)
    {
        int age = releaseDate.Year - birthDate.Year;
        if (releaseDate < birthDate.AddYears(age)) age--;
        return Math.Max(0, age);
    }

    internal static void ValidateLink(CustomShowManifest show,
        CustomPerformerProfile performer)
    {
        if (performer.BirthDate is DateOnly birth &&
            AgeAt(birth, show.ReleaseDate) is < 18 or > 120)
            throw new InvalidDataException(
                "The calculated model age at release must be 18–120.");
    }

    internal static decimal? CmToInches(decimal? centimetres) =>
        centimetres is null ? null : decimal.Round(centimetres.Value / 2.54m, 1);

    internal static string CmToLegacyHeight(decimal? centimetres)
    {
        if (centimetres is null) return "";
        int inches = (int)Math.Round(centimetres.Value / 2.54m);
        return $"{inches / 12}.{inches % 12}";
    }

    internal static bool TryFrameRate(string value, out double rate)
    {
        rate = 0;
        string[] parts = value.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out double numerator) ||
            !double.TryParse(parts[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out double denominator) || denominator <= 0)
            return false;
        rate = numerator / denominator;
        return double.IsFinite(rate) && rate > 0 && rate <= 240;
    }

    static void ValidateMediaCompatibility(CustomShowManifest show, string folder)
    {
        foreach (CustomClipMedia media in show.Clips.Where(clip => clip.Included)
                     .Select(clip => clip.Media!))
            ValidateMediaCompatibility(folder, media,
                media.Width, media.Height, media.FrameRate, media.DurationMs);
    }

    static void ValidateMediaCompatibility(string folder, CustomClipMedia media,
        int width, int height, string frameRate, long durationMs)
    {
        using FfmpegCpuDecoder rgb = new(ResolveRelative(folder, media.Foreground));
        TryFrameRate(frameRate, out double declaredRate);
        if (rgb.Width != width || rgb.Height != height ||
            Math.Abs(1 / rgb.FrameDuration - declaredRate) > .02 ||
            Math.Abs(rgb.Duration * 1000 - durationMs) > Math.Max(100, rgb.FrameDuration * 2000))
            throw new InvalidDataException(
                "Foreground and manifest media properties do not match.");
        if (!rgb.DecodeNext(out long rgbTime))
            throw new InvalidDataException("Foreground contains no frames.");
        rgb.ValidateGpuFrame();
        if (CustomClipMedia.IsRealtimeMode(media.Mode))
            return;
        using FfmpegCpuDecoder alpha = new(ResolveRelative(folder, media.Alpha!));
        if (rgb.Width != alpha.Width || rgb.Height != alpha.Height ||
            Math.Abs(rgb.FrameDuration - alpha.FrameDuration) > .0005 ||
            Math.Abs(rgb.Duration - alpha.Duration) > Math.Max(.05, rgb.FrameDuration * 2) ||
            !alpha.DecodeNext(out long alphaTime))
            throw new InvalidDataException(
                "Foreground and alpha media properties do not match.");
        alpha.GrayPlane();
        if (Math.Abs(rgbTime - alphaTime) >
            Math.Max(10_000, (long)Math.Round(rgb.FrameDuration * 5_000_000)))
            throw new InvalidDataException("Foreground and alpha timestamps do not match.");
    }

    static ModelCard ToModelCard(CustomShowManifest show,
        CustomPerformerProfile performer, string folder, string manifestPath,
        bool loadImage)
    {
        ModelCard? istripperModel = FindIstripperModel(performer.IstripperModelId);
        string cover = ResolveRelative(folder, show.Media.Cover);
        CustomClipMedia[] playableMedia = show.Clips.Where(clip => clip.Included)
            .Select(clip => clip.Media!).ToArray();
        DateOnly? birthDate = istripperModel?.birthdate is DateTime nativeBirth &&
            nativeBirth != default ? DateOnly.FromDateTime(nativeBirth) : performer.BirthDate;
        int age = birthDate is DateOnly birth
            ? AgeAt(birth, show.ReleaseDate)
            : show.AgeAtReleaseOverride ?? 0;
        long mediaSize = playableMedia.Sum(media =>
            new FileInfo(ResolveRelative(folder, media.Foreground)).Length +
            (CustomClipMedia.IsRealtimeMode(media.Mode) ? 0 :
                new FileInfo(ResolveRelative(folder, media.Alpha!)).Length));
        long playableDuration = playableMedia.Sum(PlaybackDuration);
        ModelCard card = new()
        {
            name = "custom:" + show.Id,
            customShowId = show.Id,
            customPerformerId = show.PerformerId,
            customManifestPath = manifestPath,
            collection = CollectionType.Custom,
            modelName = istripperModel?.modelName?.Trim() ?? performer.ModelName.Trim(),
            modelId = string.IsNullOrWhiteSpace(performer.IstripperModelId)
                ? show.PerformerId : performer.IstripperModelId,
            outfit = show.Title.Trim(),
            description = show.Description,
            tags = show.Tags,
            datePurchased = show.CreatedUtc.ToLocalTime(),
            dateReleased = show.ReleaseDate.ToDateTime(TimeOnly.MinValue),
            dateShow = show.ShowDate?.ToDateTime(TimeOnly.MinValue) ?? default,
            birthdate = birthDate?.ToDateTime(TimeOnly.MinValue),
            modelAge = age,
            rating = show.OfficialRating is decimal rating ? rating + 5 : 0,
            hotnessLevel = show.Hotness,
            gender = show.Gender,
            exclusive = show.Exclusive,
            numgirls = show.PerformerCount,
            hair = istripperModel?.hair ?? performer.Hair,
            ethnicity = istripperModel?.ethnicity ?? performer.Ethnicity,
            city = istripperModel?.city ?? performer.City,
            country = istripperModel?.country ?? performer.Country,
            bust = istripperModel?.bust ?? CmToInches(performer.BustCm),
            waist = istripperModel?.waist ?? CmToInches(performer.WaistCm),
            hips = istripperModel?.hips ?? CmToInches(performer.HipsCm),
            height = istripperModel?.height ?? CmToLegacyHeight(performer.HeightCm),
            imagefile = cover,
            resolution = ResolutionFor(show.Media.Height),
            bestResolution = ResolutionFor(show.Media.Height),
            frameCount = TryFrameRate(show.Media.FrameRate, out double fps)
                ? (int)Math.Min(int.MaxValue, Math.Round(fps * playableDuration / 1000d)) : 0,
            folderSize = mediaSize,
            inCollection = true,
            cardDownloaded = true,
            cardDownloaded2 = true,
            cardEnabled = true
        };
        if (loadImage)
        {
            using Image source = Image.FromFile(cover);
            card.image = new Bitmap(source);
        }
        card.clips = show.Clips.Where(clip => clip.Included).Select((clip, index) =>
            {
                CustomClipMedia media = clip.Media!;
                string clipForeground = ResolveRelative(folder, media.Foreground);
                string? clipAlpha = CustomClipMedia.IsRealtimeMode(media.Mode)
                    ? null : ResolvePlaybackAlpha(folder, media.Alpha!);
                long size = checked(new FileInfo(clipForeground).Length +
                    (clipAlpha == null ? 0 : new FileInfo(clipAlpha).Length));
                string clipName = playableMedia.Length == 1
                    ? card.name : $"{card.name}:{clip.Id}";
                return CreateClip(clipName, clipForeground, clipAlpha,
                    size, index + 1, clip.Hotness, clip.ClipTypes,
                    clip.AlphaThreshold, clip.EdgeChokePixels,
                    media.PlaybackStartMs,
                    PlaybackEnd(media), VirtualGreenScreenMath.Resolve(
                        show.VirtualGreenScreen, clip.VirtualGreenScreen),
                    media.Mode, clip.RvmOnnx);
            }).ToList();
        return card;
    }

    internal static long PlaybackEnd(CustomClipMedia media) =>
        media.PlaybackEndMs ?? media.DurationMs;

    internal static long PlaybackDuration(CustomClipMedia media) =>
        PlaybackEnd(media) - media.PlaybackStartMs;

    static void ValidateClipSource(CustomShowClip clip, string folder)
    {
        if (clip.Source == null)
        {
            if (clip.SourceStartMs != null || clip.SourceEndMs != null)
                throw new InvalidDataException("Clip source ranges require a clip source.");
            return;
        }
        if (clip.Source.Mode is not "copy" and not "reference" ||
            string.IsNullOrWhiteSpace(clip.Source.Path))
            throw new InvalidDataException("Invalid clip source metadata.");
        if (clip.SourceStartMs is not long start || clip.SourceEndMs is not long end ||
            start < 0 || end <= start || end - start != clip.EndMs - clip.StartMs)
            throw new InvalidDataException("Invalid clip source range.");
        if (clip.Source.Mode == "copy" &&
            !File.Exists(ResolveRelative(folder, clip.Source.Path)))
            throw new FileNotFoundException("The copied clip source video is missing.");
    }

    static ModelCard? FindIstripperModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        return (Datastore.modelcards ?? []).FirstOrDefault(card =>
            !card.IsCustom && string.Equals(card.modelId?.Trim(), modelId.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }

    static ModelClip CreateClip(string name, string foreground, string? alpha,
        long size, int number, string hotness, string[] types,
        int alphaThreshold, float edgeChokePixels, long startMs,
        long endMs, CustomVirtualGreenScreen virtualGreenScreen,
        string mediaMode, CustomRvmOnnxSettings? rvmOnnx) => new()
    {
        clipName = name,
        customForegroundPath = foreground,
        customAlphaPath = alpha,
        customMediaMode = mediaMode,
        customRvmOnnxSettings = rvmOnnx?.Clone(),
        customAlphaThreshold = alphaThreshold,
        customEdgeChokePixels = edgeChokePixels,
        customVirtualGreenScreen = virtualGreenScreen,
        customStartMs = startMs,
        customEndMs = endMs,
        size = size,
        isEnabled = true,
        hotnessCode = ParseHotness(hotness),
        clipType = string.Join(", ", types),
        clipNumber = number
    };

    static CardResolutionType ResolutionFor(int height) => height switch
    {
        >= 2160 => CardResolutionType.highest,
        >= 1600 => CardResolutionType.high,
        >= 1080 => CardResolutionType.medium,
        >= 720 => CardResolutionType.low,
        _ => CardResolutionType.lowest
    };

    internal static HotnessCode ParseHotness(string value) => value switch
    {
        "Public" => HotnessCode.publ,
        "Topless" => HotnessCode.topless,
        "Nudity" => HotnessCode.nudity,
        "FullNudity" => HotnessCode.fullnudity,
        "XXX" => HotnessCode.xxx,
        _ => HotnessCode.nonudity
    };

    static void ValidateId(string id, string description)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException($"The {description} ID must be a 32-character UUID.");
    }

    static void ValidateMeasurement(decimal? value, decimal min, decimal max, string name)
    {
        if (value is not null && (value < min || value > max))
            throw new InvalidDataException($"Model {name} must be {min}–{max} cm.");
    }

    static bool PropSegmenterCompatible(string algorithm,
        bool sam2MattingRvmInitializer, string? maskEngine) =>
        algorithm is "matanyone2" or "rvm-matanyone2" or
            "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
        sam2MattingRvmInitializer || maskEngine == "rvm-sam2";

    internal static bool VerifyCoreLogic()
    {
        return AgeAt(new DateOnly(2000, 8, 8), new DateOnly(2026, 8, 7)) == 25 &&
            AgeAt(new DateOnly(2000, 8, 7), new DateOnly(2026, 8, 7)) == 26 &&
            CmToInches(91.44m) == 36m && CmToLegacyHeight(170m) == "5.7" &&
            TryFrameRate("30000/1001", out double rate) && rate > 29.9 && rate < 30 &&
            (FindPythonForVerification() is string python &&
                (python.Length == 0 || File.Exists(python))) &&
            Sam2MattingSupport.VerifyPromptModes() &&
            Sam2MattingScenePlanner.VerifySceneValidation() &&
            PropSegmenterCompatible("rvm-matanyone2", false, null) &&
            PropSegmenterCompatible("rvm-vitmatte-s", false, null) &&
            PropSegmenterCompatible("rvm-vitmatte-b", false, null) &&
            PropSegmenterCompatible("vitmatte-s", false, "rvm-sam2") &&
            PropSegmenterCompatible("vitmatte-b", false, "rvm-sam2") &&
            PropSegmenterCompatible("sam2matting", true, null) &&
            PropSegmenterCompatible("matanyone2", false, null) &&
            !PropSegmenterCompatible("quality", false, null) &&
            VerifyVirtualGreenScreen() &&
            VerifyRealtimeManifestContracts() &&
            RejectsTraversal();
    }

    static bool VerifyRealtimeManifestContracts()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "iqp-realtime-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            using (Bitmap cover = new(8, 8)) cover.Save(Path.Combine(root,
                "cover.jpg"), System.Drawing.Imaging.ImageFormat.Jpeg);
            CustomClipMedia? legacy = JsonSerializer.Deserialize<CustomClipMedia>(
                "{\"foreground\":\"foreground.mp4\",\"alpha\":\"alpha.mkv\"," +
                "\"width\":64,\"height\":64,\"frameRate\":\"10/1\"," +
                "\"durationMs\":500}", JsonOptions);
            if (legacy?.Mode != CustomClipMedia.PairedAlphaMode)
                return false;
            CustomShowClip paired = new()
            {
                StartMs = 0, EndMs = 500,
                Media = legacy
            };
            CustomShowClip rvmOnnx = new()
            {
                StartMs = 500, EndMs = 1000,
                RvmOnnx = new(),
                Media = new()
                {
                    Mode = CustomClipMedia.RvmOnnxMode,
                    Foreground = "rvm-onnx/foreground.mp4",
                    Width = 64, Height = 64, FrameRate = "10/1",
                    DurationMs = 500
                }
            };
            Directory.CreateDirectory(Path.Combine(root, "rvm-onnx"));
            File.WriteAllBytes(Path.Combine(root, "foreground.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(root, "alpha.mkv"), [1]);
            File.WriteAllBytes(Path.Combine(root, "rvm-onnx", "foreground.mp4"),
                [1, 2, 3]);
            CustomShowManifest show = new()
            {
                Title = "Mixed", PerformerId = Guid.NewGuid().ToString("N"),
                Source = new() { Mode = "reference", Path = "source.mp4" },
                Media = new() { Width = 64, Height = 64, FrameRate = "10/1",
                    DurationMs = 1000 },
                Clips = [paired, rvmOnnx],
                Processing = new()
                {
                    Algorithm = CustomClipMedia.RvmOnnxMode,
                    ExecutionPolicy = "realtime-playback",
                    PrecisionPolicy = "onnx-directml-fp32",
                    ResolvedExecutionMode = "metadata-only-reuse",
                    Clips = [new() { ClipId = paired.Id },
                        new() { ClipId = rvmOnnx.Id }]
                }
            };
            ValidateManifest(show, root);
            bool invalidRvmQualityRejected = Reject(() =>
            {
                rvmOnnx.RvmOnnx!.Quality = "maximum";
                ValidateManifest(show, root);
            });
            rvmOnnx.RvmOnnx!.Quality = "balanced";
            bool invalidRvmModelRejected = Reject(() =>
            {
                rvmOnnx.RvmOnnx!.Model = "unknown";
                ValidateManifest(show, root);
            });
            rvmOnnx.RvmOnnx!.Model = "mobilenetv3";
            bool missingRvmModelDefaultsToMobileNet =
                JsonSerializer.Deserialize<CustomRvmOnnxSettings>("{}",
                    JsonOptions)?.Model == "mobilenetv3";
            bool realtimeAlphaRejected = Reject(() =>
            {
                rvmOnnx.Media!.Alpha = "alpha.mkv";
                try { ValidateManifest(show, root); }
                finally { rvmOnnx.Media.Alpha = null; }
            });
            bool traversalRejected = Reject(() =>
            {
                string original = rvmOnnx.Media!.Foreground;
                rvmOnnx.Media.Foreground = "../escape.mp4";
                try { ValidateManifest(show, root); }
                finally { rvmOnnx.Media.Foreground = original; }
            });
            bool pairedWithoutAlphaRejected = Reject(() =>
            {
                string? original = paired.Media!.Alpha;
                paired.Media.Alpha = null;
                try { ValidateManifest(show, root); }
                finally { paired.Media.Alpha = original; }
            });
            JsonNode retired = JsonNode.Parse("""
                {
                  "processing": { "algorithm": "nvidia-aigs",
                    "precisionPolicy": "sdk-managed" },
                  "clips": [{ "media": { "mode": "nvidia-aigs" },
                    "nvidia": { "mode": 0, "temporal": true,
                      "inferenceResolution": "720p" } }]
                }
                """)!;
            bool retiredMigrated = MigrateRetiredRealtimeRenderer(retired) &&
                retired["processing"]?["algorithm"]?.GetValue<string>() ==
                    CustomClipMedia.RvmOnnxMode &&
                retired["clips"]?[0]?["media"]?["mode"]?.GetValue<string>() ==
                    CustomClipMedia.RvmOnnxMode &&
                retired["clips"]?[0]?["nvidia"] == null &&
                retired["clips"]?[0]?["rvmOnnx"]?["model"]?.GetValue<string>() ==
                    RvmOnnxSupport.MobileNetV3;
            return invalidRvmQualityRejected && invalidRvmModelRejected &&
                missingRvmModelDefaultsToMobileNet && realtimeAlphaRejected &&
                traversalRejected && pairedWithoutAlphaRejected &&
                retiredMigrated;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        static bool Reject(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }
    }

    static bool VerifyVirtualGreenScreen()
    {
        CustomVirtualGreenScreen show = new()
        {
            SmallPatchSize = 12,
            MinimumTransparentAreaRadius = 10,
            Colors = [new() { Color = "#00FF00", Tolerance = 18, Feather = 5 }]
        };
        CustomVirtualGreenScreen disabled = new() { Enabled = false };
        CustomVirtualGreenScreen replacement = new()
        {
            Colors = [new() { Color = "#0000FF", Tolerance = 10 }]
        };
        VirtualGreenScreenSample target = VirtualGreenScreenMath.Samples(show)[0];
        byte cb = (byte)Math.Round(target.Cb * 255);
        byte cr = (byte)Math.Round(target.Cr * 255);
        float feathered = VirtualGreenScreenMath.Keep(
            target.Cb + target.Tolerance + target.Feather / 2, target.Cr,
            [target]);
        CustomVirtualGreenScreen hardEdge = new()
        {
            Colors = [new() { Color = "#00FF00", Tolerance = 0, Feather = 0 }]
        };
        VirtualGreenScreenSample hardTarget =
            VirtualGreenScreenMath.Samples(hardEdge)[0];
        CustomVirtualGreenScreen protectedColor = new()
        {
            Colors =
            [
                new() { Color = "#00FF00", Tolerance = 18, Feather = 5 },
                new() { Color = "#00FF00", Tolerance = 18, Feather = 5,
                    Locked = true }
            ]
        };
        CustomVirtualGreenScreen blockedByThreshold = show.Clone();
        blockedByThreshold.KeyOverLockedThreshold = 100;
        CustomVirtualGreenScreen allowedByThreshold = show.Clone();
        allowedByThreshold.KeyOverLockedThreshold = 99;
        CustomVirtualGreenScreen disabledColor = new()
        {
            Colors = [new() { Color = "#00FF00", Tolerance = 18,
                Feather = 5, Disabled = true }]
        };
        CustomVirtualGreenScreen roleFeathers = new()
        {
            KeyFeather = 0,
            LockedFeather = 20,
            Colors =
            [
                new() { Color = "#00FF00", Tolerance = 18, Feather = 50 },
                new() { Color = "#FF0000", Tolerance = 18, Feather = 50,
                    Locked = true }
            ]
        };
        VirtualGreenScreenSample[] roleSamples =
            VirtualGreenScreenMath.Samples(roleFeathers);
        CustomVirtualGreenScreen neutralChroma = new()
        {
            ColorDomain = "ycbcr",
            Colors = [new() { Color = "#808080", Tolerance = 1, Feather = 0 }]
        };
        CustomVirtualGreenScreen neutralRgb = neutralChroma.Clone();
        neutralRgb.ColorDomain = "rgb";
        bool domainsMatchTheirOwnTargets = VirtualGreenScreenMath.ColorDomains.All(
            domain =>
            {
                CustomVirtualGreenScreen settings = new()
                {
                    ColorDomain = domain,
                    Colors = [new() { Color = "#28C070", Tolerance = 1,
                        Feather = 0 }]
                };
                return VirtualGreenScreenMath.MatchStrength(
                    Color.FromArgb(0x28, 0xC0, 0x70), settings.Colors[0],
                    settings) == 1;
            });
        CustomVirtualGreenScreen largePalette = new()
        {
            Colors = Enumerable.Range(0, 300)
                .Select(_ => new CustomVirtualGreenScreenColor()).ToArray()
        };
        ValidateVirtualGreenScreen(largePalette);
        return VirtualGreenScreenMath.Resolve(show, null).Colors[0].Color ==
                "#00FF00" &&
            VirtualGreenScreenMath.SmallPatchSize(show) == 12 &&
            VirtualGreenScreenMath.MinimumTransparentAreaRadius(show) == 10 &&
            VirtualGreenScreenMath.SmallPatchSize(disabled) == 0 &&
            VirtualGreenScreenMath.MinimumTransparentAreaRadius(disabled) == 0 &&
            !VirtualGreenScreenMath.Resolve(show, disabled).Enabled &&
            VirtualGreenScreenMath.Resolve(show, replacement).Colors[0].Color ==
                "#0000FF" &&
            VirtualGreenScreenMath.Apply(255, cb, cr, 0, 200, show) == 0 &&
            VirtualGreenScreenMath.Apply(255, cb, cr, 0, 200,
                protectedColor) == 255 &&
            VirtualGreenScreenMath.Apply(255, cb, cr, 0, 200,
                blockedByThreshold) == 255 &&
            VirtualGreenScreenMath.Apply(255, cb, cr, 0, 200,
                allowedByThreshold) == 0 &&
            VirtualGreenScreenMath.Apply(255, cb, cr, 0, 200,
                disabledColor) == 255 &&
            roleSamples.Length == 2 && roleSamples[0].Locked &&
            Math.Abs(roleSamples[0].Feather - 20f / 255) < .0001f &&
            !roleSamples[1].Locked && roleSamples[1].Feather == 0 &&
            feathered == 0 &&
            VirtualGreenScreenMath.Samples(largePalette).Length == 300 &&
            domainsMatchTheirOwnTargets &&
            VirtualGreenScreenMath.MatchStrength(Color.FromArgb(32, 32, 32),
                neutralChroma.Colors[0], neutralChroma) == 1 &&
            VirtualGreenScreenMath.MatchStrength(Color.FromArgb(32, 32, 32),
                neutralRgb.Colors[0], neutralRgb) == 0 &&
            VirtualGreenScreenMath.ReducesAlpha(200, 199) &&
            !VirtualGreenScreenMath.ReducesAlpha(200, 200) &&
            !VirtualGreenScreenMath.ReducesAlpha(200, 201) &&
            VirtualGreenScreenMath.Keep(hardTarget.Cb, hardTarget.Cr,
                [hardTarget]) == 0 &&
            VirtualGreenScreenMath.Keep(hardTarget.Cb + 1f / 255,
                hardTarget.Cr, [hardTarget]) == 1 &&
            VirtualGreenScreenMath.Apply(128, 128, 128, 0, 200, show) == 128 &&
            VirtualGreenScreenMath.Apply(255, 128, 128, 0, 200,
                (CustomVirtualGreenScreen?)null) == 255;
    }

    static string FindPythonForVerification() =>
        CustomShowConfiguration.FindPythonExecutable();

    internal static bool VerifyIntegration()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "iqp-custom-show-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            CustomShowStore store = new(root);
            store.EnsureCreated();
            CustomPerformerProfile profile = new()
            {
                IstripperModelId = "test-native-model",
                ModelName = "Test Model", Gender = "Non-Binary", HeightCm = 170,
                BustCm = 91.44m, WaistCm = 66, HipsCm = 94
            };
            store.SavePerformer(profile);
            bool duplicateNameRejected = false;
            try
            {
                store.SavePerformer(new CustomPerformerProfile
                {
                    ModelName = " test model ", Gender = "Non-Binary"
                });
            }
            catch (InvalidDataException) { duplicateNameRejected = true; }
            if (!duplicateNameRejected)
            {
                Console.Error.WriteLine("Duplicate custom model name was accepted.");
                return false;
            }
            CustomShowManifest show = new()
            {
                PerformerId = profile.Id, Title = "Test Show",
                Gender = "Non-Binary",
                AgeAtReleaseOverride = 24,
                ClipDetection = new()
                {
                    Method = "omnishotcut",
                    ToolRevision = "test-omni-revision",
                    OverlapFrames = 20,
                    TransitionBufferMs = 250,
                    MinimumClipMs = 10_000,
                    ManuallyEdited = true
                },
                Media = new() { Width = 64, Height = 64,
                    FrameRate = "10/1", DurationMs = 1000,
                    CoverModelFont = "Arial", CoverTitleFont = "Tahoma" },
                Source = new() { Mode = "reference", Path = "test-source.mp4" },
                VirtualGreenScreen = new()
                {
                    SmallPatchSize = 8,
                    MinimumTransparentAreaRadius = 6,
                    ColorDomain = "oklab",
                    KeyOverLockedThreshold = 23,
                    KeyFeather = 7,
                    LockedFeather = 11,
                    Colors = [new() { Color = "#00FF00", Tolerance = 18,
                        Feather = 5, Locked = true }]
                },
                Clips =
                [
                    new() { StartMs = 0, EndMs = 400, Hotness = "NoNudity",
                        AlphaThreshold = 24, EdgeChokePixels = 1.25f,
                        ClipTypes = ["Standing"], DetectionLabels = ["General"] },
                    new() { StartMs = 400, EndMs = 600, Included = false,
                        Hotness = "NoNudity", ClipTypes = ["Standing"],
                        DetectionLabels = ["Fade"] },
                    new() { StartMs = 600, EndMs = 1000, Hotness = "Topless",
                        ClipTypes = ["Table"],
                        DetectionLabels = ["General", "Hard Cut"] }
                ]
            };
            string folder = Path.Combine(store.ShowsFolder, show.Id);
            Directory.CreateDirectory(folder);
            string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            Run(ffmpeg, "-y", "-v", "error", "-f", "lavfi", "-i",
                "testsrc2=size=64x64:rate=10:duration=1", "-f", "lavfi", "-i",
                "sine=frequency=440:duration=1", "-map", "0:v", "-map", "1:a",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
                "-shortest", Path.Combine(folder, "source.mp4"));
            foreach (CustomShowClip clip in show.Clips.Where(clip => clip.Included))
            {
                string relative = Path.Combine("clips", clip.Id);
                string clipFolder = Path.Combine(folder, relative);
                Directory.CreateDirectory(clipFolder);
                double seconds = (clip.EndMs - clip.StartMs) / 1000d;
                Run(ffmpeg, "-y", "-v", "error", "-f", "lavfi", "-i",
                    $"testsrc2=size=64x64:rate=10:duration={seconds}",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p",
                    Path.Combine(clipFolder, "foreground.mp4"));
                Run(ffmpeg, "-y", "-v", "error", "-f", "lavfi", "-i",
                    $"color=white:size=64x64:rate=10:duration={seconds}",
                    "-vf", "format=yuv420p", "-c:v", "libx264", "-crf", "10",
                    Path.Combine(clipFolder, "alpha.mkv"));
                clip.Media = new()
                {
                    Foreground = Path.Combine(relative, "foreground.mp4").Replace('\\', '/'),
                    Alpha = Path.Combine(relative, "alpha.mkv").Replace('\\', '/'),
                    Width = 64, Height = 64, FrameRate = "10/1",
                    DurationMs = clip.EndMs - clip.StartMs
                };
            }
            CustomClipMedia trimmedFixture = show.Clips[0].Media!;
            trimmedFixture.PlaybackStartMs = 100;
            trimmedFixture.PlaybackEndMs = 300;
            show.Processing = new()
            {
                Algorithm = "rvm-matanyone2", MattingDetailPx = 512,
                BatchSize = 3, RvmInitializerAlphaThresholdPercent = 40,
                PropSegmenterModelId = "verification-model",
                PropSegmenterCheckpointSha256 = new string('a', 64),
                PropSegmenterConfidenceThreshold = .5,
                PropSegmenterProximityRadiusAt512 = 24,
                PropSegmenterManifestSchemaVersion = 2,
                PropSegmenterArchitecture = "rvm-conditioned-convnext-fpn-v2",
                PropSegmenterManifestSha256 = new string('b', 64),
                PropSegmenterPostprocessingContract = "rvm-conditioned-temporal-union-v2",
                RvmMatAnyoneMaskRefresh = true,
                PropSegmenterEveryFrame = false,
                DebugPropContribution = false,
                RvmMatAnyoneRefreshStrengthPercent = 75,
                MatAnyoneMaxMemoryFrames = 10,
                MatAnyoneUseLongTermMemory = true,
                TemporalAlphaCleanup = true,
                TemporalAlphaCleanupWindowFrames = 4,
                TemporalAlphaCleanupStrengthPercent = 75,
                TemporalAlphaTrackingStrengthPercent = 60,
                TemporalAlphaCleanupAlphaThreshold = 90,
                AutoAcceptedAlphaThreshold = 25,
                ExecutionPolicy = "auto",
                ResolvedExecutionMode = "eager",
                EffectiveBatchSize = 3,
                PipelineDepth = 3,
                Encoder = "libx264",
                EncoderPreset = "slow/medium",
                PrecisionPolicy = "fp16-autocast-fp32-cpu-fallback",
                RecurrentRefinementSteps = 11,
                ProcessedUtc = DateTime.UtcNow,
                QuickPlayerVersion = "verification",
                ToolRevisions = new() { ["MATANYONE2_COMMIT"] = "test-revision" },
                Clips = show.Clips.Where(clip => clip.Included).Select(clip =>
                    new CustomClipProcessing
                    {
                        ClipId = clip.Id,
                        // RVM-MatAnyone records where its automatic initializer
                        // came from even though it does not run SAM2.
                        InitialMaskFrameMs = clip.StartMs,
                        Sam2MaskTracking = false
                    }).ToArray()
            };
            show.Clips[2].VirtualGreenScreen = new() { Enabled = false };
            using (Bitmap cover = new(64, 96)) cover.Save(
                Path.Combine(folder, "cover.jpg"), System.Drawing.Imaging.ImageFormat.Jpeg);
            store.SaveManifest(show);
            CustomShowManifest roundTrip = store.LoadManifest(show.Id);
            if (store.LoadPerformer(profile.Id).Gender != "Non-Binary" ||
                roundTrip.Processing?.Algorithm != "rvm-matanyone2" ||
                roundTrip.Processing.MattingDetailPx != 512 ||
                roundTrip.Processing.RvmInitializerAlphaThresholdPercent != 40 ||
                !roundTrip.Processing.RvmMatAnyoneMaskRefresh ||
                roundTrip.Processing.PropSegmenterEveryFrame ||
                roundTrip.Processing.DebugPropContribution ||
                roundTrip.Processing.RvmMatAnyoneRefreshStrengthPercent != 75 ||
                roundTrip.Processing.MatAnyoneMaxMemoryFrames != 10 ||
                !roundTrip.Processing.MatAnyoneUseLongTermMemory ||
                !roundTrip.Processing.TemporalAlphaCleanup ||
                roundTrip.Processing.TemporalAlphaCleanupWindowFrames != 4 ||
                roundTrip.Processing.TemporalAlphaCleanupStrengthPercent != 75 ||
                roundTrip.Processing.TemporalAlphaTrackingStrengthPercent != 60 ||
                roundTrip.Processing.TemporalAlphaCleanupAlphaThreshold != 90 ||
                roundTrip.Processing.AutoAcceptedAlphaThreshold != 25 ||
                roundTrip.Processing.Sam2Model != null ||
                roundTrip.Processing.ResolvedExecutionMode != "eager" ||
                roundTrip.Processing.EffectiveBatchSize != 3 ||
                roundTrip.Processing.PipelineDepth != 3 ||
                roundTrip.Processing.Encoder != "libx264" ||
                roundTrip.Processing.EncoderPreset != "slow/medium" ||
                roundTrip.Processing.Clips.Length != 2 ||
                roundTrip.Media.CoverModelFont != "Arial" ||
                roundTrip.Media.CoverTitleFont != "Tahoma" ||
                roundTrip.Gender != "Non-Binary" ||
                roundTrip.ClipDetection?.Method != "omnishotcut" ||
                roundTrip.ClipDetection.OverlapFrames != 20 ||
                !roundTrip.ClipDetection.ManuallyEdited ||
                roundTrip.Clips[0].Media?.PlaybackStartMs != 100 ||
                roundTrip.Clips[0].Media?.PlaybackEndMs != 300 ||
                roundTrip.Clips[0].EdgeChokePixels != 1.25f ||
                roundTrip.VirtualGreenScreen?.Colors.Single().Color != "#00FF00" ||
                roundTrip.VirtualGreenScreen.SmallPatchSize != 8 ||
                roundTrip.VirtualGreenScreen.MinimumTransparentAreaRadius != 6 ||
                roundTrip.VirtualGreenScreen.ColorDomain != "oklab" ||
                roundTrip.VirtualGreenScreen.KeyOverLockedThreshold != 23 ||
                roundTrip.VirtualGreenScreen.KeyFeather != 7 ||
                roundTrip.VirtualGreenScreen.LockedFeather != 11 ||
                roundTrip.VirtualGreenScreen.Colors[0].Tolerance != 18 ||
                roundTrip.VirtualGreenScreen.Colors[0].Feather != 5 ||
                !roundTrip.VirtualGreenScreen.Colors[0].Locked ||
                roundTrip.Clips[0].VirtualGreenScreen != null ||
                roundTrip.Clips[2].VirtualGreenScreen?.Enabled != false ||
                !roundTrip.Clips[1].DetectionLabels.SequenceEqual(["Fade"]) ||
                !roundTrip.Clips[2].DetectionLabels.Contains("Hard Cut"))
            {
                Console.Error.WriteLine("Custom processing provenance round-trip failed.");
                return false;
            }
            bool greenPreviewReady = false;
            using (VirtualGreenScreenEditorForm greenEditor = new(
                new CustomShowConfiguration { LibraryRoot = root }, show.Id)
            {
                Opacity = 0,
                ShowInTaskbar = false
            })
            using (System.Windows.Forms.Timer previewCheck = new()
                { Interval = 100 })
            {
                int attempts = 0;
                previewCheck.Tick += (_, _) =>
                {
                    greenPreviewReady = greenEditor.PreviewReady;
                    if (greenPreviewReady || ++attempts >= 30)
                    {
                        previewCheck.Stop();
                        greenEditor.Close();
                    }
                };
                greenEditor.Shown += (_, _) => previewCheck.Start();
                greenEditor.ShowDialog();
            }
            if (!greenPreviewReady)
            {
                Console.Error.WriteLine(
                    "Virtual green-screen preview did not render a frame.");
                return false;
            }
            using (CustomClipEditorForm editor = new(
                Path.Combine(folder, "source.mp4"), show.Clips,
                show.Hotness, show.ClipTypes, allowBoundaryEditing: false))
                editor.CreateControl();
            bool completed = false, failed = false;
            Exception? playbackError = null;
            using (CustomPlayerForm player = new(
                ResolveRelative(folder, show.Clips[0].Media!.Foreground),
                ResolveRelative(folder, show.Clips[0].Media!.Alpha), 10, 0,
                suppressErrorDialog: true, startMs: 0, endMs: 400,
                edgeChokePixels: show.Clips[0].EdgeChokePixels))
            using (System.Windows.Forms.Timer exercise = new() { Interval = 120 })
            using (System.Windows.Forms.Timer timeout = new() { Interval = 5_000 })
            {
                int step = 0;
                player.PlaybackCompleted += (_, _) => completed = true;
                player.PlaybackFailed += (_, error) =>
                {
                    failed = true;
                    playbackError = error;
                };
                exercise.Tick += (_, _) =>
                {
                    if (++step == 1) player.SetRate(.5);
                    else if (step == 2) player.SeekTo(.25);
                    else if (step == 3) player.TogglePause();
                    else if (step == 4) { player.TogglePause(); player.SetRate(2); }
                    else exercise.Stop();
                };
                timeout.Tick += (_, _) => { failed = true; player.Close(); };
                player.Shown += (_, _) => { exercise.Start(); timeout.Start(); };
                player.ShowDialog();
            }
            if (!completed || failed)
            {
                Console.Error.WriteLine($"Custom player check: completed={completed}, failed={failed}, error={playbackError}");
                return false;
            }
            CustomClipMedia probeMedia = show.Clips[0].Media!;
            string alphaProbe = ResolveRelative(folder, probeMedia.Alpha);
            using (CustomPlayerForm earlyClose = new(
                ResolveRelative(folder, probeMedia.Foreground),
                alphaProbe, 10, 0, suppressErrorDialog: true))
            using (System.Windows.Forms.Timer close = new() { Interval = 80 })
            {
                close.Tick += (_, _) =>
                {
                    close.Stop();
                    earlyClose.Close();
                };
                earlyClose.Shown += (_, _) => close.Start();
                earlyClose.ShowDialog();
                earlyClose.ClosePlayerAsync().GetAwaiter().GetResult();
                using FileStream unlocked = new(alphaProbe, FileMode.Open,
                    FileAccess.Read, FileShare.None);
            }
            string cleanedAlphaProbe = CleanedAlphaPath(alphaProbe);
            File.Copy(alphaProbe, cleanedAlphaProbe);
            IReadOnlyList<ModelCard> cards = store.LoadCards(false);
            List<ModelClip>? loadedClips = cards.FirstOrDefault()?.clips;
            if (cards.Count != 1 || cards[0].name != "custom:" + show.Id ||
                cards[0].modelId != "test-native-model" ||
                cards[0].gender != "Non-Binary" ||
                cards[0].modelAge != 24 || cards[0].bust != 36 ||
                cards[0].height != "5.7" || loadedClips?.Count != 2 ||
                loadedClips[0].customStartMs != 100 ||
                loadedClips[0].customEndMs != 300 ||
                loadedClips[0].customAlphaThreshold != 24 ||
                loadedClips[0].customEdgeChokePixels != 1.25f ||
                loadedClips[0].customVirtualGreenScreen.Colors.Single().Color !=
                    "#00FF00" ||
                !string.Equals(loadedClips[0].customAlphaPath,
                    cleanedAlphaProbe, StringComparison.OrdinalIgnoreCase) ||
                loadedClips[1].customStartMs != 0 ||
                loadedClips[1].customVirtualGreenScreen.Enabled ||
                loadedClips[1].hotnessCode != HotnessCode.topless)
            {
                Console.Error.WriteLine("Custom card check failed: " +
                    string.Join(" | ", store.LoadWarnings));
                return false;
            }
            profile.ModelName = "Updated Model";
            store.SavePerformer(profile);
            if (store.LoadCards(false).Single().modelName != "Updated Model")
            {
                Console.Error.WriteLine("Custom performer propagation check failed.");
                return false;
            }
            string replacement = Path.Combine(root, ".staging", show.Id);
            CopyDirectoryForVerification(folder, replacement);
            roundTrip.Title = "Reprocessed Test Show";
            store.Replace(replacement, roundTrip);
            folder = Path.Combine(store.ShowsFolder, show.Id);
            if (store.LoadManifest(show.Id).Title != "Reprocessed Test Show" ||
                Directory.Exists(replacement))
            {
                Console.Error.WriteLine("Custom show replacement check failed.");
                return false;
            }
            string removedClipId = roundTrip.Clips[0].Id;
            string removedClipFolder = Path.Combine(folder, "clips",
                removedClipId);
            string? cleanupWarning = store.DeleteClip(show.Id,
                removedClipId, recycleMedia: false);
            CustomShowManifest afterDeletion = store.LoadManifest(show.Id);
            IReadOnlyList<ModelCard> afterDeletionCards =
                store.LoadCards(false);
            if (cleanupWarning != null || Directory.Exists(removedClipFolder) ||
                afterDeletion.Clips.Single(value => value.Id == removedClipId)
                    .Included ||
                afterDeletion.Clips.Single(value => value.Id == removedClipId)
                    .Media != null ||
                afterDeletion.Processing?.Clips.Any(value =>
                    value.ClipId == removedClipId) != false ||
                afterDeletionCards.Single().clips?.Count != 1)
            {
                Console.Error.WriteLine("Custom clip deletion check failed: " +
                    cleanupWarning);
                return false;
            }
            CustomShowManifest invalid = new()
            {
                PerformerId = profile.Id, Title = "Invalid",
                Media = new() { Width = 64, Height = 64,
                    FrameRate = "10/1", DurationMs = 1000 },
                Source = new() { Mode = "reference", Path = "source.mp4" },
                Clips = [new()
                {
                    StartMs = 0, EndMs = 1000,
                    Media = new() { Width = 64, Height = 64,
                        FrameRate = "10/1", DurationMs = 1000,
                        Foreground = "../foreground.mp4", Alpha = "alpha.mkv" }
                }]
            };
            string invalidFolder = Path.Combine(store.ShowsFolder, invalid.Id);
            Directory.CreateDirectory(invalidFolder);
            WriteJsonAtomic(Path.Combine(invalidFolder, "show.json"), invalid);
            bool malformedSkipped = store.LoadCards(false).Count == 1 &&
                store.LoadWarnings.Count == 1;
            CustomShowCheckReport malformedReport = store.CheckShows();
            if (!malformedSkipped || malformedReport.TotalShows != 2 ||
                malformedReport.ValidShows != 1 ||
                malformedReport.Issues.Count != 1 ||
                malformedReport.Issues[0].CanRepair)
                return false;

            CustomShowClip remaining = afterDeletion.Clips.Single(clip =>
                clip.Included);
            File.WriteAllText(ResolveRelative(folder, remaining.Media!.Alpha),
                "invalid alpha video");
            bool normalLoadTrustsManifest = store.LoadCards(false).Count == 1;
            CustomShowCheckReport mediaReport = store.CheckShows();
            return normalLoadTrustsManifest && mediaReport.TotalShows == 2 &&
                mediaReport.ValidShows == 0 && mediaReport.Issues.Count == 2 &&
                mediaReport.Issues.Any(issue => issue.CanRepair &&
                    issue.ShowId == show.Id);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Custom-show integration failed: " + error);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch { }
        }
    }

    static void Run(string executable, params string[] arguments)
    {
        ProcessStartInfo start = new(executable)
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg did not start.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg fixture creation failed.");
    }

    static void CopyDirectoryForVerification(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string child in Directory.EnumerateDirectories(source))
            CopyDirectoryForVerification(child,
                Path.Combine(destination, Path.GetFileName(child)));
    }

    static bool RejectsTraversal()
    {
        try { ResolveRelative(Path.GetTempPath(), "..\\escape.mp4"); return false; }
        catch (InvalidDataException) { return true; }
    }
}
