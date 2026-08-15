using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomShowMaskDraft
{
    internal const int SchemaVersion = 1;
    readonly string root;

    internal string Root => root;
    internal string ManifestPath => Path.Combine(root, "draft.json");
    internal string SetupPath => Path.Combine(root, "setup.json");
    internal string PublishedPath => Path.Combine(root, "published.json");

    CustomShowMaskDraft(string root) => this.root = root;

    internal static CustomShowMaskDraft Open(CustomShowStore store, string source,
        string algorithm, string sam2Model, string maskEngine,
        IReadOnlyList<CustomShowClip> clips)
    {
        string fullSource = Path.GetFullPath(source);
        FileInfo file = new(fullSource);
        CustomShowMaskDraftManifest expected = new()
        {
            SourcePath = fullSource,
            SourceLength = file.Length,
            SourceLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
            Algorithm = algorithm,
            Sam2Model = sam2Model,
            MaskEngine = maskEngine,
            Clips = clips.Select((clip, index) => new CustomShowMaskDraftClip
            {
                Index = index,
                StartMs = clip.StartMs,
                EndMs = clip.EndMs
            }).ToArray()
        };
        string identity = JsonSerializer.Serialize(expected, CustomShowStore.JsonOptions);
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        CustomShowMaskDraft result = new(Path.Combine(store.Root, ".mask-drafts", key));
        Directory.CreateDirectory(result.root);
        CustomShowStore.WriteJsonAtomic(result.ManifestPath, expected);
        return result;
    }

    internal string ClipFolder(int index)
    {
        string folder = Path.Combine(root, $"clip-{index + 1:000}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal void SaveSetup(CustomShowManifest show)
    {
        CustomShowStore.WriteJsonAtomic(SetupPath, new CustomShowIncompleteSetup
        {
            SavedUtc = DateTime.UtcNow,
            Show = show
        });
    }

    internal void MarkPublished(CustomShowManifest show)
    {
        CustomShowStore.WriteJsonAtomic(PublishedPath, new CustomShowPublishedDraft
        {
            PublishedUtc = DateTime.UtcNow,
            ShowId = show.Id,
            Title = show.Title
        });
    }

    internal static IReadOnlyList<CustomShowIncompleteSetupEntry> FindIncomplete(
        CustomShowStore store)
        => FindDrafts(store, completed: false);

    internal static IReadOnlyList<CustomShowIncompleteSetupEntry> FindCompleted(
        CustomShowStore store)
        => FindDrafts(store, completed: true);

    static IReadOnlyList<CustomShowIncompleteSetupEntry> FindDrafts(
        CustomShowStore store, bool completed)
    {
        string drafts = Path.Combine(store.Root, ".mask-drafts");
        if (!Directory.Exists(drafts)) return [];
        List<CustomShowIncompleteSetupEntry> result = [];
        foreach (string folder in Directory.EnumerateDirectories(drafts))
        {
            CustomShowIncompleteSetup? setup = Load<CustomShowIncompleteSetup>(
                Path.Combine(folder, "setup.json"));
            CustomShowMaskDraftManifest? manifest = Load<CustomShowMaskDraftManifest>(
                Path.Combine(folder, "draft.json"));
            if (manifest == null || !File.Exists(manifest.SourcePath)) continue;
            CustomShowManifest? published = PublishedMatch(store, folder, manifest,
                setup);
            if ((published != null) != completed) continue;
            if (setup == null)
            {
                setup = new CustomShowIncompleteSetup
                {
                    SavedUtc = Directory.GetLastWriteTimeUtc(folder),
                    Show = new CustomShowManifest
                    {
                        Title = Path.GetFileNameWithoutExtension(manifest.SourcePath),
                        Source = new() { Mode = "reference", Path = manifest.SourcePath },
                        Clips = manifest.Clips.Select(value => new CustomShowClip
                        {
                            StartMs = value.StartMs, EndMs = value.EndMs
                        }).ToArray(),
                        Processing = new()
                        {
                            Algorithm = manifest.Algorithm,
                            Sam2Model = manifest.Sam2Model,
                            MaskEngine = manifest.MaskEngine
                        }
                    }
                };
            }
            if (published != null)
            {
                setup.Show.Id = published.Id;
                setup.Show.Title = published.Title;
            }
            result.Add(new(folder, setup));
        }
        return result.OrderByDescending(value => value.Setup.SavedUtc).ToArray();
    }

    static CustomShowManifest? PublishedMatch(CustomShowStore store, string folder,
        CustomShowMaskDraftManifest draft, CustomShowIncompleteSetup? setup)
    {
        CustomShowPublishedDraft? marker = Load<CustomShowPublishedDraft>(
            Path.Combine(folder, "published.json"));
        if (marker != null && !string.IsNullOrWhiteSpace(marker.ShowId))
        {
            CustomShowManifest? marked = Load<CustomShowManifest>(Path.Combine(
                store.ShowsFolder, marker.ShowId, "show.json"));
            if (marked != null) return marked;
        }

        if (!Directory.Exists(store.ShowsFolder)) return null;
        foreach (string showFolder in Directory.EnumerateDirectories(store.ShowsFolder))
        {
            CustomShowManifest? show = Load<CustomShowManifest>(Path.Combine(
                showFolder, "show.json"));
            if (show == null) continue;
            if (setup != null && !string.IsNullOrWhiteSpace(setup.Show.Id) &&
                string.Equals(setup.Show.Id, show.Id,
                    StringComparison.OrdinalIgnoreCase))
                return show;
            if (!SamePath(show.Source.Path, draft.SourcePath)) continue;
            CustomShowClip[] included = show.Clips.Where(clip => clip.Included).ToArray();
            if (included.Length != draft.Clips.Length) continue;
            bool sameLayout = included.Zip(draft.Clips).All(pair =>
                pair.First.StartMs == pair.Second.StartMs &&
                pair.First.EndMs == pair.Second.EndMs);
            if (sameLayout) return show;
        }
        return null;
    }

    static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static T? Load<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path),
                CustomShowStore.JsonOptions);
        }
        catch { return null; }
    }

    internal static void CopyAtomic(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, true);
            File.Move(temporary, destination, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    internal static bool VerifyRoundTrip()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "iqp-mask-draft-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "initial-mask.json");
            CustomShowStore.WriteJsonAtomic(path, new CustomInitialMaskDraft
            {
                FrameMs = 1234,
                Automatic = true,
                Points = [[12.5f, 30.25f], [8, 9]],
                Labels = [1, 0]
            });
            CustomInitialMaskDraft? loaded = Load<CustomInitialMaskDraft>(path);
            string retainedPath = Path.Combine(folder, "retained-masks.json");
            CustomShowStore.WriteJsonAtomic(retainedPath,
                new CustomRetainedMasksManifest
                {
                    SourceLength = 9876, SourceLastWriteUtcTicks = 123,
                    Clips = [new() { ClipId = Guid.NewGuid().ToString("N"),
                        StartMs = 100, EndMs = 200, InitialMaskFrameMs = 125,
                        HasInitialMask = true, HasTrackedMasks = true }]
                });
            CustomRetainedMasksManifest? retained =
                Load<CustomRetainedMasksManifest>(retainedPath);
            return loaded?.FrameMs == 1234 && loaded.Automatic &&
                loaded.Points.Length == 2 && loaded.Labels.SequenceEqual([1, 0]) &&
                retained?.SourceLength == 9876 && retained.Clips.Length == 1 &&
                retained.Clips[0].HasTrackedMasks;
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}

