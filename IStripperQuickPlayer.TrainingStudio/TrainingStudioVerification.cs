using System.Drawing.Imaging;

namespace IStripperQuickPlayer.TrainingStudio;

internal static class TrainingStudioVerification
{
    internal static bool Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "iqp-training-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            DatasetStore store = new(root);
            string split = DatasetStore.SplitFor("000000000000000000000000");
            if (split != "train" || DatasetStore.FrameTimestamp(1_033, 30) != 1_033) return false;
            if (DatasetStore.SplitFor("ffffffff0000000000000000") is not ("train" or "validation" or "test"))
                return false;
            string sourcePath = Path.Combine(root, "source.mp4");
            File.WriteAllBytes(sourcePath, [1, 2, 3]);
            FileInfo sourceFile = new(sourcePath);
            string sourceId = DatasetStore.SourceId(sourceFile);
            if (sourceId != DatasetStore.SourceId(sourceFile)) return false;
            File.AppendAllText(sourcePath, "changed"); sourceFile.Refresh();
            if (sourceId == DatasetStore.SourceId(sourceFile)) return false;
            sourceId = DatasetStore.SourceId(sourceFile);
            VideoSource source = new() { Id = sourceId, Path = sourcePath, Length = sourceFile.Length,
                LastWriteUtcTicks = sourceFile.LastWriteTimeUtc.Ticks, DurationMs = 100_000,
                Width = 2, Height = 2, FramesPerSecond = 30, Split = DatasetStore.SplitFor(sourceId) };
            store.Dataset.Sources.Add(source); store.Save();
            TrainingSample first = store.NextCandidate()!;
            if (first.TimestampMs < 30_000 || first.TimestampMs > 70_000 || first.Split != source.Split) return false;
            store.Reject(first);
            TrainingSample positive = store.NextCandidate()!;
            if (Math.Abs(positive.TimestampMs - first.TimestampMs) <= 10_000) return false;
            positive.Width = positive.Height = 2;
            positive.FramePath = CreateDraftFrame(store, positive);
            AnnotationObject prop = new() { Id = 7, Name = "Prop", ColorArgb = Color.Lime.ToArgb() };
            int[] positiveIds = [0, 7, 7, 0];
            store.SaveDraftAnnotation(positive, positiveIds, [prop], "verification");
            var loaded = store.LoadDraftAnnotation(positive);
            if (loaded.Ids == null || !loaded.Ids.SequenceEqual(positiveIds) ||
                loaded.Objects.Single().Id != 7) return false;
            store.Accept(positive, positiveIds, [prop], "verification");
            TrainingSample negative = new() { SourceId = sourceId, Split = source.Split,
                TimestampMs = 69_500, Width = 2, Height = 2 };
            negative.FramePath = CreateDraftFrame(store, negative);
            store.Dataset.Samples.Add(negative); store.Save();
            store.Accept(negative, new int[4], [], "confirmed-negative");
            DatasetStatistics statistics = store.Statistics();
            if (statistics.Positive != 1 || statistics.Negative != 1 || statistics.Rejected != 1 ||
                statistics.Objects != 1 || statistics.CoveredVideos != 1 || statistics.NegativeRatio != .5)
                return false;
            int[] ids = [0, 1, 256, 65535];
            string mask = Path.Combine(root, "mask.png"); PngMaskWriter.SaveGray16(mask, 2, 2, ids);
            if (!File.Exists(mask) || new FileInfo(mask).Length < 40) return false;
            TrainingDataset roundTrip = System.Text.Json.JsonSerializer.Deserialize<TrainingDataset>(
                File.ReadAllText(Path.Combine(root, "dataset.json")),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (roundTrip.SchemaVersion != 1 || roundTrip.DatasetId != store.Dataset.DatasetId ||
                roundTrip.Samples.Any(value => value.Split != source.Split)) return false;
            string recoveredRoot = Path.Combine(root, "recovery"); Directory.CreateDirectory(recoveredRoot);
            File.Copy(Path.Combine(root, "dataset.json"),
                Path.Combine(recoveredRoot, "dataset.json.tmp-verification"));
            DatasetStore recovered = new(recoveredRoot);
            return recovered.Dataset.DatasetId == store.Dataset.DatasetId &&
                !Directory.EnumerateFiles(recoveredRoot, "dataset.json.tmp-*").Any();
        }
        catch (Exception error) { Console.Error.WriteLine(error); return false; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }


    static string CreateDraftFrame(DatasetStore store, TrainingSample sample)
    {
        string path = Path.Combine(store.Root, "drafts", sample.Id, "frame.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Bitmap bitmap = new(2, 2); bitmap.SetPixel(0, 0, Color.White);
        bitmap.Save(path, ImageFormat.Png);
        return store.Relative(path);
    }
}
