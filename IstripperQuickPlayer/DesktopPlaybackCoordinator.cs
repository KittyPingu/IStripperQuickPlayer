namespace IStripperQuickPlayer;

internal sealed record PlayQueueEntry(string CardTag, string? ClipName = null);

// All transitions are reduced on the UI thread; transport only publishes observations.
internal sealed class DesktopPlaybackCoordinator
{
    internal sealed record ClipPolicy(string Types, bool Public, bool NoNudity,
        bool Topless, bool Nudity, bool FullNudity, bool Xxx, bool Demo, long MinimumSizeMb);
    internal sealed record Selection(long Id, string Path, object? Reservation,
        bool Prepared, long Instance);
    internal sealed record Observation(long Attachment, ulong Sequence,
        ulong Instance, ulong CompletedInstance, string Path, int State,
        int Elapsed, int Duration, ulong Request = 0);

    internal long Attachment { get; private set; }
    internal Observation? Confirmed { get; private set; }
    internal Selection? Pending { get; private set; }
    internal Selection? Prepared { get; private set; }
    long nextRequest;
    ulong lastCompletion;
    ulong lastPlaybackInstance;
    internal PlayQueueEntry? ActiveQueuedCard;
    internal PlayQueueEntry? ActiveManualQueueEntry;
    internal PlayQueueEntry? ActiveAutomaticQueueEntry;
    internal long ActiveQueuedCardStartedAt = -1;
    internal string ActiveQueuedCardLastAnimationPath = "";
    internal bool ActiveQueuedCardUsesSmartRules;
    internal long ActiveUnqueuedCardStartedAt = -1;
    internal string ActiveUnqueuedCardTag = "";
    internal bool ActiveUnqueuedCardSequential;

    internal void Attach(long attachment)
    {
        Attachment = attachment;
        Confirmed = null;
        Pending = Prepared = null;
        lastCompletion = 0;
        lastPlaybackInstance = 0;
    }

    internal Selection Request(string path, object? reservation = null, Observation? baseline = null)
    {
        // Custom playback can leave the last observed native movie behind the cached snapshot.
        if (baseline != null) Observe(baseline, out _, out _, out _);
        Prepared = null;
        return Pending = new(++nextRequest, path, reservation, false,
            (long)lastPlaybackInstance);
    }

    internal Selection Prepare(string path, object? reservation)
    {
        return Prepared = new(++nextRequest, path, reservation, true,
            (long)lastPlaybackInstance);
    }

    internal void Cancel(long id)
    {
        if (Pending?.Id == id) Pending = null;
        if (Prepared?.Id == id) Prepared = null;
    }

    internal Selection? ConfirmCustom(long id, string path)
    {
        if (Pending?.Id != id || !string.Equals(Pending.Path, path,
                StringComparison.OrdinalIgnoreCase)) return null;
        Selection accepted = Pending;
        Pending = null;
        return accepted;
    }

    internal long NextCommandId() => ++nextRequest;

    internal static bool CardSessionContinues(long startedAt, long now, int durationMinutes) =>
        startedAt >= 0 && durationMinutes > 0 && now - startedAt <= durationMinutes * 60_000L;

    internal bool Observe(Observation observation, out Selection? accepted,
        out bool manual, out bool completed)
    {
        accepted = null;
        manual = completed = false;
        if (observation.Attachment != Attachment ||
            observation.Sequence <= (Confirmed?.Sequence ?? 0)) return false;
        Observation? previous = Confirmed;
        Confirmed = observation;
        completed = previous != null && observation.CompletedInstance > lastCompletion;
        lastCompletion = Math.Max(lastCompletion, observation.CompletedInstance);
        // Temporary unavailability is not a playback transition.
        ulong previousInstance = lastPlaybackInstance;
        bool changed = observation.Instance != 0 &&
            observation.Instance != previousInstance &&
            observation.Path.Length != 0 && observation.State is 3 or 4;
        if (!changed) return true;
        lastPlaybackInstance = observation.Instance;
        Selection? selection = Pending ?? Prepared;
        if (selection != null && observation.Request != 0 && observation.Request < (ulong)selection.Id)
            return true;
        if (selection != null && string.Equals(selection.Path, observation.Path,
                StringComparison.OrdinalIgnoreCase) &&
            observation.Request == (ulong)selection.Id &&
            observation.Instance != (ulong)selection.Instance)
            accepted = selection;
        else if (selection == Prepared && selection != null &&
            previousInstance != 0 && observation.CompletedInstance == previousInstance &&
            observation.Request == 0)
            // The host's own next movie is not an explicit takeover at a natural boundary.
            return true;
        else manual = previousInstance != 0 &&
            observation.CompletedInstance != previousInstance;
        Pending = Prepared = null;
        return true;
    }

