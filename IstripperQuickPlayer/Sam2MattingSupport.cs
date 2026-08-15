using System.Globalization;

namespace IStripperQuickPlayer;

internal static class Sam2MattingSupport
{
    internal const string Algorithm = "sam2matting";
    internal const int OptionVersion = 3;
    internal const string AlphaEncodingPolicy = "h264-yuv420p-linear";
    internal const int ScenePlanVersion = 1;
    internal const string EnvironmentVersion = "sam2matting-v1";
    internal const string SourceRevision =
        "73dd721d77b56749248aefe5e8824d7f61b9d13c";
    internal const string CheckpointRevision =
        "4315db9c60d27fde396b09765748a0ca6c97bed5";
    internal static readonly HashSet<string> Trackers =
        ["sam2.1-tiny", "sam2.1-base-plus", "sam3"];
    internal static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "sam2matting-sam3-worker", "v1");

    internal static string DisplayName(string? tracker) => tracker switch
    {
        "sam2.1-tiny" => "SAM2.1-T",
        "sam2.1-base-plus" => "SAM2.1-B+",
        _ => "SAM3"
    };

    internal static bool IsValidPromptMode(string tracker, string? promptMode) =>
        tracker == "sam3" ? promptMode == "text-concepts" :
        promptMode is "initial-mask" or "rvm-initial-mask";

    internal static bool IsSupportedOptionContract(int version, string tracker,
        string? promptMode) =>
        version == OptionVersion && IsValidPromptMode(tracker, promptMode) ||
        version == 2 && IsValidPromptMode(tracker, promptMode) &&
            promptMode != "rvm-initial-mask";

    internal static string CheckpointFile(string? tracker) => tracker switch
    {
        "sam2.1-tiny" => "SAM2Matting-SAM2.1Tiny.pt",
        "sam2.1-base-plus" => "SAM2Matting-SAM2.1Base+.pt",
        _ => "SAM2Matting-SAM3.pt"
    };

    internal static string CheckpointSha256(string? tracker) => tracker switch
    {
        "sam2.1-tiny" =>
            "5b9321e3b51bc20f5b84c208746cc083dd3053dd701590f2e88dc8640afcc39d",
        "sam2.1-base-plus" =>
            "1f0eb2eda3e8bc9101eafc0b30b8b8fcae1ff83d8fd3adc18e2f3b410fdaae60",
        _ => "7102d695be6070b39acd67464f93207df725514a688b545ed1267d913d3b9c7d"
    };

    internal static long CheckpointSize(string? tracker) => tracker switch
    {
        "sam2.1-tiny" => 215_569_778,
        "sam2.1-base-plus" => 383_180_506,
        _ => 3_509_720_141
    };

    internal static string[] ParseConcepts(string? text)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string line in (text ?? "").Replace("\r\n", "\n")
                     .Replace('\r', '\n').Split('\n'))
        {
            string value = line.Trim();
            if (value.Length == 0 || !seen.Add(value)) continue;
            if (value.Length > 200)
                throw new InvalidDataException(
                    "A foreground concept cannot exceed 200 characters.");
            result.Add(value);
        }
        if (result.Count == 0)
            throw new InvalidDataException(
                "Enter at least one foreground concept for SAM3.");
        if (result.Count > 64)
            throw new InvalidDataException(
                "A SAM3 job cannot contain more than 64 foreground concepts.");
        return [.. result];
    }

    internal static bool IsInstalled(CustomShowConfiguration configuration,
        string? tracker = null)
    {
        try
        {
            string python = configuration.Sam2MattingPythonExecutable;
            if (!File.Exists(python) || !File.Exists(Path.Combine(RuntimeRoot,
                    "environment.json")) || !Directory.Exists(Path.Combine(
                    RuntimeRoot, "source", "SAM2Matting"))) return false;
            IEnumerable<string> selected = tracker == null ? Trackers : [tracker];
            return selected.All(value => Trackers.Contains(value) &&
                new FileInfo(Path.Combine(RuntimeRoot, "checkpoints",
                    CheckpointFile(value))) is { Exists: true } checkpoint &&
                checkpoint.Length == CheckpointSize(value));
        }
        catch { return false; }
    }

    internal static bool VerifyConceptParser()
    {
        string[] parsed = ParseConcepts(" person \r\nBicycle\nPERSON\n\n" +
            "handbag held by a person ");
        try { _ = ParseConcepts(" \r\n "); return false; }
        catch (InvalidDataException) { }
        return parsed.SequenceEqual(
            ["person", "Bicycle", "handbag held by a person"]);
    }

    internal static bool VerifyPromptModes() =>
        IsValidPromptMode("sam2.1-tiny", "initial-mask") &&
        IsValidPromptMode("sam2.1-tiny", "rvm-initial-mask") &&
        IsValidPromptMode("sam2.1-base-plus", "initial-mask") &&
        IsValidPromptMode("sam2.1-base-plus", "rvm-initial-mask") &&
        IsValidPromptMode("sam3", "text-concepts") &&
        !IsValidPromptMode("sam3", "rvm-initial-mask") &&
        !IsValidPromptMode("sam2.1-tiny", "text-concepts") &&
        IsSupportedOptionContract(2, "sam3", "text-concepts") &&
        IsSupportedOptionContract(2, "sam2.1-tiny", "initial-mask") &&
        !IsSupportedOptionContract(2, "sam2.1-tiny", "rvm-initial-mask") &&
        IsSupportedOptionContract(OptionVersion, "sam2.1-tiny",
            "rvm-initial-mask");
}

