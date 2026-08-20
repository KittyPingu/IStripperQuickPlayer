using System.Text.Json.Serialization;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class TrainingDataset
{
    public int SchemaVersion { get; set; } = 1;
    public string DatasetId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string[] SourceFolders { get; set; } = [];
    public string? ActiveSourceFolder { get; set; }
    public List<VideoSource> Sources { get; set; } = [];
    [JsonIgnore]
    public List<TrainingSample> Samples { get; set; } = [];
}

internal sealed class TrainingSampleLedger
{
    public int SchemaVersion { get; set; } = 1;
    public string DatasetId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<TrainingSample> Samples { get; set; } = [];
}

internal sealed class TrainingSampleRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string DatasetId { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public TrainingSample Sample { get; set; } = new();
}

internal sealed class VideoSource
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FramesPerSecond { get; set; }
    public string Split { get; set; } = "train";
    public string? ProbeError { get; set; }
}

internal sealed class TrainingSample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceId { get; set; } = "";
    public long TimestampMs { get; set; }
    public string Decision { get; set; } = "draft";
    public string Split { get; set; } = "train";
    public string? BurstId { get; set; }
    public int? BurstIndex { get; set; }
    public bool FeedbackPriority { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int ObjectCount { get; set; }
    public string? FramePath { get; set; }
    public string? AnnotationPath { get; set; }
    public string? EditableMaskPath { get; set; }
    public string? InstanceMaskPath { get; set; }
    public string? PropMaskPath { get; set; }
    public string? RvmPersonMaskPath { get; set; }
    public string? DesiredForegroundPath { get; set; }
    public string? RvmRevision { get; set; }
    public double? RvmThreshold { get; set; }
    public string? DerivationStatus { get; set; }
    public string? DerivationError { get; set; }
    public DateTime PresentedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedUtc { get; set; }

    [JsonIgnore]
    public bool Accepted => Decision is "positive" or "negative";
}

internal sealed class SampleAnnotation
{
    public int SchemaVersion { get; set; } = 1;
    public string Category { get; set; } = "foreground_prop";
    public List<AnnotationObject> Objects { get; set; } = [];
    public string Provenance { get; set; } = "manual";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class AnnotationObject
{
    public int Id { get; set; }
    public string Name { get; set; } = "Prop";
    public int ColorArgb { get; set; }
}

internal readonly record struct DatasetStatistics(
    int Positive, int Negative, int Rejected, int Draft, int Objects,
    int Videos, int CoveredVideos, int Train, int Validation, int Test)
{
    internal int Accepted => Positive + Negative;
    internal double NegativeRatio => Accepted == 0 ? 0 : (double)Negative / Accepted;
}

internal readonly record struct MediaInfo(
    long DurationMs, int Width, int Height, double FramesPerSecond);
