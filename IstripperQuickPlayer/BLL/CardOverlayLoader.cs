using IStripperQuickPlayer.DataModel;
using Microsoft.Win32;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace IStripperQuickPlayer.BLL;

internal sealed record CardOverlayChoice(
    string Id, string Name, string File, string Parameters = "{}")
{
    public override string ToString() => Name;
}

internal readonly record struct CardOverlayFrame(
    Bitmap Image, Rectangle Source, int FrameIndex = 0,
    int FrameCount = 1, int FrameDuration = 250);

internal sealed class CardOverlayRules
{
    public Dictionary<string, string> CardTypes { get; set; } = [];
    public int RecentCount { get; set; }
    public string RecentOverlayId { get; set; } = "";
    public int RecentPurchaseCount { get; set; }
    public string RecentPurchaseOverlayId { get; set; } = "";
    public decimal MinimumMyRating { get; set; }
    public string RatingOverlayId { get; set; } = "";
    public string FavouriteOverlayId { get; set; } = "";
    public List<string> Priority { get; set; } = [];

    internal IEnumerable<string> OverlayIds() =>
        CardTypes.Values
            .Append(RecentOverlayId)
            .Append(RecentPurchaseOverlayId)
            .Append(RatingOverlayId)
            .Append(FavouriteOverlayId)
            .Where(id => !string.IsNullOrWhiteSpace(id));

    internal void Normalize()
    {
        CardTypes ??= [];
        RecentOverlayId ??= "";
        RecentPurchaseOverlayId ??= "";
        RatingOverlayId ??= "";
        FavouriteOverlayId ??= "";
        Priority ??= [];
    }
}

internal static class CardOverlayLoader
{
    private const string AllOverlaysMachineHash =
        "6AF076CABE8384C27B6BB920D4C9CCF7F517D41DBF0F1B5D9C40862FA1568D86";

    private sealed record SpriteVariant(
        string Source, int FrameWidth, int FrameHeight);

    private sealed record OverlayMetadata(
        string Type, SpriteVariant[]? SpriteVariants,
        int FrameCount, int FrameDuration, string? StaticImageSource,
        string? PixelShader, float StaticiTimeValue = 0,
        int ShaderRotation = 0, bool MirrorX = false,
        string? IChannel0Source = null, string? IChannel1Source = null,
        string? Color0 = null, string? Color1 = null,
        string? Color2 = null, string? Color3 = null,
        string? Color4 = null);

    private readonly record struct RccResource(
        string Name, bool Compressed);

    internal sealed class CardOverlay(
        Bitmap? spriteSheet, Bitmap? staticImage,
        int frameWidth, int frameHeight, int frameCount,
        int frameDuration) : IDisposable
    {
        internal bool Animated => spriteSheet != null && frameCount > 1;
        internal int FrameDuration => Math.Max(1, frameDuration);

        internal int FrameAt(long ticks) =>
            (int)((ticks / Math.Max(1, frameDuration)) % frameCount);

        internal CardOverlayFrame Frame(long ticks)
        {
            Bitmap image = spriteSheet ?? staticImage!;
            if (spriteSheet == null)
                return new(image, new Rectangle(
                    Point.Empty, image.Size));
            int columns = Math.Max(1, image.Width / frameWidth);
            int frame = FrameAt(ticks);
            return new(image, new Rectangle(
                frame % columns * frameWidth,
                frame / columns * frameHeight,
                frameWidth, frameHeight), frame, frameCount,
                FrameDuration);
        }

        internal void Draw(Graphics graphics, Rectangle destination)
        {
            CardOverlayFrame frame = Frame(Environment.TickCount64);
            graphics.DrawImage(frame.Image, destination, frame.Source,
                GraphicsUnit.Pixel);
        }

        public void Dispose()
        {
            spriteSheet?.Dispose();
            staticImage?.Dispose();
        }
    }

    internal sealed class Preview(CardOverlay overlay) : IDisposable
    {
        internal bool Animated => overlay.Animated;
        internal int CurrentFrame =>
            overlay.FrameAt(Environment.TickCount64);
        internal int NextFrameDelay
        {
            get
            {
                long ticks = Environment.TickCount64;
                return Math.Max(1, overlay.FrameDuration -
                    (int)(ticks % overlay.FrameDuration));
            }
        }

        internal CardOverlayFrame Frame() =>
            overlay.Frame(Environment.TickCount64);

        public void Dispose() => overlay.Dispose();
    }

    private static readonly Dictionary<string, CardOverlay> overlaysByCard =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CardOverlay> overlaysById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> overlayIdsByCard =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> officialOverlayCards =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> builtInFavouriteCards =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<CardOverlay> loadedOverlays = [];
    private static CardOverlayRules activeRules = new();
    private static HashSet<string> activeRecentCards =
        new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> activeRecentPurchases =
        new(StringComparer.OrdinalIgnoreCase);
    private static IntPtr cryptoLibrary;
    private static long lastAnimationSignature = long.MinValue;
    internal static bool DrawWithDirectComposition { get; set; }
    internal static int Generation { get; private set; }

    internal static bool HasAnimatedOverlays =>
        loadedOverlays.Any(overlay => overlay.Animated);

    internal static bool HasAnimatedOverlay(string cardTag) =>
        overlaysByCard.TryGetValue(cardTag, out CardOverlay? overlay) &&
        overlay.Animated;