internal sealed class CustomShowMaskDraftManifest
{
    public int SchemaVersion { get; set; } = CustomShowMaskDraft.SchemaVersion;
    public string SourcePath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public string Algorithm { get; set; } = "";
    public string Sam2Model { get; set; } = "";
    public string MaskEngine { get; set; } = "sam2";
    public CustomShowMaskDraftClip[] Clips { get; set; } = [];
}

internal sealed class CustomShowMaskDraftClip
{
    public int Index { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
}

internal sealed class CustomInitialMaskDraft
{
    public int SchemaVersion { get; set; } = CustomShowMaskDraft.SchemaVersion;
    public long FrameMs { get; set; }
    public bool Automatic { get; set; }
    public float[][] Points { get; set; } = [];
    public int[] Labels { get; set; } = [];
}

internal sealed class CustomVideoMaskDraft
{
    public int SchemaVersion { get; set; } = CustomShowMaskDraft.SchemaVersion;
    public int FrameCount { get; set; }
    public double Fps { get; set; }
    public int ReviewWidth { get; set; }
    public int ReviewHeight { get; set; }
    public CustomVideoMaskAnchor[] Anchors { get; set; } = [];
    public CustomVideoMaskRange[] CorrectedRanges { get; set; } = [];
}

internal sealed class CustomVideoMaskAnchor
{
    public int Frame { get; set; }
    public string Mode { get; set; } = "prompt";
    public float[][] Points { get; set; } = [];
    public int[] Labels { get; set; } = [];
}

internal sealed class CustomVideoMaskRange
{
    public int AnchorFrame { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
}

internal sealed class CustomShowIncompleteSetup
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public CustomShowManifest Show { get; set; } = new();
}

internal sealed class CustomShowPublishedDraft
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime PublishedUtc { get; set; } = DateTime.UtcNow;
    public string ShowId { get; set; } = "";
    public string Title { get; set; } = "";
}

internal sealed record CustomShowIncompleteSetupEntry(
    string Folder, CustomShowIncompleteSetup Setup);

internal sealed class CustomRetainedMasksManifest
{
    public int SchemaVersion { get; set; } = 1;
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public CustomRetainedMaskClip[] Clips { get; set; } = [];
}

internal sealed class CustomRetainedMaskClip
{
    public string ClipId { get; set; } = "";
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public long? InitialMaskFrameMs { get; set; }
    public bool HasInitialMask { get; set; }
    public bool HasTrackedMasks { get; set; }
    public string? TrackedMaskArchive { get; set; }
}
