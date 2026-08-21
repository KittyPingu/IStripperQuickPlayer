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
    public string LastSam2MattingTracker { get; set; } = "sam3";
    public string LastMaskEngine { get; set; } = "sam2";
    public string LastSam2Model { get; set; } = "base-plus";
    public int LastMattingDetailPx { get; set; } = 512;
    public int LastVitMatteInferenceDetailPx { get; set; } = 1024;
    // Zero means the worker should choose and learn an adaptive batch size.
    public int LastProcessingBatchSize { get; set; }
    public int LastRvmInitializerAlphaThresholdPercent { get; set; } = 40;
    public bool LastRvmMatAnyoneMaskRefresh { get; set; }
    public bool LastPropSegmenterEveryFrame { get; set; }
    public bool LastDebugPropContribution { get; set; }
    public int LastRvmMatAnyoneRefreshStrengthPercent { get; set; } = 100;
    public int MatAnyoneMemoryDefaultsVersion { get; set; } = 1;
    public int LastMatAnyoneMaxMemoryFrames { get; set; } = 14;
    public bool LastMatAnyoneUseLongTermMemory { get; set; } = true;
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
                settingsObject.Remove("videoMaMaPreferredBatchSize");
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
                 "sam2matting"))
                configuration.LastProcessingAlgorithm = "quality";
            if (!Sam2MattingSupport.Trackers.Contains(
                    configuration.LastSam2MattingTracker))
                configuration.LastSam2MattingTracker = "sam3";
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
    string PostprocessingContract)
{
    internal static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "rvm-runtime", "prop-segmenter", "models");

    internal static bool ValidModelId(string value) => value.Length is > 0 and <= 120 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static IReadOnlyList<PropSegmenterPackage> Installed()
    {
        if (!Directory.Exists(ModelsRoot)) return [];
        List<PropSegmenterPackage> result = [];
        foreach (string folder in Directory.EnumerateDirectories(ModelsRoot))
            if (TryLoad(Path.GetFileName(folder), false, out PropSegmenterPackage? package))
                result.Add(package);
        return result.OrderByDescending(value => value.ModelId).ToArray();
    }

    internal static PropSegmenterPackage? Active(CustomShowConfiguration configuration,
        bool compatible)
    {
        if (!configuration.PropSegmenterEnabled || !compatible) return null;
        if (configuration.ActivePropSegmenterModelId is not string id ||
            !TryLoad(id, true, out PropSegmenterPackage? package))
            throw new InvalidOperationException(
                "The enabled prop-segmenter package is missing or invalid. Select an installed model in Custom Show Settings.");
        return package;
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
                !((schemaVersion == 1 && architecture == "deeplabv3-resnet50-binary-v1") ||
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
                if (input.GetProperty("cropSize").GetInt32() != 768 ||
                    input.GetProperty("channels").GetArrayLength() != 5 ||
                    runtime.GetProperty("contract").GetString() !=
                        "rvm-conditioned-temporal-union-v2" ||
                    runtime.GetProperty("pixelThreshold").GetDouble() != threshold ||
                    runtime.GetProperty("temporalWindow").GetInt32() != 3)
                    return false;
            }
            string contract = schemaVersion == 2
                ? root.GetProperty("runtime").GetProperty("contract").GetString()!
                : root.GetProperty("postprocessing").GetProperty("contract").GetString()!;
            string manifestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(manifestPath))).ToLowerInvariant();
            package = new(id, folder, hash.ToLowerInvariant(), threshold, radius,
                schemaVersion, architecture!, manifestHash, contract); return true;
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
}