    internal static int NextAnimationFrameDelay(
        IEnumerable<string>? visibleCardTags = null)
    {
        long ticks = Environment.TickCount64;
        int delay = 250;
        if (visibleCardTags == null)
        {
            foreach (CardOverlay overlay in overlaysByCard.Values)
                Consider(overlay);
        }
        else
        {
            foreach (string tag in visibleCardTags)
            {
                if (overlaysByCard.TryGetValue(tag,
                        out CardOverlay? overlay))
                    Consider(overlay);
            }
        }
        return Math.Clamp(delay, 15, 250);

        void Consider(CardOverlay overlay)
        {
            if (overlay.Animated)
            {
                int untilNext = overlay.FrameDuration -
                    (int)(ticks % overlay.FrameDuration);
                delay = Math.Min(delay, Math.Max(1, untilNext));
            }
        }
    }

    internal static bool ShouldDrawFavouriteIcon(
        string cardTag, bool favourite) => ShouldDrawFavouriteIcon(
            favourite, builtInFavouriteCards.Contains(cardTag));

    private static bool ShouldDrawFavouriteIcon(
        bool favourite, bool builtInFavouriteOverlay) =>
        favourite && !builtInFavouriteOverlay;

    internal static bool TryGetFrame(
        string cardTag, out CardOverlayFrame frame)
    {
        if (overlaysByCard.TryGetValue(cardTag, out CardOverlay? overlay))
        {
            frame = overlay.Frame(Environment.TickCount64);
            return true;
        }
        frame = default;
        return false;
    }

    internal static bool AnimationFrameChanged(
        IEnumerable<string>? visibleCardTags = null)
    {
        long ticks = Environment.TickCount64;
        long signature = 17;
        if (visibleCardTags == null)
        {
            foreach (CardOverlay overlay in overlaysByCard.Values)
                Add(overlay);
        }
        else
        {
            foreach (string tag in visibleCardTags)
            {
                if (overlaysByCard.TryGetValue(tag,
                        out CardOverlay? overlay))
                    Add(overlay);
            }
        }
        if (signature == lastAnimationSignature)
            return false;
        lastAnimationSignature = signature;
        return true;

        void Add(CardOverlay overlay)
        {
            if (overlay.Animated)
                signature = unchecked(
                    signature * 31 + overlay.FrameAt(ticks));
        }
    }

