using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IStripperQuickPlayer;

internal sealed class NvidiaFolderImportItem
{
    public string SourcePath { get; init; } = "";
    public string Video { get; init; } = "";
    public string Title { get; set; } = "";
    public string PerformerId { get; set; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public long DurationMs { get; init; }
    public DateOnly Date { get; init; }
    public bool CanInclude { get; init; }
    public bool Included { get; set; }
    public string Status { get; init; } = "Ready";
    public string Resolution => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "—";
    public string Length => NvidiaFolderImporter.DurationText(DurationMs);
}

internal sealed record NvidiaFolderImportProgress(int Completed, int Total,
    string Message);

internal sealed record NvidiaFolderScanResult(
    IReadOnlyList<NvidiaFolderImportItem> Items,
    IReadOnlyList<string> Warnings);

internal static partial class NvidiaFolderImporter
{
    internal static readonly HashSet<string> SupportedExtensions = new(
        [".mp4", ".mov", ".mkv", ".avi", ".webm"],
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();
    [GeneratedRegex(@"\A\s*\d{4}[-_.]\d{2}[-_.]\d{2}(?:[-_. ]+|\z)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDate();
    [GeneratedRegex(@"\b\d{4}[-_.]\d{2}[-_.]\d{2}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex AnyDate();
    [GeneratedRegex(@"\b(?:0?[1-9]|1[0-2])[-_.](?:0?[1-9]|[12]\d|3[01])[-_.]20\d{2}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex AnyUsDate();
    [GeneratedRegex(@"\A\s*(?:(?:r[\s_.-]*)?[A-Za-z][A-Za-z0-9]*[\s_.-]+)?1[a-z0-9]{5,12}[\s_.-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RedditPrefix();
    [GeneratedRegex(@"\b(?:only[\s_.-]*fans|coomer|reddit|post|video|vid|source|original|download)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceWords();
    [GeneratedRegex(@"\b(?:\d{3,4}p|[248]k|uhd|fhd|hd|x26[45]|h[._-]?26[45]|hevc|avc)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatWords();
    [GeneratedRegex(@"(?:[\s_.-]+\d{1,4})+\s*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex SequenceSuffix();
    [GeneratedRegex(@"\A\s*(?:aka|by|from)\b|\b(?:aka|by|from)\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DanglingSourceGrammar();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"\A(?=[A-Za-z0-9]*\d)[A-Za-z0-9]{6,16}(?:[\s_.-]+(?:\d{3,4}p|[248]k|uhd|fhd|hd|x26[45]|h[._-]?26[45]|hevc|avc))*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierOnly();
    [GeneratedRegex(@"\A(?<year>20\d{2})-(?<month>0[1-9]|1[0-2])(?:-(?<day>0[1-9]|[12]\d|3[01]))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex DatePrefix();
    [GeneratedRegex(@"\A(?<year>20\d{2})-(?<month>0[1-9]|1[0-2])\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex MonthFolder();

    internal static NvidiaFolderScanResult Scan(string root,
        IReadOnlySet<string> catalogSources,
        IReadOnlySet<string> queuedSources,
        IProgress<NvidiaFolderImportProgress>? progress,
        CancellationToken token)
    {
        root = Path.GetFullPath(root);
        List<string> warnings = [];
        string[] videos = EnumerateVideoFiles(root, warnings, token).ToArray();
        List<NvidiaFolderImportItem> items = [];
        Dictionary<string, int> titles = new(StringComparer.CurrentCultureIgnoreCase);
        string rootName = new DirectoryInfo(root).Name;
        for (int index = 0; index < videos.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            string video = videos[index];
            progress?.Report(new NvidiaFolderImportProgress(index, videos.Length,
                $"Reading {Path.GetFileName(video)}"));
            FileInfo info = new(video);
            DateOnly date = InferDate(video, root,
                DateOnly.FromDateTime(info.LastWriteTime));
            string title = UniqueTitle(InferTitle(video, root, rootName), titles);
            string canonical = CanonicalPath(video);
            bool inCatalog = catalogSources.Contains(canonical);
            bool inQueue = queuedSources.Contains(canonical);
            int width = 0, height = 0;
            long duration = 0;
            string? error = null;
            try
            {
                using FfmpegCpuDecoder decoder = new(video, fastDecode: true);
                width = decoder.Width;
                height = decoder.Height;
                duration = checked((long)Math.Round(decoder.Duration * 1000));
                if (width <= 0 || height <= 0 || duration <= 0)
                    throw new InvalidDataException(
                        "The video has invalid dimensions or duration.");
            }
            catch (Exception failure)
            {
                error = SingleLine(failure.Message);
            }
            bool valid = error == null && !inCatalog && !inQueue;
            items.Add(new NvidiaFolderImportItem
            {
                SourcePath = video,
                Video = Path.GetRelativePath(root, video),
                Title = title,
                Width = width,
                Height = height,
                DurationMs = duration,
                Date = date,
                CanInclude = valid,
                Included = valid,
                Status = error != null ? "Unreadable: " + error : inCatalog
                    ? "Already in custom-show catalog" : inQueue
                    ? "Already in custom-show queue" : "Ready"
            });
        }
        progress?.Report(new NvidiaFolderImportProgress(videos.Length,
            videos.Length, $"Found {videos.Length:N0} videos"));
        return new NvidiaFolderScanResult(items, warnings);
    }

    internal static IEnumerable<string> EnumerateVideoFiles(string root,
        ICollection<string>? warnings = null, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        List<string> videos = [];
        Stack<string> folders = new();
        folders.Push(root);
        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string folder = folders.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                    if (SupportedExtensions.Contains(Path.GetExtension(file)))
                        videos.Add(Path.GetFullPath(file));
            }
            catch (Exception error) when (error is IOException or
                UnauthorizedAccessException)
            {
                warnings?.Add($"{folder}: {SingleLine(error.Message)}");
            }
            try
            {
                foreach (string child in Directory.EnumerateDirectories(folder))
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(child); }
                    catch (Exception error) when (error is IOException or
                        UnauthorizedAccessException)
                    {
                        warnings?.Add($"{child}: {SingleLine(error.Message)}");
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        folders.Push(child);
                }
            }
            catch (Exception error) when (error is IOException or
                UnauthorizedAccessException)
            {
                warnings?.Add($"{folder}: {SingleLine(error.Message)}");
            }
        }
        return videos.OrderBy(path => Path.GetRelativePath(root, path),
            StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static HashSet<string> ExistingSourcePaths(CustomShowStore store,
        IEnumerable<CustomShowQueueJob> jobs)
    {
        HashSet<string> paths = ExistingCatalogSourcePaths(store);
        paths.UnionWith(ExistingQueueSourcePaths(jobs));
        return paths;
    }

    internal static HashSet<string> ExistingCatalogSourcePaths(
        CustomShowStore store)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(store.ShowsFolder)) return paths;
        foreach (string folder in Directory.EnumerateDirectories(store.ShowsFolder))
        {
            try
            {
                CustomShowManifest show = store.LoadManifest(
                    Path.GetFileName(folder));
                if (show.Source.Mode == "reference")
                    AddCanonical(paths, show.Source.Path);
                foreach (CustomShowClip clip in show.Clips)
                    if (clip.Source?.Mode == "reference")
                        AddCanonical(paths, clip.Source.Path);
            }
            catch { }
        }
        return paths;
    }

    internal static HashSet<string> ExistingQueueSourcePaths(
        IEnumerable<CustomShowQueueJob> jobs)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (CustomShowQueueJob job in jobs)
            AddCanonical(paths, job.SourcePath);
        return paths;
    }

    internal static string CanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static bool IsWithin(string path, string root)
    {
        string candidate = CanonicalPath(path);
        string parent = CanonicalPath(root);
        return string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    static void AddCanonical(ISet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { paths.Add(CanonicalPath(path)); }
        catch { }
    }

    internal static string ProfileKey(string? value) => string.Concat(
        (value ?? "").Normalize(NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    internal static CustomPerformerProfile? MatchRootProfile(string root,
        IEnumerable<CustomPerformerProfile> profiles)
    {
        string key = ProfileKey(new DirectoryInfo(Path.GetFullPath(root)).Name);
        return profiles.FirstOrDefault(profile =>
            ProfileKey(profile.ModelName) == key);
    }

    internal static string InferTitle(string video, string root,
        params string[] aliases)
    {
        string text = Path.GetFileNameWithoutExtension(video)
            .Normalize(NormalizationForm.FormKC);
        if (IdentifierOnly().IsMatch(text))
            return FallbackTitle(video, root);
        text = CamelBoundary().Replace(text, " ");
        text = LeadingDate().Replace(text, "");
        text = RedditPrefix().Replace(text, "");
        foreach (string alias in aliases.Where(value =>
                     !string.IsNullOrWhiteSpace(value)))
            text = AliasPattern(alias).Replace(text, " ");
        text = AnyDate().Replace(text, " ");
        text = AnyUsDate().Replace(text, " ");
        text = SequenceSuffix().Replace(text, " ");
        text = NonAlphanumeric().Replace(text, " ");
        text = SourceWords().Replace(text, " ");
        text = FormatWords().Replace(text, " ");
        text = DanglingSourceGrammar().Replace(text, " ");
        text = Whitespace().Replace(text, " ").Trim();
        if (string.IsNullOrWhiteSpace(text) || IdentifierOnly().IsMatch(text))
            return FallbackTitle(video, root);
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            text.ToLowerInvariant());
    }

    static Regex AliasPattern(string alias)
    {
        string[] parts = NonAlphanumeric().Split(alias).Where(part =>
            part.Length > 0).ToArray();
        return parts.Length == 0 ? new Regex("(?!)") : new Regex(
            $@"\b{string.Join(@"[\W_]*", parts.Select(Regex.Escape))}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static string FallbackTitle(string video, string root)
    {
        DirectoryInfo? folder = new FileInfo(video).Directory;
        string rootPath = CanonicalPath(root);
        while (folder != null && IsWithin(folder.FullName, rootPath))
        {
            Match match = MonthFolder().Match(folder.Name);
            if (match.Success && int.TryParse(match.Groups["year"].Value,
                    out int year) && int.TryParse(match.Groups["month"].Value,
                    out int month))
                return new DateTime(year, month, 1).ToString(
                    "MMMM yyyy 'Video'", CultureInfo.InvariantCulture);
            if (string.Equals(CanonicalPath(folder.FullName), rootPath,
                    StringComparison.OrdinalIgnoreCase)) break;
            folder = folder.Parent;
        }
        return "Imported Video";
    }

    internal static string UniqueTitle(string title,
        IDictionary<string, int> counts)
    {
        if (!counts.TryGetValue(title, out int count))
        {
            counts[title] = 1;
            return title;
        }
        counts[title] = ++count;
        return $"{title} ({count})";
    }

    internal static DateOnly InferDate(string video, string root,
        DateOnly modified)
    {
        Match prefix = DatePrefix().Match(Path.GetFileName(video));
        if (TryDate(prefix, out DateOnly exact)) return exact;
        DirectoryInfo? folder = new FileInfo(video).Directory;
        string rootPath = CanonicalPath(root);
        while (folder != null && IsWithin(folder.FullName, rootPath))
        {
            Match month = MonthFolder().Match(folder.Name);
            if (TryDate(month, out DateOnly inferred)) return inferred;
            if (string.Equals(CanonicalPath(folder.FullName), rootPath,
                    StringComparison.OrdinalIgnoreCase)) break;
            folder = folder.Parent;
        }
        return modified;
    }

    static bool TryDate(Match match, out DateOnly date)
    {
        date = default;
        if (!match.Success || !int.TryParse(match.Groups["year"].Value,
                out int year) || !int.TryParse(match.Groups["month"].Value,
                out int month)) return false;
        int day = match.Groups["day"].Success && int.TryParse(
            match.Groups["day"].Value, out int parsed) ? parsed : 1;
        return DateOnly.TryParseExact($"{year:D4}-{month:D2}-{day:D2}",
            "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }

    internal static string DurationText(long milliseconds)
    {
        if (milliseconds <= 0) return "—";
        TimeSpan duration = TimeSpan.FromMilliseconds(milliseconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes}:{duration.Seconds:D2}";
    }

    internal static CustomShowQueueBatchEntry BuildEntry(
        NvidiaFolderImportItem item, CustomPerformerProfile performer,
        CustomNvidiaSettings settings, string hotness, string[] clipTypes,
        CustomShowConfiguration configuration, CustomShowStore store)
    {
        CustomShowStore.ValidateProfile(performer);
        CustomShowStore.ValidateNvidiaSettings(settings);
        if (!item.CanInclude || item.DurationMs <= 0)
            throw new InvalidDataException(
                "Only valid videos can be added to the NVIDIA folder import.");
        if (string.IsNullOrWhiteSpace(item.Title))
            throw new InvalidDataException("Every included video requires a show name.");
        if (!CustomShowStore.HotnessOptions.Contains(hotness) ||
            clipTypes.Length == 0 || clipTypes.Any(type =>
                !CustomShowStore.ClipTypeOptions.Contains(type)))
            throw new InvalidDataException("The batch clip metadata is invalid.");

        CustomShowClip clip = new()
        {
            StartMs = 0,
            EndMs = item.DurationMs,
            Hotness = hotness,
            ClipTypes = [.. clipTypes],
            AlphaThreshold = configuration.DefaultAlphaThreshold,
            Nvidia = settings.Clone()
        };
        CustomShowManifest manifest = new()
        {
            PerformerId = performer.Id,
            Title = item.Title.Trim(),
            ReleaseDate = item.Date,
            ShowDate = item.Date,
            Gender = string.IsNullOrWhiteSpace(performer.Gender)
                ? "Female" : performer.Gender,
            Hotness = hotness,
            PerformerCount = 1,
            ClipTypes = [.. clipTypes],
            Clips = [clip],
            Source = new CustomShowSource
            {
                Mode = "reference", Path = Path.GetFullPath(item.SourcePath)
            },
            Processing = new CustomShowProcessing
            {
                Algorithm = CustomClipMedia.NvidiaAigsMode,
                MattingDetailPx = 0,
                ExecutionPolicy = "realtime-playback",
                PrecisionPolicy = "sdk-managed"
            }
        };
        FileInfo source = new(item.SourcePath);
        CustomShowQueueJob job = new()
        {
            Operation = CustomShowQueueOperation.New,
            Manifest = manifest,
            Performer = ClonePerformer(performer),
            Clips = [clip],
            SourcePath = Path.GetFullPath(item.SourcePath),
            SourceLength = source.Length,
            SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
            RequestedOutputPath = Path.Combine(store.ShowsFolder, manifest.Id),
            Message = "Pending; RGB clips will be encoded when the job runs"
        };
        return new CustomShowQueueBatchEntry(job, null,
            new Dictionary<string, string>());
    }

    static CustomPerformerProfile ClonePerformer(CustomPerformerProfile value) =>
        new()
        {
            SchemaVersion = value.SchemaVersion,
            Id = value.Id,
            IstripperModelId = value.IstripperModelId,
            ModelName = value.ModelName,
            Gender = value.Gender,
            BirthDate = value.BirthDate,
            HeightCm = value.HeightCm,
            BustCm = value.BustCm,
            WaistCm = value.WaistCm,
            HipsCm = value.HipsCm,
            Hair = value.Hair,
            Ethnicity = value.Ethnicity,
            City = value.City,
            Country = value.Country
        };

    static string SingleLine(string value)
    {
        string line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 180 ? line : line[..177] + "...";
    }

    internal static bool VerifyContracts()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "iqp-nvidia-folder-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            string month = Path.Combine(root, "2025-05");
            Directory.CreateDirectory(month);
            string nested = Path.Combine(month,
                "Charlotte40404 aka charlotte40404 - 01-25-2025 OnlyFans Video - Fun Fact_0.MP4");
            File.WriteAllText(nested, "invalid");
            File.WriteAllText(Path.Combine(month, "ignore.jpg"), "x");
            string[] found = EnumerateVideoFiles(root).ToArray();
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            CustomPerformerProfile profile = new() { ModelName = "Charlotte40404" };
            string nestedTitle = InferTitle(nested, root, "Charlotte40404");
            string redditTitle = InferTitle(Path.Combine(month,
                    "2025-05-02_r_charlotte40404_1abcde_HappyDay_SOURCE_1080p_x264_03.mp4"),
                    root, "Charlotte40404");
            string emptyTitle = InferTitle(Path.Combine(month, "_0.mp4"),
                root, "Charlotte40404");
            string identifierTitle = InferTitle(Path.Combine(root,
                "qIUdU9Q5_720p.mp4"), root, "Charlotte40404");
            bool naming = nestedTitle == "Fun Fact" &&
                redditTitle == "Happy Day" &&
                emptyTitle == "May 2025 Video" &&
                identifierTitle == "Imported Video" &&
                UniqueTitle("Show", counts) == "Show" &&
                UniqueTitle("Show", counts) == "Show (2)";
            bool dates = InferDate(Path.Combine(month,
                    "2024-11-13_example.mp4"), root, new DateOnly(2020, 1, 2)) ==
                    new DateOnly(2024, 11, 13) &&
                InferDate(Path.Combine(month, "example.mp4"), root,
                    new DateOnly(2020, 1, 2)) == new DateOnly(2025, 5, 1) &&
                MatchRootProfile(Path.Combine(root, "Charlotte40404"),
                    [profile]) == profile;
            NvidiaFolderImportItem item = new()
            {
                SourcePath = nested, Video = Path.GetFileName(nested),
                Title = "Fun Fact", Width = 1920, Height = 1080,
                DurationMs = 12_345, Date = new DateOnly(2024, 11, 13),
                CanInclude = true, Included = true
            };
            CustomShowQueueBatchEntry entry = BuildEntry(item, profile, new(),
                "NoNudity", ["Standing"], new(), new CustomShowStore(root));
            CustomShowStore store = new(root);
            CustomShowQueueJob[] allStatuses = Enum.GetValues<
                    CustomShowQueueStatus>().Select((value, index) => new
                    CustomShowQueueJob
                    {
                        Status = value,
                        SourcePath = Path.Combine(root, $"queue-{index}.mp4")
                    }).ToArray();
            HashSet<string> duplicatePaths = ExistingSourcePaths(store,
                allStatuses);
            bool duplicates = allStatuses.All(job =>
                duplicatePaths.Contains(CanonicalPath(job.SourcePath)));
            bool discovery = found.SequenceEqual([nested],
                StringComparer.OrdinalIgnoreCase);
            bool contract = discovery &&
                naming && dates && duplicates &&
                entry.Job.Manifest.Processing?.Algorithm ==
                    CustomClipMedia.NvidiaAigsMode &&
                entry.Job.Manifest.Processing.ExecutionPolicy ==
                    "realtime-playback" &&
                entry.Job.Manifest.Clips is [{ StartMs: 0, EndMs: 12_345,
                    Nvidia: not null }] &&
                entry.Job.Manifest.Clips[0].Media == null &&
                entry.Job.Manifest.ShowDate == new DateOnly(2024, 11, 13) &&
                entry.Job.SourceLength == new FileInfo(nested).Length;
            if (!contract)
                Console.Error.WriteLine(
                    $"NVIDIA folder import contracts: discovery={discovery}, " +
                    $"naming={naming} [{nestedTitle}; {redditTitle}; " +
                    $"{emptyTitle}; {identifierTitle}], dates={dates}, " +
                    $"duplicates={duplicates}");
            return contract;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("NVIDIA folder import check failed: " + error);
            return false;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

internal sealed class NvidiaFolderImportForm : Form
{
    sealed record ModelChoice(string Id, string Name);

    readonly CustomShowStore store;
    readonly CustomShowConfiguration configuration;
    readonly CustomShowQueueManager queueManager;
    readonly TextBox folder = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        EditMode = DataGridViewEditMode.EditOnEnter,
        RowHeadersVisible = false
    };
    readonly ComboBox bulkModel = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 250
    };
    readonly ComboBox nvidiaMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 290,
        DropDownWidth = 290
    };
    readonly CheckBox temporal = new()
    {
        Text = "Temporal filtering", Checked = true, AutoSize = true
    };
    readonly ComboBox resolution = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 105
    };
    readonly ComboBox hotness = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 125
    };
    readonly List<CheckBox> clipTypes = [];
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly ProgressBar progress = new() { Width = 180, Visible = false };
    readonly Button import = new() { Text = "Add to Queue", AutoSize = true,
        Enabled = false };
    readonly Button browse = new() { Text = "Browse...", AutoSize = true };
    readonly Button rescan = new() { Text = "Rescan", AutoSize = true };
    readonly Label selectionSummary = new()
    {
        Text = "0 selected", AutoSize = true, Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 10, 0)
    };
    readonly Button selectAllRows = new()
    {
        Text = "Select all rows", AutoSize = true, Enabled = false
    };
    readonly Button includeSelected = new()
    {
        Text = "Include selected", AutoSize = true, Enabled = false
    };
    readonly Button excludeSelected = new()
    {
        Text = "Exclude selected", AutoSize = true, Enabled = false
    };
    readonly Button applySelectedModel = new()
    {
        Text = "Apply model", AutoSize = true, Enabled = false
    };
    readonly Button clearSelection = new()
    {
        Text = "Clear selection", AutoSize = true, Enabled = false
    };
    readonly BindingList<NvidiaFolderImportItem> items = [];
    readonly List<CustomPerformerProfile> profiles;
    DataGridViewComboBoxColumn modelColumn = null!;
    CancellationTokenSource? scanCancellation;
    string? sortProperty;
    ListSortDirection sortDirection = ListSortDirection.Ascending;
    bool scanning;
    internal int QueuedCount { get; private set; }

    internal NvidiaFolderImportForm(CustomShowStore store,
        CustomShowConfiguration configuration,
        CustomShowQueueManager queueManager, string initialFolder)
    {
        this.store = store;
        this.configuration = configuration;
        this.queueManager = queueManager;
        profiles = store.LoadPerformers().ToList();
        CustomShowEditorForm.AddIstripperModels(profiles);
        folder.Text = Path.GetFullPath(initialFolder);
        Text = "Folder Import for NVIDIA";
        ClientSize = new Size(1210, 720);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill, Padding = new Padding(10),
            ColumnCount = 1, RowCount = 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        TableLayoutPanel folderRow = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4,
            Margin = new Padding(0, 0, 0, 8)
        };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(new Label { Text = "Folder", AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) }, 0, 0);
        folderRow.Controls.Add(folder, 1, 0);
        folderRow.Controls.Add(browse, 2, 0);
        folderRow.Controls.Add(rescan, 3, 0);
        layout.Controls.Add(folderRow, 0, 0);

        FlowLayoutPanel options = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        nvidiaMode.Items.AddRange(["Quality — chairs foreground",
            "Performance — chairs foreground", "Quality — chairs background",
            "Performance — chairs background"]);
        nvidiaMode.SelectedIndex = 0;
        resolution.Items.AddRange(["540p", "720p", "1080p", "Source"]);
        resolution.SelectedIndex = 1;
        hotness.Items.AddRange(CustomShowStore.HotnessOptions);
        hotness.SelectedItem = "NoNudity";
        options.Controls.AddRange([LabelFor("NVIDIA mode"), nvidiaMode,
            temporal, LabelFor("Inference"), resolution,
            LabelFor("Hotness"), hotness]);
        layout.Controls.Add(options, 0, 1);

        GroupBox clipTypeGroup = new()
        {
            Text = "Clip types", Dock = DockStyle.Fill, AutoSize = true,
            Margin = new Padding(0, 0, 0, 6), Padding = new Padding(8, 4, 8, 5)
        };
        FlowLayoutPanel clipTypeRow = new()
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 0)
        };
        foreach (string type in CustomShowStore.ClipTypeOptions)
        {
            CheckBox choice = new()
            {
                Text = type, AutoSize = true, Checked = type == "Standing",
                Margin = new Padding(3, 3, 14, 3)
            };
            clipTypes.Add(choice);
            clipTypeRow.Controls.Add(choice);
        }
        clipTypeGroup.Controls.Add(clipTypeRow);
        layout.Controls.Add(clipTypeGroup, 0, 2);

        GroupBox batchGroup = new()
        {
            Text = "Batch edit selected rows", Dock = DockStyle.Fill,
            AutoSize = true, Margin = new Padding(0, 0, 0, 6),
            Padding = new Padding(8, 4, 8, 5)
        };
        TableLayoutPanel batchLayout = new()
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            RowCount = 2, Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 0)
        };
        FlowLayoutPanel selectionActions = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            Margin = Padding.Empty
        };
        FlowLayoutPanel modelActions = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            Margin = Padding.Empty
        };
        Button applyAll = new() { Text = "Apply model to all included",
            AutoSize = true };
        Button newModel = new() { Text = "New model...", AutoSize = true };
        selectionActions.Controls.AddRange([selectionSummary, selectAllRows,
            includeSelected, excludeSelected, clearSelection]);
        modelActions.Controls.AddRange([LabelFor("Model"), bulkModel,
            applySelectedModel, applyAll, newModel]);
        batchLayout.Controls.Add(selectionActions, 0, 0);
        batchLayout.Controls.Add(modelActions, 0, 1);
        batchGroup.Controls.Add(batchLayout);
        layout.Controls.Add(batchGroup, 0, 3);

        ConfigureGrid();
        grid.DataSource = items;
        layout.Controls.Add(grid, 0, 4);

        TableLayoutPanel bottom = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3,
            Margin = new Padding(0, 8, 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        FlowLayoutPanel progressArea = new() { AutoSize = true,
            Dock = DockStyle.Fill, WrapContents = false };
        progressArea.Controls.AddRange([status, progress]);
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(progressArea, 0, 0);
        bottom.Controls.Add(import, 1, 0);
        bottom.Controls.Add(cancel, 2, 0);
        layout.Controls.Add(bottom, 0, 5);
        CancelButton = cancel;

        RefreshModelChoices();
        Shown += async (_, _) => await ScanAsync();
        FormClosing += (_, _) => scanCancellation?.Cancel();
        browse.Click += async (_, _) => await BrowseAsync();
        rescan.Click += async (_, _) => await ScanAsync();
        selectAllRows.Click += (_, _) => grid.SelectAll();
        includeSelected.Click += (_, _) => SetSelectedIncluded(true);
        excludeSelected.Click += (_, _) => SetSelectedIncluded(false);
        applySelectedModel.Click += (_, _) => ApplyModel(selectedOnly: true);
        applyAll.Click += (_, _) => ApplyModel(selectedOnly: false);
        newModel.Click += (_, _) => NewModel();
        clearSelection.Click += (_, _) => grid.ClearSelection();
        import.Click += (_, _) => AddToQueue();
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty) grid.CommitEdit(
                DataGridViewDataErrorContexts.Commit);
        };
        grid.SelectionChanged += (_, _) => UpdateSelectionState();
        grid.CellValueChanged += (_, _) => UpdateImportState();
        grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
        grid.CellBeginEdit += (_, e) =>
        {
            if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is
                NvidiaFolderImportItem item && item.CanInclude) return;
            e.Cancel = true;
        };
        grid.DataBindingComplete += (_, _) => StyleRows();
        grid.DataError += (_, e) => e.ThrowException = false;
        AppTheme.Apply(this);
    }

    static Label LabelFor(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        Margin = new Padding(8, 8, 3, 0)
    };

    void ConfigureGrid()
    {
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(NvidiaFolderImportItem.Included),
            HeaderText = "☐ Include", Width = 82,
            ToolTipText = "Click to include or exclude every eligible row"
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(NvidiaFolderImportItem.Video),
            HeaderText = "Video", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 34
        });
        modelColumn = new DataGridViewComboBoxColumn
        {
            DataPropertyName = nameof(NvidiaFolderImportItem.PerformerId),
            HeaderText = "Model", DisplayMember = nameof(ModelChoice.Name),
            ValueMember = nameof(ModelChoice.Id), Width = 180,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        grid.Columns.Add(modelColumn);
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(NvidiaFolderImportItem.Title),
            HeaderText = "Show name",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 34
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(NvidiaFolderImportItem.Resolution),
            HeaderText = "Resolution", ReadOnly = true, Width = 105
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(NvidiaFolderImportItem.Length),
            HeaderText = "Length", ReadOnly = true, Width = 78
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(NvidiaFolderImportItem.Status),
            HeaderText = "Status", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 23
        });
        for (int index = 0; index < grid.Columns.Count; index++)
        {
            DataGridViewColumn column = grid.Columns[index];
            column.SortMode = index == 0
                ? DataGridViewColumnSortMode.NotSortable
                : DataGridViewColumnSortMode.Programmatic;
        }
    }

    async Task BrowseAsync()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Select a folder to scan recursively for NVIDIA custom shows",
            SelectedPath = folder.Text,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        folder.Text = Path.GetFullPath(dialog.SelectedPath);
        await ScanAsync();
    }

    async Task ScanAsync()
    {
        if (scanning || !Directory.Exists(folder.Text)) return;
        scanning = true;
        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        scanCancellation = new CancellationTokenSource();
        CancellationToken token = scanCancellation.Token;
        SetScanning(true);
        sortProperty = null;
        foreach (DataGridViewColumn column in grid.Columns)
            column.HeaderCell.SortGlyphDirection = SortOrder.None;
        items.Clear();
        try
        {
            (HashSet<string> Catalog, HashSet<string> Queue) existing =
                await Task.Run(() => (
                    NvidiaFolderImporter.ExistingCatalogSourcePaths(store),
                    NvidiaFolderImporter.ExistingQueueSourcePaths(
                        queueManager.Jobs)), token);
            Progress<NvidiaFolderImportProgress> scanProgress = new(value =>
            {
                status.Text = value.Message;
                progress.Maximum = Math.Max(1, value.Total);
                progress.Value = Math.Clamp(value.Completed, 0, progress.Maximum);
            });
            NvidiaFolderScanResult result = await Task.Run(() =>
                NvidiaFolderImporter.Scan(folder.Text, existing.Catalog,
                    existing.Queue, scanProgress, token), token);
            CustomPerformerProfile? match =
                NvidiaFolderImporter.MatchRootProfile(folder.Text, profiles);
            foreach (NvidiaFolderImportItem item in result.Items)
            {
                if (match != null) item.PerformerId = match.Id;
                items.Add(item);
            }
            int ready = items.Count(item => item.CanInclude);
            int disabled = items.Count - ready;
            string warnings = result.Warnings.Count == 0 ? "" :
                $"; {result.Warnings.Count:N0} folders could not be read";
            status.Text = $"{ready:N0} ready, {disabled:N0} excluded{warnings}";
        }
        catch (OperationCanceledException) { status.Text = "Scan cancelled"; }
        catch (Exception error)
        {
            status.Text = "Scan failed";
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            scanning = false;
            SetScanning(false);
            StyleRows();
            UpdateImportState();
        }
    }

    void SetScanning(bool value)
    {
        progress.Visible = value;
        browse.Enabled = rescan.Enabled = import.Enabled = !value;
        grid.Enabled = !value;
    }

    void RefreshModelChoices(string? selectedId = null)
    {
        ModelChoice[] choices = [new("", "(Select model)"),
            .. profiles.OrderBy(value => value.ModelName,
                StringComparer.CurrentCultureIgnoreCase).Select(value =>
                    new ModelChoice(value.Id, value.DisplayName))];
        string current = selectedId ?? (bulkModel.SelectedValue as string ?? "");
        modelColumn.DataSource = choices;
        bulkModel.DataSource = choices;
        bulkModel.DisplayMember = nameof(ModelChoice.Name);
        bulkModel.ValueMember = nameof(ModelChoice.Id);
        bulkModel.SelectedValue = choices.Any(value => value.Id == current)
            ? current : "";
    }

    void ApplyModel(bool selectedOnly)
    {
        NvidiaFolderImportItem[] targets = selectedOnly
            ? grid.SelectedRows.Cast<DataGridViewRow>()
                .Select(row => row.DataBoundItem)
                .OfType<NvidiaFolderImportItem>()
                .Where(item => item.CanInclude).ToArray()
            : items.Where(item => item.CanInclude && item.Included).ToArray();
        if (targets.Length == 0)
        {
            MessageBox.Show(this, selectedOnly
                    ? "Select one or more editable rows first."
                    : "There are no included rows to update.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string id = bulkModel.SelectedValue as string ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            MessageBox.Show(this, "Select a model first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (NvidiaFolderImportItem item in targets)
            item.PerformerId = id;
        grid.Refresh();
        UpdateImportState();
    }

    void NewModel()
    {
        using CustomPerformerForm form = new(null, profiles);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            store.SavePerformer(form.Profile);
            profiles.Add(form.Profile);
            RefreshModelChoices(form.Profile.Id);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SetIncluded(bool included)
    {
        grid.EndEdit();
        foreach (NvidiaFolderImportItem item in items)
            if (item.CanInclude) item.Included = included;
        grid.Refresh();
        UpdateImportState();
    }

    void SetSelectedIncluded(bool included)
    {
        grid.EndEdit();
        foreach (NvidiaFolderImportItem item in grid.SelectedRows
            .Cast<DataGridViewRow>().Select(row => row.DataBoundItem)
            .OfType<NvidiaFolderImportItem>())
            if (item.CanInclude) item.Included = included;
        grid.Refresh();
        UpdateImportState();
    }

    void GridColumnHeaderMouseClick(object? sender,
        DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0) return;
        DataGridViewColumn column = grid.Columns[e.ColumnIndex];
        string property = column.DataPropertyName;
        if (property == nameof(NvidiaFolderImportItem.Included))
        {
            bool include = items.Any(item => item.CanInclude && !item.Included);
            SetIncluded(include);
            return;
        }
        if (string.IsNullOrWhiteSpace(property)) return;
        sortDirection = sortProperty == property &&
            sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        sortProperty = property;
        SortItems(column);
    }

    void SortItems(DataGridViewColumn column)
    {
        grid.EndEdit();
        HashSet<NvidiaFolderImportItem> selected = grid.SelectedRows
            .Cast<DataGridViewRow>().Select(row => row.DataBoundItem)
            .OfType<NvidiaFolderImportItem>().ToHashSet();
        NvidiaFolderImportItem? current = grid.CurrentRow?.DataBoundItem as
            NvidiaFolderImportItem;
        int currentColumn = grid.CurrentCell?.ColumnIndex ?? 0;
        Dictionary<string, string> modelNames = profiles
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.First().DisplayName,
                StringComparer.OrdinalIgnoreCase);
        StringComparer textComparer = StringComparer.CurrentCultureIgnoreCase;
        int Compare(NvidiaFolderImportItem left, NvidiaFolderImportItem right)
        {
            int result = column.DataPropertyName switch
            {
                nameof(NvidiaFolderImportItem.Video) =>
                    textComparer.Compare(left.Video, right.Video),
                nameof(NvidiaFolderImportItem.PerformerId) =>
                    textComparer.Compare(
                        modelNames.GetValueOrDefault(left.PerformerId, ""),
                        modelNames.GetValueOrDefault(right.PerformerId, "")),
                nameof(NvidiaFolderImportItem.Title) =>
                    textComparer.Compare(left.Title, right.Title),
                nameof(NvidiaFolderImportItem.Resolution) => CompareResolution(
                    left, right),
                nameof(NvidiaFolderImportItem.Length) =>
                    left.DurationMs.CompareTo(right.DurationMs),
                nameof(NvidiaFolderImportItem.Status) =>
                    textComparer.Compare(left.Status, right.Status),
                _ => 0
            };
            if (result == 0)
                result = textComparer.Compare(left.Video, right.Video);
            int sign = Math.Sign(result);
            return sortDirection == ListSortDirection.Descending ? -sign : sign;
        }
        List<NvidiaFolderImportItem> sorted = [.. items];
        sorted.Sort(Compare);
        items.RaiseListChangedEvents = false;
        try
        {
            items.Clear();
            foreach (NvidiaFolderImportItem item in sorted) items.Add(item);
        }
        finally
        {
            items.RaiseListChangedEvents = true;
            items.ResetBindings();
        }
        foreach (DataGridViewColumn candidate in grid.Columns)
            candidate.HeaderCell.SortGlyphDirection = ReferenceEquals(
                candidate, column)
                    ? sortDirection == ListSortDirection.Ascending
                        ? SortOrder.Ascending : SortOrder.Descending
                    : SortOrder.None;
        DataGridViewRow? currentRow = null;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.DataBoundItem is not NvidiaFolderImportItem item) continue;
            if (ReferenceEquals(item, current)) currentRow = row;
        }
        if (currentRow != null)
            grid.CurrentCell = currentRow.Cells[Math.Clamp(currentColumn, 0,
                grid.Columns.Count - 1)];
        grid.ClearSelection();
        foreach (DataGridViewRow row in grid.Rows)
            if (row.DataBoundItem is NvidiaFolderImportItem item)
                row.Selected = selected.Contains(item);
        StyleRows();
        UpdateSelectionState();
    }

    static int CompareResolution(NvidiaFolderImportItem left,
        NvidiaFolderImportItem right)
    {
        long leftPixels = (long)left.Width * left.Height;
        long rightPixels = (long)right.Width * right.Height;
        int result = leftPixels.CompareTo(rightPixels);
        if (result != 0) return result;
        result = left.Width.CompareTo(right.Width);
        return result != 0 ? result : left.Height.CompareTo(right.Height);
    }

    void UpdateSelectionState()
    {
        int selected = grid.SelectedRows.Count;
        int editable = grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<NvidiaFolderImportItem>().Count(item => item.CanInclude);
        selectionSummary.Text = selected == editable
            ? $"{selected:N0} selected"
            : $"{selected:N0} selected ({editable:N0} editable)";
        selectAllRows.Enabled = !scanning && items.Count > 0;
        includeSelected.Enabled = editable > 0;
        excludeSelected.Enabled = editable > 0;
        applySelectedModel.Enabled = editable > 0;
        clearSelection.Enabled = selected > 0;
    }

    void StyleRows()
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.DataBoundItem is not NvidiaFolderImportItem item) continue;
            row.ReadOnly = !item.CanInclude;
            row.DefaultCellStyle.ForeColor = item.CanInclude
                ? grid.DefaultCellStyle.ForeColor : SystemColors.GrayText;
            row.DefaultCellStyle.BackColor = item.CanInclude
                ? grid.DefaultCellStyle.BackColor : SystemColors.Control;
        }
    }

