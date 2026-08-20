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
            if (!DatasetStore.IsSourceInFolder(Path.Combine(root, "videos", "one.mp4"), root) ||
                DatasetStore.IsSourceInFolder(Path.Combine(Path.GetDirectoryName(root)!, "outside.mp4"), root))
                return false;
            DatasetStore store = new(root);
            string rescanRoot = Path.Combine(root, "rescan-index");
            string nestedVideos = Path.Combine(rescanRoot, "nested");
            Directory.CreateDirectory(nestedVideos);
            File.WriteAllBytes(Path.Combine(rescanRoot, "first.mp4"), [1]);
            DatasetStore rescanStore = new(Path.Combine(root, "rescan-dataset"));
            if (rescanStore.IndexFolderAsync(rescanRoot, null, CancellationToken.None)
                    .GetAwaiter().GetResult() != 1 ||
                rescanStore.IndexFolderAsync(rescanRoot, null, CancellationToken.None)
                    .GetAwaiter().GetResult() != 0) return false;
            File.WriteAllBytes(Path.Combine(nestedVideos, "second.mkv"), [2]);
            if (rescanStore.IndexFolderAsync(rescanRoot, null, CancellationToken.None)
                    .GetAwaiter().GetResult() != 1 ||
                rescanStore.Dataset.Sources.Count != 2 ||
                !string.Equals(rescanStore.Dataset.ActiveSourceFolder, Path.GetFullPath(rescanRoot),
                    StringComparison.OrdinalIgnoreCase)) return false;
            string split = DatasetStore.SplitFor("000000000000000000000000");
            if (split != "train" || DatasetStore.FrameTimestamp(1_033, 30) != 1_033) return false;
            if (DatasetStore.SplitFor("ffffffff0000000000000000") is not ("train" or "validation" or "test"))
                return false;
            if (AnnotationCanvas.AdjustBrushSize(32, 120) != 36 ||
                AnnotationCanvas.AdjustBrushSize(2, -120) != 2 ||
                AnnotationCanvas.AdjustBrushSize(200, 120) != 200 ||
                !DatasetStore.BurstOffsetsMs().SequenceEqual(
                    [-3_000L, -1_000L, 0L, 1_000L, 3_000L])) return false;
            using (AnnotationCanvas historyCanvas = new())
            {
                int promptCount = 1;
                historyCanvas.RecordExternalChange(() => promptCount--, () => promptCount++);
                historyCanvas.Undo(); if (promptCount != 0) return false;
                historyCanvas.Redo(); if (promptCount != 1) return false;
            }
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
            byte[] sourceManifestHash = System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(root, "dataset.json")));
            TrainingSample first = store.NextCandidate()!;
            if (first.TimestampMs < 30_000 || first.TimestampMs > 70_000 || first.Split != source.Split) return false;
            store.Reject(first);
            if (store.NextCandidate(null, 2160) != null) return false;
            TrainingSample positive = store.NextCandidate()!;
            if (Math.Abs(positive.TimestampMs - first.TimestampMs) <= 10_000) return false;
            string adaptiveRoot = Path.Combine(root, "adaptive-spacing");
            DatasetStore adaptiveStore = new(adaptiveRoot);
            VideoSource adaptiveSource = new() { Id = "adaptive-source", Path = sourcePath,
                DurationMs = 100_000, Width = 2, Height = 2, FramesPerSecond = 30, Split = "train" };
            adaptiveStore.Dataset.Sources.Add(adaptiveSource);
            foreach (long timestamp in new long[] { 30_000, 45_000, 60_000, 70_000 })
                adaptiveStore.Dataset.Samples.Add(new TrainingSample { SourceId = adaptiveSource.Id,
                    TimestampMs = timestamp, Decision = "rejected", Split = adaptiveSource.Split });
            TrainingSample adaptive = adaptiveStore.NextCandidate()!;
            long adaptiveDistance = adaptiveStore.Dataset.Samples.Where(value => value.Id != adaptive.Id)
                .Min(value => Math.Abs(value.TimestampMs - adaptive.TimestampMs));
            if (adaptiveDistance <= 5_000 || adaptiveDistance > 10_000 ||
                !DatasetStore.CandidateSpacingMs.SequenceEqual([10_000, 5_000, 2_000])) return false;
            positive.Width = positive.Height = 2;
            positive.FramePath = CreateDraftFrame(store, positive);
            AnnotationObject prop = new() { Id = 7, Name = "Prop", ColorArgb = Color.Lime.ToArgb() };
            int[] positiveIds = [0, 7, 7, 0];
            store.SaveDraftAnnotation(positive, positiveIds, [prop], "verification");
            byte[] draftRecordHash = Hash(store.RecordPath(positive.Id));
            int[] editedPositiveIds = [7, 7, 7, 0];
            store.SaveDraftAnnotation(positive, editedPositiveIds, [prop], "verification");
            if (!draftRecordHash.SequenceEqual(Hash(store.RecordPath(positive.Id)))) return false;
            var loaded = store.LoadDraftAnnotation(positive);
            if (loaded.Ids == null || !loaded.Ids.SequenceEqual(editedPositiveIds) ||
                loaded.Objects.Single().Id != 7) return false;
            store.Accept(positive, editedPositiveIds, [prop], "verification");
            TrainingSample negative = new() { SourceId = sourceId, Split = source.Split,
                TimestampMs = 69_500, Width = 2, Height = 2 };
            negative.FramePath = CreateDraftFrame(store, negative);
            store.Dataset.Samples.Add(negative); store.SaveSample(negative);
            store.Accept(negative, new int[4], [], "confirmed-negative");
            if (store.MostRecentDecision()?.Id != negative.Id) return false;
            store.UndoDecision(negative);
            var restoredNegative = store.LoadDraftAnnotation(negative);
            if (negative.Decision != "draft" || restoredNegative.Ids == null ||
                restoredNegative.Ids.Any(value => value != 0)) return false;
            store.Accept(negative, new int[4], [], "confirmed-negative");
            DatasetStatistics statistics = store.Statistics();
            if (statistics.Positive != 1 || statistics.Negative != 1 || statistics.Rejected != 1 ||
                statistics.Objects != 1 || statistics.CoveredVideos != 1 || statistics.NegativeRatio != .5)
                return false;
            byte[] rejectedRecordHash = Hash(store.RecordPath(first.Id));
            byte[] negativeRecordHash = Hash(store.RecordPath(negative.Id));
            byte[] positiveRecordHash = Hash(store.RecordPath(positive.Id));
            store.SetDerivation(positive, "ready", "person.png", "desired.png", null,
                "verification-rvm", .4);
            store.MarkFeedbackPriority(positive);
            if (!rejectedRecordHash.SequenceEqual(Hash(store.RecordPath(first.Id))) ||
                !negativeRecordHash.SequenceEqual(Hash(store.RecordPath(negative.Id))) ||
                positiveRecordHash.SequenceEqual(Hash(store.RecordPath(positive.Id)))) return false;
            int[] ids = [0, 1, 256, 65535];
            string mask = Path.Combine(root, "mask.png"); PngMaskWriter.SaveGray16(mask, 2, 2, ids);
            if (!File.Exists(mask) || new FileInfo(mask).Length < 40) return false;
            string previewSource = Path.Combine(root, "preview-source.png");
            using (Bitmap bitmap = new(2, 2)) bitmap.Save(previewSource, ImageFormat.Png);
            using (Bitmap preview = TrainingStudioForm.LoadUnlockedImage(previewSource))
                File.Move(previewSource, Path.Combine(root, "preview-moved.png"));
            string historyRoot = Path.Combine(root, "history");
            DatasetStore historyStore = new(historyRoot);
            VideoSource historySource = new() { Id = "history-source", Path = sourcePath,
                DurationMs = 100_000, Width = 2, Height = 2, FramesPerSecond = 30, Split = "train" };
            historyStore.Dataset.Sources.Add(historySource);
            TrainingSample olderHistory = new() { SourceId = historySource.Id, Split = "train",
                TimestampMs = 40_000, Width = 2, Height = 2 };
            olderHistory.FramePath = CreateDraftFrame(historyStore, olderHistory);
            historyStore.Dataset.Samples.Add(olderHistory); historyStore.SaveSample(olderHistory);
            historyStore.Accept(olderHistory, [0, 7, 7, 0], [prop], "verification");
            olderHistory.ReviewedUtc = DateTime.UtcNow.AddMinutes(-2); historyStore.SaveSample(olderHistory);
            TrainingSample newerHistory = new() { SourceId = historySource.Id, Split = "train",
                TimestampMs = 50_000, Width = 2, Height = 2 };
            newerHistory.FramePath = CreateDraftFrame(historyStore, newerHistory);
            historyStore.Dataset.Samples.Add(newerHistory); historyStore.SaveSample(newerHistory);
            historyStore.Accept(newerHistory, new int[4], [], "confirmed-negative");
            if (!historyStore.AcceptedHistory().Select(value => value.Id)
                    .SequenceEqual([newerHistory.Id, olderHistory.Id])) return false;
            using (Bitmap overlay = DatasetHistoryForm.CreateMaskOverlay(
                       historyStore.Resolve(olderHistory.FramePath!),
                       historyStore.Resolve(olderHistory.PropMaskPath!)))
            {
                Color foregroundOverlay = overlay.GetPixel(1, 0);
                if (overlay.Width != 2 || overlay.Height != 2 || foregroundOverlay.G < 140 ||
                    foregroundOverlay.R > 30 || foregroundOverlay.B > 30) return false;
            }
            string deletedFolder = Path.GetDirectoryName(historyStore.Resolve(newerHistory.FramePath!))!;
            historyStore.DeleteAcceptedSample(newerHistory);
            if (newerHistory.Decision != "rejected" || newerHistory.FramePath != null ||
                newerHistory.PropMaskPath != null || Directory.Exists(deletedFolder) ||
                historyStore.AcceptedHistory().Single().Id != olderHistory.Id ||
                !File.Exists(historyStore.RecordPath(newerHistory.Id))) return false;
            string review = Path.Combine(root, "runs", "verification", "package", "review");
            Directory.CreateDirectory(review);
            using (Bitmap bitmap = new(2, 2)) bitmap.Save(Path.Combine(review, "fp.png"), ImageFormat.Png);
            DatasetStore.WriteJsonAtomic(Path.Combine(review, "review.json"), new Dictionary<string, object>
            {
                ["false-positive"] = new[] { new { sampleId = positive.Id,
                    errorPixelsAtInput = 84, errorPixelsAt512 = 42, image = "fp.png" } },
                ["false-negative"] = Array.Empty<object>()
            });
            string package = Directory.GetParent(review)!.FullName;
            File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
            File.WriteAllBytes(Path.Combine(package, "model.pth"), [1]);
            IReadOnlyList<ErrorReviewItem> reviewItems = ErrorReviewForm.LoadItems(review);
            if (reviewItems.Count != 1 || reviewItems[0].SampleId != positive.Id ||
                reviewItems[0].Kind != "false-positive" || reviewItems[0].ErrorPixels != 84 ||
                ErrorReviewForm.FindLatest(root) != review ||
                TrainingRunner.FindLatestPackage(root) != package) return false;
            ErrorReviewForm.MarkHidden(review, "false-positive", positive.Id);
            if (!ErrorReviewForm.LoadItems(review)[0].Hidden) return false;
            TrainingDataset roundTrip = System.Text.Json.JsonSerializer.Deserialize<TrainingDataset>(
                File.ReadAllText(Path.Combine(root, "dataset.json")),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            TrainingSampleRecord[] sampleRoundTrip = Directory.EnumerateFiles(Path.Combine(root, "records"),
                "*.json", SearchOption.AllDirectories).Select(path =>
                    System.Text.Json.JsonSerializer.Deserialize<TrainingSampleRecord>(File.ReadAllText(path),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).ToArray();
            if (roundTrip.SchemaVersion != 1 || roundTrip.DatasetId != store.Dataset.DatasetId ||
                sampleRoundTrip.Length != 3 || sampleRoundTrip.Any(value => value.Sample.Split != source.Split) ||
                !sampleRoundTrip.Single(value => value.Sample.Id == positive.Id).Sample.FeedbackPriority ||
                !sourceManifestHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(root, "dataset.json")))) ||
                File.Exists(Path.Combine(root, "samples.json")) ||
                File.ReadAllText(Path.Combine(root, "dataset.json")).Contains("\"samples\"",
                    StringComparison.OrdinalIgnoreCase)) return false;
            string recoveredRoot = Path.Combine(root, "recovery"); Directory.CreateDirectory(recoveredRoot);
            File.Copy(Path.Combine(root, "dataset.json"),
                Path.Combine(recoveredRoot, "dataset.json.tmp-verification"));
            string recordSource = store.RecordPath(positive.Id);
            string recordRecoveryFolder = Path.Combine(recoveredRoot, "records", positive.Id[..2]);
            Directory.CreateDirectory(recordRecoveryFolder);
            File.Copy(recordSource, Path.Combine(recordRecoveryFolder,
                positive.Id + ".json.tmp-verification"));
            DatasetStore recovered = new(recoveredRoot);
            if (recovered.Dataset.DatasetId != store.Dataset.DatasetId ||
                recovered.Dataset.Samples.Single().Id != positive.Id ||
                Directory.EnumerateFiles(recoveredRoot, "dataset.json.tmp-*").Any()) return false;
            return VerifyLegacyMigration(root);
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

    static byte[] Hash(string path) => System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(path));

    static bool VerifyLegacyMigration(string parent)
    {
        string embeddedRoot = Path.Combine(parent, "embedded-migration");
        Directory.CreateDirectory(embeddedRoot);
        TrainingSample embedded = new() { Id = "abcdef0123456789abcdef0123456789",
            SourceId = "source", Decision = "rejected" };
        DatasetStore.WriteJsonAtomic(Path.Combine(embeddedRoot, "dataset.json"), new
        {
            schemaVersion = 1, datasetId = "embedded-test", sourceFolders = Array.Empty<string>(),
            sources = Array.Empty<VideoSource>(), samples = new[] { embedded }
        });
        DatasetStore embeddedStore = new(embeddedRoot);
        if (embeddedStore.Dataset.Samples.Single().Id != embedded.Id ||
            !File.Exists(embeddedStore.RecordPath(embedded.Id)) ||
            File.ReadAllText(Path.Combine(embeddedRoot, "dataset.json")).Contains("\"samples\"",
                StringComparison.OrdinalIgnoreCase)) return false;

        string ledgerRoot = Path.Combine(parent, "ledger-migration");
        Directory.CreateDirectory(ledgerRoot);
        TrainingSample ledgerSample = new() { Id = "0123456789abcdef0123456789abcdef",
            SourceId = "source", Decision = "negative" };
        DatasetStore.WriteJsonAtomic(Path.Combine(ledgerRoot, "dataset.json"), new
        {
            schemaVersion = 1, datasetId = "ledger-test", sourceFolders = Array.Empty<string>(),
            sources = Array.Empty<VideoSource>()
        });
        DatasetStore.WriteJsonAtomic(Path.Combine(ledgerRoot, "samples.json"),
            new TrainingSampleLedger { DatasetId = "ledger-test", Samples = [ledgerSample] });
        DatasetStore ledgerStore = new(ledgerRoot);
        return ledgerStore.Dataset.Samples.Single().Id == ledgerSample.Id &&
            File.Exists(ledgerStore.RecordPath(ledgerSample.Id)) &&
            !File.Exists(Path.Combine(ledgerRoot, "samples.json")) &&
            Directory.EnumerateFiles(ledgerRoot, "samples.json.migrated*").Any();
    }
}