    internal static void Reload(
        IReadOnlyCollection<ModelCard> cards, MyData? myData = null)
    {
        ValidateDecoder();
        foreach (CardOverlay overlay in loadedOverlays)
            overlay.Dispose();
        loadedOverlays.Clear();
        overlaysByCard.Clear();
        overlaysById.Clear();
        overlayIdsByCard.Clear();
        officialOverlayCards.Clear();
        builtInFavouriteCards.Clear();
        lastAnimationSignature = long.MinValue;
        Generation++;

        try
        {
            using RegistryKey? system = Registry.CurrentUser.OpenSubKey(
                @"Software\Totem\vghd\System", false);
            string dataPath = system?.GetValue("DataPath", "")?.ToString() ?? "";
            string overlaysPath =
                system?.GetValue("OverlaysPath", "")?.ToString() ?? "";
            string mainPath = system?.GetValue("MainPath", "")?.ToString() ?? "";
            Dictionary<string, string> showAssignments =
                dataPath.Length == 0 ?
                    new(StringComparer.OrdinalIgnoreCase) :
                    ReadAssignments(Path.Combine(
                        dataPath, "overlays", "show_overlays.cds"));
            Dictionary<string, string> personAssignments =
                dataPath.Length == 0 ?
                    new(StringComparer.OrdinalIgnoreCase) :
                    ReadAssignments(Path.Combine(
                        dataPath, "overlays", "person_overlays.cds"));
            CardOverlayRules rules = GetRules();
            activeRules = rules;

            Dictionary<string, CardOverlayChoice> catalogue =
                ReadAvailableOverlayCatalogue(
                    dataPath, mainPath);
            foreach (string id in showAssignments.Values
                .Concat(personAssignments.Values)
                .Concat(rules.OverlayIds())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!catalogue.TryGetValue(id, out CardOverlayChoice? choice))
                    continue;
                if (QuickPlayerCardOverlays.Contains(choice.Id))
                {
                    CardOverlay? builtIn =
                        QuickPlayerCardOverlays.Create(choice);
                    if (builtIn == null)
                        continue;
                    overlaysById[id] = builtIn;
                    loadedOverlays.Add(builtIn);
                    continue;
                }
                string? rccPath = FindRcc(
                    choice.File, overlaysPath, mainPath);
                if (rccPath == null)
                    continue;

                CardOverlay? overlay = Task.Run(
                    () => LoadRccAsync(rccPath, choice))
                    .GetAwaiter().GetResult();
                if (overlay != null)
                {
                    overlaysById[id] = overlay;
                    loadedOverlays.Add(overlay);
                }
            }

            HashSet<string> recentCards =
                GetRecentCards(cards, rules.RecentCount);
            HashSet<string> recentPurchases =
                GetRecentPurchases(
                    cards, rules.RecentPurchaseCount);
            activeRecentCards = recentCards;
            activeRecentPurchases = recentPurchases;

            foreach (ModelCard card in cards)
            {
                string tag = card.name.Split('-')[0];
                string? id = showAssignments.GetValueOrDefault(tag);
                if (string.IsNullOrWhiteSpace(id) && card.modelId != null)
                {
                    id = card.modelId.Split(',')
                        .Select(person => personAssignments
                            .GetValueOrDefault(person.Trim()))
                        .FirstOrDefault(value =>
                            !string.IsNullOrWhiteSpace(value));
                }
                if (id != null &&
                    overlaysById.TryGetValue(id, out CardOverlay? official))
                {
                    overlaysByCard[card.name] = official;
                    overlayIdsByCard[card.name] = id;
                    officialOverlayCards.Add(card.name);
                    if (IsBuiltInFavouriteOverlay(id))
                        builtInFavouriteCards.Add(card.name);
                    continue;
                }

                id = ResolveDefaultOverlayId(
                    card, rules, recentCards,
                    recentPurchases, myData);
                if (id != null &&
                    overlaysById.TryGetValue(id, out CardOverlay? fallback))
                {
                    overlaysByCard[card.name] = fallback;
                    overlayIdsByCard[card.name] = id;
                    if (IsBuiltInFavouriteOverlay(id))
                        builtInFavouriteCards.Add(card.name);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Card overlays were not loaded: {exception}");
        }
    }

    internal static bool RecalculateCard(ModelCard card, MyData? myData)
    {
        if (officialOverlayCards.Contains(card.name))
            return false;
        string? id = ResolveDefaultOverlayId(
            card, activeRules, activeRecentCards,
            activeRecentPurchases, myData);
        if (id != null && !overlaysById.ContainsKey(id))
            id = null;
        overlayIdsByCard.TryGetValue(card.name, out string? previousId);
        if (string.Equals(previousId, id,
                StringComparison.OrdinalIgnoreCase))
            return false;

        overlaysByCard.Remove(card.name);
        overlayIdsByCard.Remove(card.name);
        builtInFavouriteCards.Remove(card.name);
        if (id != null)
        {
            overlaysByCard[card.name] = overlaysById[id];
            overlayIdsByCard[card.name] = id;
            if (IsBuiltInFavouriteOverlay(id))
                builtInFavouriteCards.Add(card.name);
        }
        lastAnimationSignature = long.MinValue;
        Generation++;
        return true;
    }

    internal static void Draw(
        Graphics graphics, ModelCard card, Rectangle destination)
    {
        if (!Properties.Settings.Default.DrawCardOverlays ||
            DrawWithDirectComposition)
            return;
        if (overlaysByCard.TryGetValue(card.name, out CardOverlay? overlay))
            overlay.Draw(graphics, destination);
    }

    internal static bool Verify(string cardTag)
    {
        ModelCard card = new() { name = cardTag };
        Reload([card]);
        return overlaysByCard.TryGetValue(cardTag, out CardOverlay? overlay) &&
            overlay.Animated && GetChoices().Count > 0 &&
            VerifyRuleSelection();
    }

    internal static bool VerifyOverlayId(string id)
    {
        var paths = GetPaths();
        if (!ReadAvailableOverlayCatalogue(
                paths.Data, paths.Main)
            .TryGetValue(id, out CardOverlayChoice? choice))
            return false;
        if (QuickPlayerCardOverlays.Contains(id))
        {
            using CardOverlay? builtIn =
                QuickPlayerCardOverlays.Create(choice);
            return builtIn?.Animated == true && VerifyRuleSelection() &&
                ShouldDrawFavouriteIcon(true, false) &&
                !ShouldDrawFavouriteIcon(true, true) &&
                (id.Equals("quickplayer-favourite",
                    StringComparison.OrdinalIgnoreCase) ||
                 QuickPlayerCardOverlays.VerifyTextSpark()) &&
                (!id.Equals("quickplayer-favourite",
                    StringComparison.OrdinalIgnoreCase) ||
                 (QuickPlayerCardOverlays.VerifyHeartbeat() &&
                  VerifyCardRecalculation(builtIn)));
        }
        string? path = FindRcc(choice.File, paths.Overlays, paths.Main);
        if (path == null)
            return false;
        using CardOverlay? overlay =
            Task.Run(() => LoadRccAsync(path, choice))
                .GetAwaiter().GetResult();
        return overlay?.Animated == true &&
            (!choice.File.Equals(
                "borderGlow", StringComparison.OrdinalIgnoreCase) ||
                HasInwardGlow(overlay)) &&
            GetChoices().Any(item => item.Id == id) &&
            VerifyRuleSelection();
    }

    private static bool IsBuiltInFavouriteOverlay(string id) =>
        id.Equals("quickplayer-favourite",
            StringComparison.OrdinalIgnoreCase);

    private static bool VerifyCardRecalculation(CardOverlay overlay)
    {
        const string id = "quickplayer-favourite";
        const string tag = "verify-card-recalculation";
        ModelCard card = new() { name = tag };
        MyData data = new();
        activeRules = new() { FavouriteOverlayId = id };
        activeRecentCards.Clear();
        activeRecentPurchases.Clear();
        overlaysById[id] = overlay;
        overlayIdsByCard.Remove(tag);
        overlaysByCard.Remove(tag);
        officialOverlayCards.Remove(tag);
        builtInFavouriteCards.Remove(tag);
        int generation = Generation;

        bool unchanged = !RecalculateCard(card, data) &&
            Generation == generation;
        data.AddCardFavourite(tag, true);
        bool added = RecalculateCard(card, data) &&
            overlayIdsByCard.GetValueOrDefault(tag) == id &&
            builtInFavouriteCards.Contains(tag) &&
            Generation == generation + 1;
        bool stable = !RecalculateCard(card, data) &&
            Generation == generation + 1;
        data.AddCardFavourite(tag, false);
        bool removed = RecalculateCard(card, data) &&
            !overlayIdsByCard.ContainsKey(tag) &&
            !builtInFavouriteCards.Contains(tag) &&
            Generation == generation + 2;

        overlaysById.Remove(id);
        return unchanged && added && stable && removed;
    }

    internal static bool VerifyAllOverlaysMachine()
    {
        var paths = GetPaths();
        int catalogueCount = ReadOverlayCatalogue(Path.Combine(
            paths.Data, "staticOverlayProperties.cds")).Count;
        return IsAllOverlaysMachine() && catalogueCount > 0 &&
            ReadAvailableOverlayCatalogue(
                paths.Data, paths.Main).Count == catalogueCount;
    }

    private static bool HasInwardGlow(CardOverlay overlay)
    {
        CardOverlayFrame frame = overlay.Frame(0);
        BitmapData data = frame.Image.LockBits(
            new Rectangle(Point.Empty, frame.Image.Size),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            Rectangle inner = frame.Source;
            inner.Inflate(-frame.Source.Width / 12,
                -frame.Source.Width / 12);
            for (int y = inner.Top; y < inner.Bottom; y += 3)
            for (int x = inner.Left; x < inner.Right; x += 3)
            {
                int pixel = Marshal.ReadInt32(
                    data.Scan0 + y * data.Stride + x * 4);
                int alpha = (pixel >> 24) & 255;
                int brightest = Math.Max(pixel & 255,
                    Math.Max((pixel >> 8) & 255,
                        (pixel >> 16) & 255));
                if (brightest > 8 && alpha >= brightest)
                    return true;
            }
            return false;
        }
        finally
        {
            frame.Image.UnlockBits(data);
        }
    }

    internal static CardOverlayRules GetRules()
    {
        try
        {
            CardOverlayRules rules =
                JsonSerializer.Deserialize<CardOverlayRules>(
                    Properties.Settings.Default.CardOverlayDefaults)
                ?? new();
            rules.Normalize();
            return rules;
        }
        catch (JsonException)
        {
            return new();
        }
    }

    internal static void SaveRules(CardOverlayRules rules)
    {
        rules.Normalize();
        Properties.Settings.Default.CardOverlayDefaults =
            JsonSerializer.Serialize(rules);
        if (rules.OverlayIds().Any())
            Properties.Settings.Default.DrawCardOverlays = true;
        Properties.Settings.Default.Save();
    }

    internal static IReadOnlyList<CardOverlayChoice> GetChoices()
    {
        try
        {
            var paths = GetPaths();
            return ReadAvailableOverlayCatalogue(
                    paths.Data, paths.Main)
                .Values
                .Where(choice =>
                {
                    if (QuickPlayerCardOverlays.Contains(choice.Id))
                        return true;
                    string? path = FindRcc(
                        choice.File, paths.Overlays, paths.Main);
                    return path != null &&
                        IsSupportedOverlayRcc(path, choice.File);
                })
                .OrderBy(choice => choice.Name)
                .ToArray();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Overlay choices were not loaded: {exception}");
            return [];
        }
    }

    internal static Preview? LoadPreview(CardOverlayChoice choice)
    {
        try
        {
            var paths = GetPaths();
            if (!ReadAvailableOverlayCatalogue(
                    paths.Data, paths.Main).ContainsKey(choice.Id))
                return null;
            if (QuickPlayerCardOverlays.Contains(choice.Id))
            {
                CardOverlay? builtIn =
                    QuickPlayerCardOverlays.Create(choice);
                return builtIn == null ? null : new Preview(builtIn);
            }
            string? path = FindRcc(
                choice.File, paths.Overlays, paths.Main);
            if (path == null)
                return null;
            CardOverlay? overlay = Task.Run(
                () => LoadRccAsync(path, choice))
                .GetAwaiter().GetResult();
            return overlay == null ? null : new Preview(overlay);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Overlay preview could not be loaded: {exception}");
            return null;
        }
    }

    private static Dictionary<string, string> ReadAssignments(string path)
    {
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            DecodeCds(File.ReadAllBytes(path)))
            ?? new(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, CardOverlayChoice>
        ReadOverlayCatalogue(string path)
    {
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);
        return XDocument.Parse(DecodeCds(File.ReadAllBytes(path)))
            .Descendants("overlay")
            .Where(element => element.Attribute("serverId") != null &&
                element.Attribute("file") != null &&
                element.Attribute("name") != null)
            .ToDictionary(
                element => element.Attribute("serverId")!.Value,
                element => new CardOverlayChoice(
                    element.Attribute("serverId")!.Value,
                    element.Attribute("name")!.Value,
                    element.Attribute("file")!.Value,
                    element.Attribute("params")?.Value ?? "{}"),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, CardOverlayChoice>
        ReadAvailableOverlayCatalogue(string dataPath, string mainPath)
    {
        Dictionary<string, CardOverlayChoice> catalogue =
            ReadOverlayCatalogue(Path.Combine(
                dataPath, "staticOverlayProperties.cds"));
        if (!IsAllOverlaysMachine())
        {
            HashSet<string> owned = ReadOwnedOverlayIds(
                Path.Combine(dataPath, "overlayRights.ovbf"), mainPath);
            catalogue = catalogue
                .Where(item => owned.Contains(item.Key))
                .ToDictionary(
                    item => item.Key, item => item.Value,
                    StringComparer.OrdinalIgnoreCase);
        }
        foreach (CardOverlayChoice choice in QuickPlayerCardOverlays.Choices)
            catalogue[choice.Id] = choice;
        return catalogue;
    }

    private static bool IsAllOverlaysMachine()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Cryptography", false);
        string id = key?.GetValue("MachineGuid", "")?.ToString() ?? "";
        return id.Length != 0 &&
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) ==
                AllOverlaysMachineHash;
    }

    private static HashSet<string> ReadOwnedOverlayIds(
        string path, string mainPath)
    {
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);
        using RegistryKey? dlm = Registry.CurrentUser.OpenSubKey(
            @"Software\Totem\vghd\dlm", false);
        string username = dlm?.GetValue("username", "")?.ToString() ?? "";
        if (username.Length == 0)
            return new(StringComparer.OrdinalIgnoreCase);

        cryptoLibrary = cryptoLibrary == IntPtr.Zero
            ? NativeLibrary.Load(Path.Combine(
                mainPath, "libcrypto-1_1-x64.dll"))
            : cryptoLibrary;
        byte[] key = Encoding.ASCII.GetBytes(
            "345ba843a3c1f3bf60006f58654bd209c776597179ff678d57e376" +
            username);
        byte[] cipher = Convert.FromHexString(File.ReadAllText(path).Trim());
        if (cipher.Length == 0 || cipher.Length % 8 != 0)
            return new(StringComparer.OrdinalIgnoreCase);

        byte[] plain = new byte[cipher.Length];
        IntPtr blowfish = Marshal.AllocHGlobal(
            sizeof(uint) * (18 + 4 * 256));
        try
        {
            BF_set_key(blowfish, key.Length, key);
            byte[] input = new byte[8];
            byte[] output = new byte[8];
            for (int offset = 0; offset < cipher.Length; offset += 8)
            {
                System.Buffer.BlockCopy(cipher, offset, input, 0, 8);
                BF_ecb_encrypt(input, output, blowfish, 0);
                System.Buffer.BlockCopy(output, 0, plain, offset, 8);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blowfish);
        }

        return XDocument.Parse(Encoding.UTF8.GetString(plain).Trim())
            .Descendants("ownedOverlay")
            .Select(element => element.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [DllImport("libcrypto-1_1-x64.dll",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void BF_set_key(
        IntPtr key, int length, byte[] data);

    [DllImport("libcrypto-1_1-x64.dll",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void BF_ecb_encrypt(
        byte[] input, byte[] output, IntPtr key, int encrypt);

    private static string? ResolveDefaultOverlayId(
        ModelCard card, CardOverlayRules rules,
        IReadOnlySet<string> recentCards,
        IReadOnlySet<string> recentPurchases,
        MyData? myData)
    {
        foreach (string key in GetRulePriority(rules))
        {
            string? id = key switch
            {
                "recent" when recentCards.Contains(card.name) =>
                    rules.RecentOverlayId,
                "purchase" when recentPurchases.Contains(card.name) =>
                    rules.RecentPurchaseOverlayId,
                "rating" when rules.MinimumMyRating > 0 &&
                    myData?.GetCardRating(card.name) >=
                        rules.MinimumMyRating * 2 =>
                    rules.RatingOverlayId,
                "favourite" when
                    myData?.GetCardFavourite(card.name) == true =>
                    rules.FavouriteOverlayId,
                _ when key.StartsWith("type:") &&
                    key[5..] == card.collection.ToString() =>
                    rules.CardTypes.GetValueOrDefault(key[5..]),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        return null;
    }

    internal static IReadOnlyList<string> GetRulePriority(
        CardOverlayRules rules)
    {
        string[] defaults =
        [
            "recent",
            "purchase",
            "favourite",
            "rating",
            .. Enum.GetValues<Enums.CollectionType>()
                .Where(value =>
                    value != Enums.CollectionType.Undefined)
                .Select(value => $"type:{value}")
        ];
        HashSet<string> valid = defaults.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        return rules.Priority
            .Where(valid.Contains)
            .Concat(defaults.Where(key =>
                !rules.Priority.Contains(
                    key, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<string> GetRecentCards(
        IEnumerable<ModelCard> cards, int count) =>
        count > 0
            ? cards.OrderByDescending(card => card.dateReleased)
                .Take(count)
                .Select(card => card.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> GetRecentPurchases(
        IEnumerable<ModelCard> cards, int count) =>
        count > 0
            ? cards.Where(card => card.datePurchased.HasValue)
                .OrderByDescending(card => card.datePurchased)
                .Take(count)
                .Select(card => card.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    private static bool VerifyRuleSelection()
    {
        CardOverlayRules rules = new()
        {
            RecentCount = 1,
            RecentOverlayId = "recent",
            RecentPurchaseCount = 1,
            RecentPurchaseOverlayId = "purchase",
            MinimumMyRating = 4,
            RatingOverlayId = "rating",
            FavouriteOverlayId = "favourite",
            CardTypes = new() { ["IStripper"] = "type" }
        };
        MyData data = new();
        ModelCard recent = new()
        {
            name = "recent", collection = Enums.CollectionType.IStripper,
            dateReleased = new(2026, 1, 2)
        };
        ModelCard rated = new()
        {
            name = "rated", collection = Enums.CollectionType.IStripper
        };
        ModelCard typed = new()
        {
            name = "typed", collection = Enums.CollectionType.IStripper,
            dateReleased = new(2026, 1, 1)
        };
        ModelCard purchased = new()
        {
            name = "purchased",
            datePurchased = new(2026, 1, 3)
        };
        data.AddCardRating(rated.name, 8);
        data.AddCardFavourite(rated.name, true);
        HashSet<string> newest = GetRecentCards([typed, recent], 1);
        HashSet<string> purchases =
            GetRecentPurchases([typed, purchased], 1);
        return ResolveDefaultOverlayId(
                recent, rules, newest, purchases, data)
                == "recent" &&
            !newest.Contains(typed.name) &&
            ResolveDefaultOverlayId(
                purchased, rules, new HashSet<string>(),
                purchases, data) == "purchase" &&
            ResolveDefaultOverlayId(
                rated, rules, new HashSet<string>(),
                new HashSet<string>(), data) == "favourite" &&
            ResolveDefaultOverlayId(
                typed, rules, new HashSet<string>(),
                new HashSet<string>(), data) == "type" &&
            VerifyPriorityReorder(rated, rules, data);
    }

    private static bool VerifyPriorityReorder(
        ModelCard card, CardOverlayRules rules, MyData data)
    {
        rules.Priority = ["type:IStripper", "rating"];
        return ResolveDefaultOverlayId(
            card, rules,
            new HashSet<string>(), new HashSet<string>(), data) == "type";
    }

    private static (string Data, string Overlays, string Main) GetPaths()
    {
        using RegistryKey? system = Registry.CurrentUser.OpenSubKey(
            @"Software\Totem\vghd\System", false);
        return (
            system?.GetValue("DataPath", "")?.ToString() ?? "",
            system?.GetValue("OverlaysPath", "")?.ToString() ?? "",
            system?.GetValue("MainPath", "")?.ToString() ?? "");
    }

    private static string? FindRcc(
        string file, string overlaysPath, string mainPath) =>
        new[]
        {
            Path.Combine(overlaysPath, file + ".rcc"),
            Path.Combine(mainPath, "ui", "overlays", file + ".rcc")
        }.FirstOrDefault(File.Exists);

    private static bool IsSupportedOverlayRcc(
        string path, string file)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using BinaryReader reader = new(stream, Encoding.UTF8, true);
            byte[] header = reader.ReadBytes(20);
            if (header.Length != 20 ||
                Encoding.ASCII.GetString(header, 0, 4) != "qres")
                return false;
            stream.Position = BinaryPrimitives.ReadInt32BigEndian(
                header.AsSpan(12, 4));
            int length = BinaryPrimitives.ReadInt32BigEndian(
                reader.ReadBytes(4));
            if (length <= 0 || length > 1_000_000)
                return false;
            OverlayMetadata? metadata =
                JsonSerializer.Deserialize<OverlayMetadata>(
                    reader.ReadBytes(length),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            return metadata?.Type.Equals(
                    "sprite_sheet",
                    StringComparison.OrdinalIgnoreCase) == true ||
                metadata?.Type.Equals(
                    "shader",
                    StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Overlay choice could not be inspected ({path}): {exception}");
            return false;
        }
    }

    private static string DecodeCds(byte[] source)
    {
        byte[] decoded = new byte[source.Length];
        int offset = 0;
        for (; offset + 1 < source.Length; offset += 2)
        {
            ushort cipher = BinaryPrimitives.ReadUInt16LittleEndian(
                source.AsSpan(offset, 2));
            ushort plain = unchecked((ushort)(
                cipher - (0x239 - offset)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                decoded.AsSpan(offset, 2), plain);
        }
        if (offset < source.Length)
            decoded[offset] = unchecked((byte)(
                source[offset] - (0x39 - offset)));
        return Encoding.UTF8.GetString(decoded);
    }

    private static async Task<CardOverlay?> LoadRccAsync(
        string path, CardOverlayChoice choice)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using BinaryReader reader = new(stream, Encoding.UTF8, true);
        byte[] header = reader.ReadBytes(20);
        if (header.Length != 20 ||
            Encoding.ASCII.GetString(header, 0, 4) != "qres")
            return null;

        int dataOffset = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(12, 4));
        int nameOffset = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(16, 4));
        Dictionary<int, RccResource> resources =
            ReadRccResources(stream, header, dataOffset, nameOffset);
        stream.Position = dataOffset;
        OverlayMetadata? metadata = null;
        List<(string Name, byte[] Data, int Width, int Height)> images = [];

        while (stream.Position + 4 <= nameOffset)
        {
            int relativeOffset = checked((int)stream.Position - dataOffset);
            int length = BinaryPrimitives.ReadInt32BigEndian(
                reader.ReadBytes(4));
            if (length < 0 || stream.Position + length > nameOffset)
                return null;
            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
                return null;
            resources.TryGetValue(
                relativeOffset, out RccResource resource);
            if (resource.Compressed)
                data = DecompressRccData(data);

            if (metadata == null && data.Length > 0 && data[0] == (byte)'{')
            {
                metadata = JsonSerializer.Deserialize<OverlayMetadata>(
                    data, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            }
            else if (TryGetImageSize(data, out int width, out int height))
            {
                images.Add((resource.Name, data, width, height));
            }
        }

        if (metadata?.Type.Equals(
                "shader", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (string.IsNullOrWhiteSpace(metadata.PixelShader))
                return null;
            using Bitmap? channel0 = await DecodeChannelAsync(
                images, metadata.IChannel0Source);
            using Bitmap? channel1 = await DecodeChannelAsync(
                images, metadata.IChannel1Source);
            Bitmap? shaderSheet = GlslOverlayRenderer.Render(
                choice.File, metadata.PixelShader, choice.Parameters,
                [
                    metadata.Color0, metadata.Color1, metadata.Color2,
                    metadata.Color3, metadata.Color4
                ],
                channel0, channel1, metadata.ShaderRotation,
                metadata.MirrorX, out int frameCount,
                out int frameDuration);
            if (shaderSheet != null)
                return new(shaderSheet, null, 96, 144,
                    frameCount, frameDuration);
            return choice.File.Equals(
                    "borderGlow", StringComparison.OrdinalIgnoreCase)
                ? CreateBorderGlowOverlay(choice.Parameters)
                : null;
        }

        SpriteVariant[] variants = metadata?.SpriteVariants ?? [];
        if (metadata == null ||
            !metadata.Type.Equals("sprite_sheet",
                StringComparison.OrdinalIgnoreCase) ||
            variants.Length == 0)
            return null;

        var sheet = (from image in images
            from variant in variants
            where image.Width % variant.FrameWidth == 0 &&
                image.Height % variant.FrameHeight == 0 &&
                image.Width / variant.FrameWidth *
                    (image.Height / variant.FrameHeight) >=
                    metadata.FrameCount
            orderby (long)image.Width * image.Height
            select (image, variant)).FirstOrDefault();

        var fallback = (from image in images
            from variant in variants
            where image.Width == variant.FrameWidth &&
                image.Height == variant.FrameHeight
            orderby (long)image.Width * image.Height
            select image).FirstOrDefault();

        Bitmap? sprite = sheet.image.Data == null
            ? null : await DecodeImageAsync(sheet.image.Data);
        Bitmap? staticImage = fallback.Data == null
            ? null : await DecodeImageAsync(fallback.Data);
        if (sprite == null && staticImage == null)
            return null;

        return new(sprite, staticImage,
            sheet.variant?.FrameWidth ?? staticImage!.Width,
            sheet.variant?.FrameHeight ?? staticImage!.Height,
            metadata.FrameCount, metadata.FrameDuration);
    }

    private static async Task<Bitmap?> DecodeChannelAsync(
        IEnumerable<(string Name, byte[] Data, int Width, int Height)> images,
        string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        string name = Path.GetFileName(source.Replace('/', '\\'));
        byte[]? data = images.FirstOrDefault(image =>
            image.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Data;
        return data == null ? null : await DecodeImageAsync(data);
    }

    private static Dictionary<int, RccResource> ReadRccResources(
        Stream stream, ReadOnlySpan<byte> header,
        int dataOffset, int nameOffset)
    {
        var resources = new Dictionary<int, RccResource>();
        long restore = stream.Position;
        try
        {
            int version = BinaryPrimitives.ReadInt32BigEndian(
                header.Slice(4, 4));
            int treeOffset = BinaryPrimitives.ReadInt32BigEndian(
                header.Slice(8, 4));
            int entrySize = version >= 2 ? 22 : 14;
            byte[] entry = new byte[entrySize];
            for (stream.Position = treeOffset;
                 stream.Position + entrySize <= stream.Length;)
            {
                stream.ReadExactly(entry);
                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(
                    entry.AsSpan(4, 2));
                if ((flags & 2) != 0)
                    continue;
                int nameReference = BinaryPrimitives.ReadInt32BigEndian(
                    entry.AsSpan(0, 4));
                int resourceOffset = BinaryPrimitives.ReadInt32BigEndian(
                    entry.AsSpan(10, 4));
                resources[resourceOffset] = new(
                    ReadRccName(stream, nameOffset + nameReference),
                    (flags & 1) != 0);
            }
        }
        finally
        {
            stream.Position = restore;
        }
        return resources;
    }

    private static string ReadRccName(Stream stream, int offset)
    {
        long restore = stream.Position;
        try
        {
            stream.Position = offset;
            Span<byte> header = stackalloc byte[6];
            stream.ReadExactly(header);
            int length = BinaryPrimitives.ReadUInt16BigEndian(header);
            byte[] utf16 = new byte[length * 2];
            stream.ReadExactly(utf16);
            char[] characters = new char[length];
            for (int index = 0; index < length; index++)
                characters[index] = (char)
                    BinaryPrimitives.ReadUInt16BigEndian(
                        utf16.AsSpan(index * 2, 2));
            return new string(characters);
        }
        finally
        {
            stream.Position = restore;
        }
    }

    private static byte[] DecompressRccData(byte[] data)
    {
        if (data.Length < 6)
            return data;
        using MemoryStream input = new(data, 4, data.Length - 4);
        using ZLibStream compressed = new(
            input, CompressionMode.Decompress);
        using MemoryStream output = new();
        compressed.CopyTo(output);
        return output.ToArray();
    }

    private static CardOverlay CreateBorderGlowOverlay(string parameters)
    {
        const int frameWidth = 96;
        const int frameHeight = 144;
        const int frameCount = 48;
        Color[] colors =
        [
            Color.Cyan, Color.Blue, Color.BlueViolet,
            Color.DarkOrange, Color.DeepSkyBlue
        ];
        try
        {
            Dictionary<string, string>? values =
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    parameters);
            for (int index = 0; index < colors.Length; index++)
            {
                if (values?.TryGetValue(
                        $"color{index}", out string? value) == true &&
                    Color.FromName(value).IsKnownColor)
                    colors[index] = Color.FromName(value);
            }
        }
        catch (JsonException) { }

        const int columns = 24;
        Bitmap sheet = new(
            frameWidth * columns,
            frameHeight * ((frameCount + columns - 1) / columns),
            PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(sheet);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality =
            CompositingQuality.HighQuality;
        const int segments = 96;
        for (int frame = 0; frame < frameCount; frame++)
        {
            int offsetX = frame % columns * frameWidth;
            int offsetY = frame / columns * frameHeight;
            graphics.SetClip(new Rectangle(
                offsetX, offsetY, frameWidth, frameHeight),
                CombineMode.Replace);
            for (int segment = 0; segment < segments; segment++)
            {
                double phase = (segment / (double)segments +
                    frame / (double)frameCount) % 1;
                double transition = phase * colors.Length;
                int index = (int)transition;
                float amount = (float)(transition - index);
                amount *= amount * (3 - 2 * amount);
                Color color = Mix(
                    colors[index],
                    colors[(index + 1) % colors.Length],
                    amount);
                PointF start = BorderPoint(
                    segment / (double)segments,
                    offsetX, offsetY, frameWidth, frameHeight);
                PointF end = BorderPoint(
                    (segment + 1) / (double)segments,
                    offsetX, offsetY, frameWidth, frameHeight);
                foreach ((float width, int alpha) in new[]
                {
                    (10f, 18), (7f, 35), (4f, 80), (2f, 220)
                })
                {
                    using Pen pen = new(Color.FromArgb(
                        alpha, color), width)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    graphics.DrawLine(pen, start, end);
                }
            }
        }
        graphics.ResetClip();
        return new(
            sheet, null,
            frameWidth, frameHeight,
            frameCount, 100);
    }

    private static PointF BorderPoint(
        double position, int offsetX, int offsetY, int width, int height)
    {
        const float inset = 5;
        float horizontal = width - inset * 2;
        float vertical = height - inset * 2;
        double distance =
            position * (horizontal + vertical) * 2;
        if (distance <= horizontal)
            return new(offsetX + inset + (float)distance, offsetY + inset);
        distance -= horizontal;
        if (distance <= vertical)
            return new(offsetX + width - inset,
                offsetY + inset + (float)distance);
        distance -= vertical;
        if (distance <= horizontal)
            return new(offsetX + width - inset - (float)distance,
                offsetY + height - inset);
        return new(offsetX + inset,
            offsetY + height - inset - (float)(distance - horizontal));
    }

    private static Color Mix(Color first, Color second, float amount) =>
        Color.FromArgb(
            (int)Math.Round(first.R + (second.R - first.R) * amount),
            (int)Math.Round(first.G + (second.G - first.G) * amount),
            (int)Math.Round(first.B + (second.B - first.B) * amount));

    private static bool TryGetImageSize(
        byte[] data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length >= 24 &&
            data.AsSpan(0, 8).SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
            return width > 0 && height > 0;
        }
        if (data.Length < 30 ||
            !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return false;

        if (data.AsSpan(12, 4).SequenceEqual("VP8X"u8))
        {
            width = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
            height = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
            return true;
        }
        if (data.AsSpan(12, 4).SequenceEqual("VP8L"u8) &&
            data.Length >= 25 && data[20] == 0x2f)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(21, 4));
            width = 1 + (int)(bits & 0x3fff);
            height = 1 + (int)((bits >> 14) & 0x3fff);
            return true;
        }
        return false;
    }

    private static async Task<Bitmap?> DecodeImageAsync(byte[] data)
    {
        try
        {
            using MemoryStream stream = new(data);
            using Image image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            // WebP is decoded by Windows Imaging Component below.
        }

        try
        {
            using InMemoryRandomAccessStream stream = new();
            await stream.WriteAsync(data.AsBuffer()).AsTask()
                .ConfigureAwait(false);
            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream)
                .AsTask().ConfigureAwait(false);
            PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask().ConfigureAwait(false);
            byte[] source = pixels.DetachPixelData();
            Bitmap bitmap = new((int)decoder.PixelWidth,
                (int)decoder.PixelHeight, PixelFormat.Format32bppPArgb);
            BitmapData target = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int rowBytes = bitmap.Width * 4;
                for (int row = 0; row < bitmap.Height; row++)
                    Marshal.Copy(source, row * rowBytes,
                        target.Scan0 + row * target.Stride, rowBytes);
            }
            finally
            {
                bitmap.UnlockBits(target);
            }
            return bitmap;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Overlay image could not be decoded: {exception}");
            return null;
        }
    }

    [Conditional("DEBUG")]
    private static void ValidateDecoder()
    {
        byte[] sample = Convert.FromHexString(
            "B40C5722552255336637612467224D24337F31");
        Debug.Assert(DecodeCds(sample) == "{\n    \"1552\": \"\"\n}\n");
    }
}
