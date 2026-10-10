using IStripperQuickPlayer.Interop;
using Microsoft.Win32;

namespace IStripperQuickPlayer;

public partial class Form1
{
    async Task<int> RunDesktopControlAsync(Func<int> command)
    {
        try { return await Task.Run(command); }
        catch (Exception exception)
        {
            SetPlaybackStatus(exception.Message);
            return unchecked((int)0x80004005);
        }
    }
    async void ApplyWheelPlayerSize(string path, int mode, int percent)
    {
        try
        {
            int result = await Task.Run(() => SetVghdPlayerSizePercent(mode, percent));
            if (result >= 0)
            {
                RememberManualPlayerSize(path, mode, percent);
                LockStateOverlay.ShowTextForProcess(vghd_procID, $"{percent}%");
            }
            else SetPlaybackStatus($"Player size update failed (0x{result:X8}).");
        }
        catch (Exception exception) { SetPlaybackStatus(exception.Message); }
    }
    readonly DesktopPlaybackCoordinator desktopPlayback = new();
    DesktopBridgeSnapshot? desktopSnapshot;
    Rectangle? lastDesktopPlacement;
    long desktopAttachment;
    int desktopUiUpdatePending;
    DateTime desktopRequestDeadline;
    bool desktopManualTakeover;
    bool desktopWasMoving;
    readonly object unqueuedDesktopContinuation = new();
    sealed record DesktopQueueReservation(PlayQueueEntry Entry, bool Manual,
        bool Continuation, bool SmartRules, bool Requeued = false);