    void UpdateImportState()
    {
        if (scanning) return;
        grid.EndEdit();
        int eligibleCount = items.Count(item => item.CanInclude);
        NvidiaFolderImportItem[] included = items.Where(item =>
            item.CanInclude && item.Included).ToArray();
        if (grid.Columns.Count > 0)
            grid.Columns[0].HeaderText = included.Length == 0 ? "☐ Include" :
                included.Length == eligibleCount ? "☑ Include" : "◩ Include";
        import.Text = included.Length == 0 ? "Add to Queue" :
            $"Add {included.Length:N0} to Queue";
        import.Enabled = included.Length > 0;
        UpdateSelectionState();
    }

    void AddToQueue()
    {
        grid.EndEdit();
        try
        {
            NvidiaFolderImportItem[] selected = items.Where(item =>
                item.CanInclude && item.Included).ToArray();
            if (selected.Length == 0)
                throw new InvalidDataException(
                    "Include at least one valid video.");
            if (selected.Any(item => string.IsNullOrWhiteSpace(item.Title)))
                throw new InvalidDataException(
                    "Every included video requires a show name.");
            Dictionary<string, CustomPerformerProfile> byId = profiles
                .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            if (selected.Any(item => string.IsNullOrWhiteSpace(item.PerformerId) ||
                    !byId.ContainsKey(item.PerformerId)))
                throw new InvalidDataException(
                    "Assign a model to every included video.");
            HashSet<string> existing = NvidiaFolderImporter.ExistingSourcePaths(
                store, queueManager.Jobs);
            NvidiaFolderImportItem? duplicate = selected.FirstOrDefault(item =>
                existing.Contains(NvidiaFolderImporter.CanonicalPath(
                    item.SourcePath)));
            if (duplicate != null)
                throw new InvalidDataException(
                    $"'{duplicate.Video}' was published or queued after this " +
                    "folder was scanned. Rescan the folder before importing.");
            string[] types = clipTypes.Where(value => value.Checked)
                .Select(value => value.Text).ToArray();
            if (types.Length == 0)
                throw new InvalidDataException(
                    "Select at least one clip type.");
            CustomNvidiaSettings settings = new()
            {
                Mode = Math.Max(0, nvidiaMode.SelectedIndex),
                Temporal = temporal.Checked,
                InferenceResolution = resolution.SelectedIndex switch
                {
                    0 => "540p", 2 => "1080p", 3 => "source", _ => "720p"
                }
            };
            string selectedHotness = hotness.SelectedItem?.ToString() ??
                "NoNudity";
            List<CustomShowQueueBatchEntry> entries = selected.Select(item =>
                NvidiaFolderImporter.BuildEntry(item, byId[item.PerformerId],
                    settings, selectedHotness, types, configuration, store))
                .ToList();
            queueManager.AddBatch(entries);
            QueuedCount = entries.Count;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