internal sealed class CustomClipMedia
{
    public string Foreground { get; set; } = "";
    public string Alpha { get; set; } = "";
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
        }
        return document?.Deserialize<CustomShowManifest>(JsonOptions) ??
            throw new InvalidDataException("The JSON document is empty.");
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
        string alpha = ResolveRelative(showFolder, clip.Media.Alpha);
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
        string foreground, string alpha, bool recycle)
    {
        string? foregroundFolder = Path.GetDirectoryName(foreground);
        string? alphaFolder = Path.GetDirectoryName(alpha);
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

        foreach (string path in new[] { foreground, alpha }
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
        ValidateClips(show.Clips, show.Media.DurationMs);
        ValidateClipDetection(show.ClipDetection);
        ValidateProcessing(show.Processing, show.Clips);
        foreach (CustomShowClip clip in show.Clips)
        {
            if (clip.Included && clip.Media == null)
                throw new InvalidDataException("Every included clip needs processed media.");
            ValidateClipSource(clip, folder);
            if (clip.Media is not CustomClipMedia media) continue;
            ValidateMediaFields(media.Width, media.Height, media.FrameRate,
                media.DurationMs, "Clip media");
            long playbackEnd = PlaybackEnd(media);
            if (media.PlaybackStartMs < 0 || playbackEnd <= media.PlaybackStartMs ||
                playbackEnd > media.DurationMs)
                throw new InvalidDataException(
                    "Clip playback trim must be inside the processed media duration.");
        }
        IEnumerable<string> paths = show.Clips.Where(clip => clip.Included)
            .SelectMany(clip => new[] { clip.Media!.Foreground, clip.Media.Alpha })
            .Append(show.Media.Cover);
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
            if (processing.Tracker == "sam3")
            {
                string[] normalized = Sam2MattingSupport.ParseConcepts(
                    string.Join('\n', processing.ForegroundConcepts ?? []));
                if (!(processing.ForegroundConcepts ?? []).SequenceEqual(normalized))
                    throw new InvalidDataException(
                        "SAM3 foreground concepts are not normalized.");
            }
            else if ((processing.ForegroundConcepts?.Length ?? 0) != 0)
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
        if (processing.RvmMatAnyoneMaskRefresh &&
            processing.Algorithm != "rvm-matanyone2")
            throw new InvalidDataException(
                "RVM mask refresh is only valid for RVM-MatAnyone processing.");
        bool propEveryFrameCompatible = processing.Algorithm is
            "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b";
        if (processing.PropSegmenterEveryFrame &&
            (!hasProp || !propEveryFrameCompatible ||
                processing.Algorithm == "rvm-matanyone2" &&
                    !processing.RvmMatAnyoneMaskRefresh))
            throw new InvalidDataException(
                "Per-frame prop segmentation requires a compatible RVM pipeline and an enabled trained prop model.");
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
        if (processing.ExecutionPolicy != (sam2Matting ? "eager" : "auto") ||
            string.IsNullOrWhiteSpace(processing.PrecisionPolicy))
            throw new InvalidDataException("Invalid processing execution policy.");
        if (processing.EffectiveBatchSize is < 1 or > 24)
            throw new InvalidDataException("Invalid effective processing batch size.");
        if (processing.PipelineDepth is < 1 or > 3)
            throw new InvalidDataException("Invalid processing pipeline depth.");
        if (processing.ResolvedExecutionMode is string mode &&
            mode is not ("eager" or "compiled" or "eager-fallback" or
                "eager-oom-fallback" or "eager-bf16-sdpa" or
                "eager-bf16-sdpa-bounded"))
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
            ValidateMediaCompatibility(folder, media.Foreground, media.Alpha,
                media.Width, media.Height, media.FrameRate, media.DurationMs);
    }

    static void ValidateMediaCompatibility(string folder, string foreground,
        string alphaPath, int width, int height, string frameRate, long durationMs)
    {
        using FfmpegCpuDecoder rgb = new(ResolveRelative(folder, foreground));
        using FfmpegCpuDecoder alpha = new(ResolveRelative(folder, alphaPath));
        TryFrameRate(frameRate, out double declaredRate);
        if (rgb.Width != alpha.Width || rgb.Height != alpha.Height ||
            rgb.Width != width || rgb.Height != height ||
            Math.Abs(rgb.FrameDuration - alpha.FrameDuration) > .0005 ||
            Math.Abs(1 / rgb.FrameDuration - declaredRate) > .02 ||
            Math.Abs(rgb.Duration - alpha.Duration) > Math.Max(.05, rgb.FrameDuration * 2) ||
            Math.Abs(rgb.Duration * 1000 - durationMs) > Math.Max(100, rgb.FrameDuration * 2000))
            throw new InvalidDataException(
                "Foreground, alpha, and manifest media properties do not match.");
        if (!rgb.DecodeNext(out long rgbTime) || !alpha.DecodeNext(out long alphaTime))
            throw new InvalidDataException("Foreground or alpha contains no frames.");
        rgb.ValidateGpuFrame();
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
            new FileInfo(ResolveRelative(folder, media.Alpha)).Length);
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
                string clipAlpha = ResolveRelative(folder, media.Alpha);
                long size = checked(new FileInfo(clipForeground).Length +
                    new FileInfo(clipAlpha).Length);
                string clipName = playableMedia.Length == 1
                    ? card.name : $"{card.name}:{clip.Id}";
                return CreateClip(clipName, clipForeground, clipAlpha,
                    size, index + 1, clip.Hotness, clip.ClipTypes,
                    clip.AlphaThreshold, clip.EdgeChokePixels,
                    media.PlaybackStartMs,
                    PlaybackEnd(media));
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

    static ModelClip CreateClip(string name, string foreground, string alpha,
        long size, int number, string hotness, string[] types,
        int alphaThreshold, float edgeChokePixels, long startMs,
        long endMs) => new()
    {
        clipName = name,
        customForegroundPath = foreground,
        customAlphaPath = alpha,
        customAlphaThreshold = alphaThreshold,
        customEdgeChokePixels = edgeChokePixels,
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
        algorithm is "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
        sam2MattingRvmInitializer || maskEngine == "rvm-sam2";

    internal static bool VerifyCoreLogic()
    {
        return AgeAt(new DateOnly(2000, 8, 8), new DateOnly(2026, 8, 7)) == 25 &&
            AgeAt(new DateOnly(2000, 8, 7), new DateOnly(2026, 8, 7)) == 26 &&
            CmToInches(91.44m) == 36m && CmToLegacyHeight(170m) == "5.7" &&
            TryFrameRate("30000/1001", out double rate) && rate > 29.9 && rate < 30 &&
            (FindPythonForVerification() is string python &&
                (python.Length == 0 || File.Exists(python))) &&
            Sam2MattingSupport.VerifyConceptParser() &&
            Sam2MattingSupport.VerifyPromptModes() &&
            Sam2MattingScenePlanner.VerifySceneValidation() &&
            PropSegmenterCompatible("rvm-matanyone2", false, null) &&
            PropSegmenterCompatible("rvm-vitmatte-s", false, null) &&
            PropSegmenterCompatible("rvm-vitmatte-b", false, null) &&
            !PropSegmenterCompatible("quality", false, null) &&
            RejectsTraversal();
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
                PropSegmenterEveryFrame = true,
                DebugPropContribution = true,
                RvmMatAnyoneRefreshStrengthPercent = 75,
                MatAnyoneMaxMemoryFrames = 10,
                MatAnyoneUseLongTermMemory = true,
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
            using (Bitmap cover = new(64, 96)) cover.Save(
                Path.Combine(folder, "cover.jpg"), System.Drawing.Imaging.ImageFormat.Jpeg);
            store.SaveManifest(show);
            CustomShowManifest roundTrip = store.LoadManifest(show.Id);
            if (store.LoadPerformer(profile.Id).Gender != "Non-Binary" ||
                roundTrip.Processing?.Algorithm != "rvm-matanyone2" ||
                roundTrip.Processing.MattingDetailPx != 512 ||
                roundTrip.Processing.RvmInitializerAlphaThresholdPercent != 40 ||
                !roundTrip.Processing.RvmMatAnyoneMaskRefresh ||
                !roundTrip.Processing.PropSegmenterEveryFrame ||
                !roundTrip.Processing.DebugPropContribution ||
                roundTrip.Processing.RvmMatAnyoneRefreshStrengthPercent != 75 ||
                roundTrip.Processing.MatAnyoneMaxMemoryFrames != 10 ||
                !roundTrip.Processing.MatAnyoneUseLongTermMemory ||
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
                !roundTrip.Clips[1].DetectionLabels.SequenceEqual(["Fade"]) ||
                !roundTrip.Clips[2].DetectionLabels.Contains("Hard Cut"))
            {
                Console.Error.WriteLine("Custom processing provenance round-trip failed.");
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
                loadedClips[1].customStartMs != 0 ||
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