    void StartDesktopObservation(PlaybackBridgeClient client)
    {
        client.Call("IStripperBeginDesktopAttachment");
        long attachment = Interlocked.Increment(ref desktopAttachment);
        Volatile.Write(ref desktopSnapshot, null);
        BeginInvoke((Action)(() =>
        {
            if (attachment != Volatile.Read(ref desktopAttachment)) return;
            desktopPlayback.Attach(attachment);
            lastDesktopPlacement = null;
            ReleaseDesktopReservation();
        }));
        _ = Task.Run(async () =>
        {
            bool discovered = false;
            bool captureArmed = false;
            int discoveryBackoffSeconds = 1;
            DateTime discoverAfter = DateTime.UtcNow.AddSeconds(1);
            ulong lastInstance = 0;
            int checkpointBucket = -1;
            while (!formIsClosing && attachment == Volatile.Read(ref desktopAttachment) && client.IsConnected)
            {
                try
                {
                    // Give native frame capture priority over a bounded late-attach scan.
                    if (!discovered && DateTime.UtcNow >= discoverAfter)
                    {
                        if (Volatile.Read(ref desktopSnapshot)?.Path.Length is not > 0)
                        {
                            discovered = client.Call("IStripperDiscoverMovie") >= 0;
                            discoveryBackoffSeconds = Math.Min(30, discoveryBackoffSeconds * 2);
                            discoverAfter = DateTime.UtcNow.AddSeconds(discoveryBackoffSeconds);
                        }
                        else discovered = true;
                    }
                    byte[] packet = DesktopBridgeSnapshot.CreatePacket(DesktopBridgeSnapshot.PacketSize);
                    if (client.CallRoundTrip("IStripperGetDesktopSnapshot", packet) >= 0 &&
                        attachment == Volatile.Read(ref desktopAttachment))
                    {
                        DesktopBridgeSnapshot snapshot = DesktopBridgeSnapshot.Parse(packet);
                        if (snapshot.Path.Length != 0) discovered = true;
                        else if (!captureArmed)
                        {
                            captureArmed = client.Call("IStripperArmMovieCapture") >= 0;
                        }
                        // Publish confirmed playback before optional checkpoint and speed commands.
                        Volatile.Write(ref desktopSnapshot, snapshot);
                        if (Interlocked.Exchange(ref desktopUiUpdatePending, 1) == 0)
                            BeginInvoke((Action)(() =>
                            {
                                Interlocked.Exchange(ref desktopUiUpdatePending, 0);
                                if (!formIsClosing && attachment == Volatile.Read(ref desktopAttachment))
                                    ApplyDesktopObservation(attachment);
                            }));
                        if (snapshot.Path.Length != 0 && snapshot.Instance != lastInstance)
                        {
                            lastInstance = snapshot.Instance;
                            checkpointBucket = -1;
                            client.Call("IStripperClearAlphaCheckpoints");
                            client.Call("IStripperSetAlphaCheckpointCacheKey",
                                Properties.Settings.Default.EnableAlphaCheckpointCache ? AlphaCheckpointClipKey(snapshot.Path) : 0);
                            if (Math.Abs(requestedPlaybackSpeed - 1) > 0.001)
                                client.Call("IStripperSetPlayRate", unchecked((ulong)BitConverter.DoubleToInt64Bits(requestedPlaybackSpeed)));
                        }
                        if (snapshot.Decoder == 1 && snapshot.SeekReady == 1 && snapshot.Elapsed / 5_000 != checkpointBucket)
                        {
                            if (client.Call("IStripperCaptureAlphaCheckpoint") >= 0)
                                checkpointBucket = snapshot.Elapsed / 5_000;
                            else if (ReferenceEquals(Volatile.Read(ref desktopSnapshot), snapshot))
                                Volatile.Write(ref desktopSnapshot, snapshot with { SeekReady = 0 });
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (attachment == Volatile.Read(ref desktopAttachment))
                        Volatile.Write(ref desktopSnapshot, null);
                    SetPlaybackStatus("Desktop observation failed: " + exception.Message);
                    break;
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
        });
    }

    void ApplyDesktopObservation(long attachment)
    {
        DesktopBridgeSnapshot? snapshot = Volatile.Read(ref desktopSnapshot);
        if (snapshot == null) return;
        if (customPlayer != null)
        {
            if (customIstripperSuspended) SuspendIStripperForCustomPlayback();
            return;
        }
        if (IsRestApiFullscreenActive()) return;
        var observation = new DesktopPlaybackCoordinator.Observation(attachment,
            snapshot.Sequence, snapshot.Instance, snapshot.CompletedInstance,
            snapshot.Path, snapshot.State, snapshot.Elapsed, snapshot.Duration, snapshot.Request);
        if (desktopPlayback.Prepared?.Instance == (long)snapshot.Instance)
            ValidateDesktopQueueReservation();
        var prepared = desktopPlayback.Prepared;
        string previousPath = desktopPlayback.Confirmed?.Path ?? "";
        ulong previousInstance = desktopPlayback.Confirmed?.Instance ?? 0;
        if (!desktopPlayback.Observe(observation, out var accepted, out bool manual, out bool completed)) return;
        if (snapshot.Window != 0 && snapshot.Bounds.Width > 15 && snapshot.Bounds.Height > 15)
            lastDesktopPlacement = snapshot.Bounds;
        if (accepted?.Reservation is DesktopQueueReservation reservation)
            ConfirmDesktopReservation(reservation, accepted.Path);
        else if (manual)
        {
            desktopManualTakeover = true;
            ReleaseDesktopReservation();
            ClearQueuedCardSession();
            ClearUnqueuedCardSession();
            ClearNativeDesktopSelection();
        }
        playbackMovieRegistered = snapshot.Instance != 0 && snapshot.Path.Length != 0 && snapshot.State is 3 or 4;
        playbackLastKnownElapsedMilliseconds = Math.Max(0, snapshot.Elapsed);
        playbackTimelineDurationMilliseconds = Math.Max(0, snapshot.Duration);
        playbackDecoderKind = snapshot.Decoder;
        playbackSeekingSupported = snapshot.Decoder is 1 or 2;
        if (playbackTimelineAnimationPath != snapshot.Path)
        {
            playbackTimelineAnimationPath = snapshot.Path;
            playbackSeekReady = false;
            ResetPlaybackReadinessDiagnostics(snapshot.Path);
            QueueConfiguredPlayerSize(snapshot.Path);
        }
        playbackSeekReady = playbackMovieRegistered && snapshot.SeekReady == 1;
        playbackSeekReadinessMask = snapshot.ReadinessMask;
        if (previousPath != snapshot.Path || previousInstance != snapshot.Instance)
            ShowNowPlaying(snapshot.Path, doWallpaper: true);
        if (desktopWasMoving && !snapshot.Moving) QueueConfiguredPlayerSize(snapshot.Path);
        desktopWasMoving = snapshot.Moving;
        if (accepted != null)
        {
            desktopManualTakeover = false;
            playbackRequestedAnimationPath = "";
            playbackRequestedAnimationAt = DateTime.MinValue;
            SelectQueuedCard(GetCardTagFromAnimationPath(accepted.Path), accepted.Path);
        }
        if (desktopPlayback.Pending != null && DateTime.UtcNow >= desktopRequestDeadline)
        {
            desktopPlayback.Cancel(desktopPlayback.Pending.Id);
            ReleaseDesktopReservation();
            playbackRequestedAnimationPath = "";
            SetPlaybackStatus("Playback was not confirmed. The queue item has been retained.");
        }
        if (completed && prepared?.Path.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var request = desktopPlayback.Request(prepared.Path, prepared.Reservation);
            if (!RequestAnimationPlayback(prepared.Path)) desktopPlayback.Cancel(request.Id);
            return;
        }
        if (completed && snapshot.Instance == snapshot.CompletedInstance && prepared == null &&
            desktopPlayback.Pending == null && !panicActive && Properties.Settings.Default.EnablePlayQueue)
        {
            GetNextClip(null, previousPath);
            return;
        }
        if (playbackMovieRegistered && desktopPlayback.Pending == null && desktopPlayback.Prepared == null && !panicActive)
            PrepareDesktopQueueSelection();
        if (!apiOnlyMode)
        {
            int maximum = Math.Max(1, playbackTimelineDurationMilliseconds);
            trkPlaybackPosition.Maximum = maximum;
            if (!playbackTimelineDragging)
                trkPlaybackPosition.Value = Math.Clamp(playbackLastKnownElapsedMilliseconds, 0, maximum);
            UpdatePlaybackTime(playbackLastKnownElapsedMilliseconds, playbackTimelineDurationMilliseconds);
            UpdatePlaybackControlsEnabled();
        }
    }

    void PrepareDesktopQueueSelection()
    {
        string path;
        object? reservation = null;
        long selectionAt = DesktopSelectionTime();
        if (!desktopManualTakeover && IsUnqueuedCardSession(nowPlayingPath) &&
            ShouldContinueQueuedCard(desktopPlayback.ActiveUnqueuedCardStartedAt, selectionAt, ReadShowDurationMinutes()) &&
            TryResolveQueueEntry(new(GetCardTagFromAnimationPath(nowPlayingPath)), out path, nowPlayingPath))
        { reservation = unqueuedDesktopContinuation; }
        else if (TryReserveDesktopQueue(out path, out DesktopQueueReservation? queued, selectionAt))
            reservation = queued;
        else
        {
            if (apiOnlyMode || !(Properties.Settings.Default.EnforceCardFilter ||
                Properties.Settings.Default.AvoidRecentRepeats) ||
                !TryChooseRandomAnimation(out path, out _)) return;
        }
        var selection = desktopPlayback.Prepare(path, reservation);
        PublishDesktopSelection(selection);
    }

    long DesktopSelectionTime()
    {
        var snapshot = Volatile.Read(ref desktopSnapshot);
        long remaining = snapshot == null ? 0 : (long)(Math.Max(0, snapshot.Duration - snapshot.Elapsed) /
            Math.Max(0.25, requestedPlaybackSpeed));
        return Environment.TickCount64 + remaining;
    }

    void PublishDesktopSelection(DesktopPlaybackCoordinator.Selection selection)
    {
        PlaybackBridgeClient? client = playbackBridgeClient;
        long attachment = Volatile.Read(ref desktopAttachment);
        if (client == null) return;
        byte[] packet = DesktopBridgeSnapshot.Selection((ulong)selection.Instance, selection.Id,
            selection.Path.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) ? "" : selection.Path);
        _ = Task.Run(() =>
        {
            try
            {
                if (attachment != Volatile.Read(ref desktopAttachment)) return;
                int result = client.CallRoundTrip("IStripperPrepareDesktopSelection", packet);
                if (result < 0 && !formIsClosing)
                    BeginInvoke((Action)(() =>
                    {
                        if (attachment == Volatile.Read(ref desktopAttachment)) desktopPlayback.Cancel(selection.Id);
                    }));
            }
            catch (Exception exception) { SetPlaybackStatus(exception.Message); }
        });
    }

    void ClearNativeDesktopSelection()
    {
        PublishDesktopSelection(new(desktopPlayback.NextCommandId(), "", null, true, 0));
    }

    void ReleaseDesktopReservation()
    {
        if (desktopPlayback.Pending != null) desktopPlayback.Cancel(desktopPlayback.Pending.Id);
        if (desktopPlayback.Prepared != null) desktopPlayback.Cancel(desktopPlayback.Prepared.Id);
    }

    void ValidateDesktopQueueReservation()
    {
        var selection = desktopPlayback.Prepared;
        bool expired = selection?.Reservation is DesktopQueueReservation { Continuation: true } &&
            !ShouldContinueQueuedCard(desktopPlayback.ActiveQueuedCardStartedAt, DesktopSelectionTime(), ReadShowDurationMinutes()) ||
            ReferenceEquals(selection?.Reservation, unqueuedDesktopContinuation) &&
            !ShouldContinueQueuedCard(desktopPlayback.ActiveUnqueuedCardStartedAt, DesktopSelectionTime(), ReadShowDurationMinutes());
        if (expired && selection != null)
        {
            desktopPlayback.Cancel(selection.Id);
            ClearNativeDesktopSelection();
            return;
        }
        if (selection?.Reservation is not DesktopQueueReservation reservation) return;
        List<PlayQueueEntry> queue = reservation.Manual ? manualPlayQueue : automaticPlayQueue;
        if (!Properties.Settings.Default.EnablePlayQueue ||
            !reservation.Continuation && !reservation.Requeued &&
            !queue.Any(entry => ReferenceEquals(entry, reservation.Entry)))
        {
            desktopPlayback.Cancel(selection.Id);
            ClearNativeDesktopSelection();
        }
    }

    bool TryReserveDesktopQueue(out string path, out DesktopQueueReservation? reservation, long? selectionAt = null)
    {
        path = "";
        reservation = null;
        if (!Properties.Settings.Default.EnablePlayQueue) return false;
        if (desktopPlayback.ActiveQueuedCard != null && ShouldContinueQueuedCard(desktopPlayback.ActiveQueuedCardStartedAt,
            selectionAt ?? Environment.TickCount64, ReadShowDurationMinutes()) &&
            TryResolveQueueEntry(desktopPlayback.ActiveQueuedCard, out path, desktopPlayback.ActiveQueuedCardLastAnimationPath, desktopPlayback.ActiveQueuedCardUsesSmartRules))
        {
            reservation = new(desktopPlayback.ActiveQueuedCard, desktopPlayback.ActiveManualQueueEntry != null, true, desktopPlayback.ActiveQueuedCardUsesSmartRules);
            return true;
        }
        List<PlayQueueEntry> manual = manualPlayQueue.Where(QueueEntryAllowedForPlayback).ToList();
        if (Properties.Settings.Default.RandomManualQueueSelection) Shuffle(manual);
        foreach (PlayQueueEntry entry in manual)
            if (TryResolveQueueEntry(entry, out path, useSmartRules: true))
            { reservation = new(entry, true, false, true); return true; }
        if (desktopPlayback.ActiveManualQueueEntry is PlayQueueEntry active &&
            Properties.Settings.Default.RequeueCompletedManualItems &&
            QueueEntryAllowedForPlayback(active) && TryResolveQueueEntry(active, out path, useSmartRules: true))
        { reservation = new(active, true, false, true, true); return true; }
        if (!apiOnlyMode && Properties.Settings.Default.EnforceCardFilter)
            foreach (PlayQueueEntry entry in automaticPlayQueue.Where(QueueEntryAllowedForPlayback))
                if (TryResolveQueueEntry(entry, out path))
                { reservation = new(entry, false, false, false); return true; }
        return false;
    }

    void ConfirmDesktopReservation(DesktopQueueReservation reservation, string path)
    {
        if (!reservation.Continuation)
        {
            List<PlayQueueEntry> queue = reservation.Manual ? manualPlayQueue : automaticPlayQueue;
            if (reservation.Requeued)
            {
                if (!ReferenceEquals(desktopPlayback.ActiveManualQueueEntry, reservation.Entry)) return;
                CompleteActiveManualQueueEntry();
            }
            int index = queue.FindIndex(entry => ReferenceEquals(entry, reservation.Entry));
            if (index < 0) return;
            queue.RemoveAt(index);
            CompleteActiveManualQueueEntry();
            StartQueuedCardSession(reservation.Entry, path, reservation.SmartRules);
            desktopPlayback.ActiveManualQueueEntry = reservation.Manual ? reservation.Entry : null;
            desktopPlayback.ActiveAutomaticQueueEntry = reservation.Manual ? null : reservation.Entry;
            if (!reservation.Manual) FillAutomaticQueue(reservation.Entry.CardTag);
        }
        desktopPlayback.ActiveQueuedCardLastAnimationPath = path;
        SavePreviousQueue();
        RenderPlayQueues();
    }

    bool RequestDesktopAnimation(string path)
    {
        if (InvokeRequired) return (bool)Invoke(() => RequestDesktopAnimation(path));
        desktopManualTakeover = false;
        object? reservation = desktopPlayback.Pending?.Path == path ? desktopPlayback.Pending.Reservation : null;
        DesktopBridgeSnapshot? snapshot = Volatile.Read(ref desktopSnapshot);
        long attachment = Volatile.Read(ref desktopAttachment);
        DesktopPlaybackCoordinator.Observation? baseline = customIstripperSuspended && snapshot != null
            ? new(attachment, snapshot.Sequence, snapshot.Instance, snapshot.CompletedInstance,
                snapshot.Path, snapshot.State, snapshot.Elapsed, snapshot.Duration, snapshot.Request)
            : null;
        var request = desktopPlayback.Request(path, reservation, baseline);
        BeginAnimationReplacement(path);
        desktopRequestDeadline = DateTime.UtcNow.AddSeconds(5);
        PlaybackBridgeClient? client = playbackBridgeClient;
        if (client?.IsConnected != true) { desktopPlayback.Cancel(request.Id); return false; }
        byte[] packet = DesktopBridgeSnapshot.Selection((ulong)request.Instance, request.Id, path);
        _ = Task.Run(() =>
        {
            try
            {
                if (attachment != Volatile.Read(ref desktopAttachment)) return;
                int result = client.CallRoundTrip("IStripperRequestDesktopSelection", packet);
                if (result < 0 && !formIsClosing)
                    BeginInvoke((Action)(() =>
                    {
                        desktopPlayback.Cancel(request.Id);
                        SetPlaybackStatus($"Playback request failed (0x{result:X8}); the queue item was retained.");
                    }));
            }
            catch (Exception exception)
            {
                SetPlaybackStatus(exception.Message);
                if (!formIsClosing) BeginInvoke((Action)(() => desktopPlayback.Cancel(request.Id)));
            }
        });
        return true;
    }
}