internal static class Sam2MattingScenePlanner
{
    internal static async Task<(CustomShowClipDetection Detection,
        CustomShowProcessingScene[] Scenes)> DetectAsync(
        CustomShowConfiguration configuration, string source,
        CustomShowClip[] clips, IProgress<int>? progress,
        CancellationToken token, CustomShowClipDetection? requested = null)
    {
        using FfmpegCpuDecoder decoder = new(source, fastDecode: true);
        long durationMs = checked((long)Math.Round(decoder.Duration * 1000));
        if (!CustomShowStore.TryFrameRate(decoder.FrameRate, out double fps))
            throw new InvalidDataException("The source frame rate is invalid.");
        string method = requested?.Method ?? ResolveDetector(configuration);
        if (method == "transnetv2" &&
            !CustomShowProcessor.IsTransNetV2Installed(configuration) ||
            method == "omnishotcut" &&
            !CustomShowProcessor.IsOmniShotCutInstalled(configuration) ||
            method is not ("ffmpeg" or "transnetv2" or "omnishotcut"))
            throw new InvalidDataException(
                $"The queued scene detector '{method}' is no longer installed.");
        using IDisposable? gpuLease = method == "ffmpeg" ? null :
            await CustomShowGpuScheduler.AcquireAsync(token);
        int sensitivity = requested?.SensitivityPercent ?? method switch
        {
            "transnetv2" => configuration.TransNetClipDetectionSensitivity,
            "omnishotcut" => configuration.OmniShotCutClipDetectionSensitivity,
            _ => configuration.FastClipDetectionSensitivity
        };
        CustomShowClip[] includedClips = clips.Where(value => value.Included)
            .OrderBy(value => value.StartMs).ToArray();
        if (includedClips.Length == 0)
            throw new InvalidDataException("At least one included clip is required.");
        long selectedDurationMs = Math.Max(1, includedClips.Sum(value =>
            Math.Max(1, value.EndMs - value.StartMs)));
        long completedDurationMs = 0;
        List<long> boundaries = [];
        List<SceneDetectionRange> ranges = [];
        SceneDetectionResult? firstResult = null;
        foreach (CustomShowClip clip in includedClips)
        {
            long clipDurationMs = Math.Max(1, clip.EndMs - clip.StartMs);
            long completedBeforeClip = completedDurationMs;
            Progress<int> clipProgress = new(value => progress?.Report((int)Math.Clamp(
                (completedBeforeClip + clipDurationMs * Math.Clamp(value, 0, 100) / 100d) /
                selectedDurationMs * 100, 0, 99)));
            SceneDetectionResult clipResult = method switch
            {
                "transnetv2" => await CustomSceneDetector.DetectTransNetAsync(
                    configuration, source, sensitivity, clipProgress, token,
                    clip.StartMs, clip.EndMs),
                "omnishotcut" => await CustomSceneDetector.DetectOmniShotCutAsync(
                    configuration, source, sensitivity, clipProgress, token,
                    clip.StartMs, clip.EndMs),
                _ => await CustomSceneDetector.DetectFastAsync(source, durationMs,
                    sensitivity, clipProgress, token, clip.StartMs, clip.EndMs)
            };
            firstResult ??= clipResult;
            boundaries.AddRange(clipResult.BoundariesMs.Select(value =>
                checked(value + clip.StartMs)));
            ranges.AddRange(clipResult.Ranges.Select(value => value with
            {
                StartMs = checked(value.StartMs + clip.StartMs),
                EndMs = checked(value.EndMs + clip.StartMs)
            }));
            completedDurationMs += clipDurationMs;
        }
        progress?.Report(100);
        SceneDetectionResult result = (firstResult ?? throw new InvalidDataException(
            "Scene detection did not return a result.")) with
        {
            BoundariesMs = [.. boundaries],
            Ranges = [.. ranges],
            // Detector score streams are relative to each bounded clip and cannot
            // be safely reapplied as one source-wide stream.
            SensitivityDataFormat = null,
            SensitivityData = null
        };
        if (!string.IsNullOrWhiteSpace(requested?.ToolRevision) &&
            !string.Equals(requested.ToolRevision, result.ToolRevision,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The queued scene detector revision has changed. Edit the job to " +
                "choose the currently installed detector.");
        CustomShowClipDetection detection = new()
        {
            Method = result.Method,
            ToolRevision = result.ToolRevision,
            OverlapFrames = result.OverlapFrames,
            SensitivityPercent = sensitivity,
            SensitivityDataFormat = result.SensitivityDataFormat,
            SensitivityData = result.SensitivityData,
            DetectionFrameRate = result.DetectionFrameRate,
            TransitionBufferMs = 0,
            MinimumClipMs = 0,
            ManuallyEdited = false
        };
        long totalFrames = Math.Max(1, checked((long)Math.Round(
            durationMs / 1000d * fps)));
        long[] cutFrames = result.BoundariesMs.Select(value => Frame(value, fps))
            .Where(value => value > 0 && value < totalFrames).Distinct().Order().ToArray();
        List<CustomShowProcessingScene> scenes = [];
        foreach (CustomShowClip clip in includedClips)
        {
            long clipStart = Math.Clamp(Frame(clip.StartMs, fps), 0, totalFrames - 1);
            long clipEnd = Math.Clamp(Frame(clip.EndMs, fps), clipStart + 1, totalFrames);
            long[] points = [clipStart, .. cutFrames.Where(value =>
                value > clipStart && value < clipEnd), clipEnd];
            for (int index = 0; index + 1 < points.Length; index++)
                scenes.Add(new CustomShowProcessingScene
                {
                    Id = SceneId(clip.Id, points[index], points[index + 1]),
                    ClipId = clip.Id,
                    StartFrame = points[index],
                    EndFrameExclusive = points[index + 1],
                    StartMs = Milliseconds(points[index], fps),
                    EndMs = Milliseconds(points[index + 1], fps)
                });
        }
        Validate(scenes, clips);
        ValidateCoverage(scenes, clips, fps);
        return (detection, [.. scenes]);
    }

    internal static CustomShowClipDetection ResolveRequest(
        CustomShowConfiguration configuration)
    {
        string method = ResolveDetector(configuration);
        int sensitivity = method switch
        {
            "transnetv2" => configuration.TransNetClipDetectionSensitivity,
            "omnishotcut" => configuration.OmniShotCutClipDetectionSensitivity,
            _ => configuration.FastClipDetectionSensitivity
        };
        string? marker = method switch
        {
            "transnetv2" => "TRANSNETV2_COMMIT",
            "omnishotcut" => "OMNISHOTCUT_COMMIT",
            _ => null
        };
        string? revision = null;
        if (marker != null)
        {
            string path = Path.Combine(CustomShowProcessor.RuntimeRoot(configuration),
                marker);
            if (File.Exists(path)) revision = File.ReadAllText(path).Trim();
        }
        return new CustomShowClipDetection
        {
            Method = method,
            ToolRevision = string.IsNullOrWhiteSpace(revision) ? null : revision,
            SensitivityPercent = sensitivity,
            TransitionBufferMs = 0,
            MinimumClipMs = 0,
            ManuallyEdited = false
        };
    }

    internal static void Validate(IEnumerable<CustomShowProcessingScene> source,
        CustomShowClip[] clips)
    {
        CustomShowProcessingScene[] scenes = source.ToArray();
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (CustomShowClip clip in clips.Where(value => value.Included))
        {
            CustomShowProcessingScene[] values = scenes.Where(value =>
                value.ClipId.Equals(clip.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.StartFrame).ToArray();
            if (values.Length == 0)
                throw new InvalidDataException(
                    "Every included clip requires a SAM2Matting scene plan.");
            for (int index = 0; index < values.Length; index++)
            {
                CustomShowProcessingScene scene = values[index];
                if (!ids.Add(scene.Id) || scene.EndFrameExclusive <= scene.StartFrame ||
                    scene.EndMs <= scene.StartMs || index > 0 &&
                    values[index - 1].EndFrameExclusive != scene.StartFrame)
                    throw new InvalidDataException(
                        "SAM2Matting scenes must be unique, ordered, and contiguous.");
            }
        }
        if (scenes.Any(scene => !clips.Any(clip => clip.Included &&
                clip.Id.Equals(scene.ClipId, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(
                "A SAM2Matting scene references an unknown clip.");
    }

    internal static void ValidateCoverage(
        IEnumerable<CustomShowProcessingScene> source, CustomShowClip[] clips,
        double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0)
            throw new InvalidDataException("The canonical scene frame rate is invalid.");
        CustomShowProcessingScene[] scenes = source.ToArray();
        foreach (CustomShowClip clip in clips.Where(value => value.Included))
        {
            CustomShowProcessingScene[] values = scenes.Where(scene =>
                    scene.ClipId.Equals(clip.Id,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(scene => scene.StartFrame).ToArray();
            long expectedStart = Frame(clip.StartMs, fps);
            long expectedEnd = Math.Max(expectedStart + 1, Frame(clip.EndMs, fps));
            if (values.Length == 0 || values[0].StartFrame != expectedStart ||
                values[^1].EndFrameExclusive != expectedEnd ||
                values.Zip(values.Skip(1)).Any(pair =>
                    pair.First.EndFrameExclusive != pair.Second.StartFrame))
                throw new InvalidDataException(
                    "SAM2Matting scenes must cover every canonical clip frame exactly once.");
        }
    }

    internal static bool VerifySceneValidation()
    {
        CustomShowClip clip = new() { StartMs = 0, EndMs = 1000 };
        CustomShowProcessingScene[] scenes =
        [
            new() { ClipId = clip.Id, StartFrame = 0, EndFrameExclusive = 10,
                StartMs = 0, EndMs = 400 },
            new() { ClipId = clip.Id, StartFrame = 10, EndFrameExclusive = 25,
                StartMs = 400, EndMs = 1000 }
        ];
        Validate(scenes, [clip]);
        scenes[1].StartFrame = 11;
        try { Validate(scenes, [clip]); return false; }
        catch (InvalidDataException) { return true; }
    }

    static string ResolveDetector(CustomShowConfiguration configuration)
    {
        // FFmpeg is always available with QuickPlayer, so an explicit Fast
        // preference is already a resolved, installed detector choice.
        if (configuration.LastClipDetector == "ffmpeg") return "ffmpeg";
        if (configuration.LastClipDetector == "omnishotcut" &&
            CustomShowProcessor.IsOmniShotCutInstalled(configuration))
            return "omnishotcut";
        if (configuration.LastClipDetector == "transnetv2" &&
            CustomShowProcessor.IsTransNetV2Installed(configuration))
            return "transnetv2";
        if (CustomShowProcessor.IsTransNetV2Installed(configuration))
            return "transnetv2";
        if (CustomShowProcessor.IsOmniShotCutInstalled(configuration))
            return "omnishotcut";
        return "ffmpeg";
    }

    internal static long Frame(long milliseconds, double fps) => checked((long)Math.Round(
        milliseconds / 1000d * fps, MidpointRounding.AwayFromZero));
    static long Milliseconds(long frame, double fps) => checked((long)Math.Round(
        frame / fps * 1000d, MidpointRounding.AwayFromZero));
    static string SceneId(string clipId, long start, long end)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{clipId}:{start}:{end}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }
}

internal static class CustomShowGpuScheduler
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token);
        return new Lease();
    }

    sealed class Lease : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Gate.Release();
        }
    }
}