    internal static bool Verify()
    {
        var player = new DesktopPlaybackCoordinator();
        if (!CardSessionContinues(100, 60_100, 1) || CardSessionContinues(100, 60_101, 1) ||
            CardSessionContinues(-1, 0, 1) || CardSessionContinues(0, 0, 0)) return false;
        player.Attach(1);
        Observation O(ulong sequence, ulong instance, string path,
            ulong completed = 0, long attachment = 1, ulong request = 0) =>
            new(attachment, sequence, instance, completed, path, 3, 0, 1000, request);
        if (!player.Observe(O(1, 1, "a"), out _, out _, out _)) return false;
        object token = new();
        var first = player.Request("b", token);
        var second = player.Request("c", token);
        player.Cancel(first.Id);
        if (player.Pending != second) return false;
        if (!player.Observe(O(2, 2, "b", request: (ulong)first.Id), out var accepted, out var manual, out _) ||
            accepted != null || manual || player.Pending != second) return false;
        if (!player.Observe(O(3, 3, "c", request: (ulong)second.Id), out accepted, out manual,
                out _) || accepted != second || manual) return false;
        var replay = player.Request("c", token);
        if (!player.Observe(O(4, 4, "c", request: (ulong)replay.Id), out accepted, out _, out _) ||
            accepted != replay) return false;
        player.Prepare("d", token);
        if (!player.Observe(O(5, 5, "manual"), out accepted, out manual,
                out _) || accepted != null || !manual ||
            player.Prepared != null) return false;
        if (!player.Observe(O(6, 5, "manual", 5), out _, out _, out var ended) ||
            !ended || !player.Observe(O(7, 5, "manual", 5), out _, out _,
                out ended) || ended) return false;
        if (player.Observe(O(5, 2, "stale"), out _, out _, out _)) return false;
        var natural = player.Prepare("next", token);
        if (!player.Observe(O(8, 6, "next", 5, request: (ulong)natural.Id),
                out accepted, out manual, out ended) || accepted != natural || manual || ended) return false;
        if (!player.Observe(O(9, 6, "next", 5, request: (ulong)natural.Id),
                out accepted, out _, out _) || accepted != null) return false;
        var failed = player.Request("failed", token);
        player.Cancel(failed.Id);
        if (player.Pending != null || player.Prepared != null) return false;
        var unknown = player.Request("unknown", token);
        if (!player.Observe(O(10, 7, ""), out accepted, out _, out _) ||
            accepted != null || player.Pending != unknown || player.Confirmed?.Path != "") return false;
        var handoff = new DesktopPlaybackCoordinator();
        handoff.Attach(1);
        handoff.Observe(O(1, 1, "finishing"), out _, out _, out _);
        var next = handoff.Prepare("queued", token);
        if (!handoff.Observe(O(2, 0, "", 1), out _, out _, out ended) || !ended ||
            handoff.Confirmed?.Path != "" || handoff.Prepared != next) return false;
        if (!handoff.Observe(O(3, 1, "finishing", 1), out accepted, out manual, out ended) ||
            accepted != null || manual || ended || handoff.Prepared != next) return false;
        if (!handoff.Observe(O(4, 2, "queued", 1, request: (ulong)next.Id),
                out accepted, out manual, out ended) || accepted != next ||
            accepted.Reservation != token || manual || ended) return false;
        handoff.Observe(O(5, 0, "", 1), out _, out _, out _);
        var sameClip = handoff.Request("queued", token);
        if (!handoff.Observe(O(6, 2, "queued", 1), out accepted, out manual, out _) ||
            accepted != null || manual || handoff.Pending != sameClip) return false;
        handoff.Observe(O(7, 0, "", 1), out _, out _, out _);
        if (!handoff.Observe(O(8, 3, "manual", 1), out accepted, out manual, out _) ||
            accepted != null || !manual || handoff.Pending != null) return false;
        var interposed = new DesktopPlaybackCoordinator();
        interposed.Attach(1);
        interposed.Observe(O(1, 10, "finishing"), out _, out _, out _);
        var reserved = interposed.Prepare("queued", token);
        if (!interposed.Observe(O(2, 11, "host-next", 10), out accepted, out manual, out ended) ||
            accepted != null || manual || !ended || interposed.Prepared != reserved) return false;
        var recovery = interposed.Request(reserved.Path, reserved.Reservation);
        if (recovery.Instance != 11 || recovery.Reservation != token ||
            !interposed.Observe(O(3, 12, "queued", 10, request: (ulong)recovery.Id),
                out accepted, out manual, out ended) || accepted != recovery || manual || ended ||
            !interposed.Observe(O(4, 12, "queued", 10, request: (ulong)recovery.Id),
                out accepted, out _, out _) || accepted != null) return false;
        reserved = interposed.Prepare("following", token);
        if (!interposed.Observe(O(5, 13, "explicit", 10), out accepted, out manual, out _) ||
            accepted != null || !manual || interposed.Prepared != null) return false;
        var customHandoff = new DesktopPlaybackCoordinator();
        customHandoff.Attach(1);
        customHandoff.Observe(O(1, 1, "before-custom"), out _, out _, out _);
        var hidden = O(2, 2, "hidden-native", 1);
        var queuedHandoff = customHandoff.Request("after-custom", token, hidden);
        if (customHandoff.Observe(hidden, out _, out _, out _) ||
            !customHandoff.Observe(O(3, 2, "hidden-native", 1), out accepted, out manual, out ended) ||
            accepted != null || manual || ended || customHandoff.Pending != queuedHandoff ||
            !customHandoff.Observe(O(4, 3, "after-custom", 1, request: (ulong)queuedHandoff.Id),
                out accepted, out manual, out ended) || accepted != queuedHandoff ||
            accepted.Reservation != token || manual || ended || customHandoff.Pending != null ||
            !customHandoff.Observe(O(5, 3, "after-custom", 1, request: (ulong)queuedHandoff.Id),
                out accepted, out _, out _) || accepted != null) return false;
        customHandoff.Attach(2);
        hidden = O(1, 4, "hidden-native", attachment: 2);
        queuedHandoff = customHandoff.Request("after-custom", token, hidden);
        if (!customHandoff.Observe(O(2, 4, "hidden-native", attachment: 2),
                out accepted, out manual, out _) || accepted != null || manual ||
            customHandoff.Pending != queuedHandoff ||
            !customHandoff.Observe(O(3, 5, "after-custom", attachment: 2, request: (ulong)queuedHandoff.Id),
                out accepted, out manual, out _) || accepted != queuedHandoff || manual) return false;
        player.Attach(2);
        var custom = player.Request("custom:one", token);
        var replacement = player.Request("custom:two", token);
        if (player.ConfirmCustom(custom.Id, custom.Path) != null ||
            player.Pending != replacement || player.ConfirmCustom(replacement.Id, custom.Path) != null ||
            player.ConfirmCustom(replacement.Id, replacement.Path) != replacement ||
            player.ConfirmCustom(replacement.Id, replacement.Path) != null) return false;
        custom = player.Request("custom:failure", token);
        player.Cancel(custom.Id);
        if (player.ConfirmCustom(custom.Id, custom.Path) != null) return false;
        var cardOwner = new DesktopPlaybackCoordinator();
        cardOwner.Attach(1);
        var cardEntry = new PlayQueueEntry("card");
        cardOwner.ActiveQueuedCard = cardOwner.ActiveAutomaticQueueEntry = cardEntry;
        cardOwner.Observe(O(1, 1, "card/first"), out _, out _, out _);
        var automaticNext = cardOwner.Prepare("other-card", token);
        var nextClip = cardOwner.Request("card/second", token);
        if (!cardOwner.Observe(O(2, 2, "other-card", request: (ulong)automaticNext.Id),
                out accepted, out manual, out _) || accepted != null || manual ||
            cardOwner.Pending != nextClip || cardOwner.ActiveAutomaticQueueEntry != cardEntry ||
            !cardOwner.Observe(O(3, 3, "card/second", request: (ulong)nextClip.Id),
                out accepted, out manual, out _) || accepted != nextClip || accepted.Reservation != token ||
            manual || cardOwner.ActiveQueuedCard != cardEntry) return false;
        var failedNext = cardOwner.Request("card/third", token);
        cardOwner.Cancel(failedNext.Id);
        if (cardOwner.ActiveQueuedCard != cardEntry || cardOwner.ActiveAutomaticQueueEntry != cardEntry)
            return false;
        return !player.Observe(O(7, 5, "old"), out _, out _, out _) &&
            player.Confirmed == null && player.Pending == null;
    }
}
