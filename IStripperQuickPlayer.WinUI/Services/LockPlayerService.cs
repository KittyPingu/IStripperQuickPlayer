using System.Text;
using IStripperQuickPlayer.Interop;
using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class LockPlayerService : IDisposable
{
    private readonly Func<FilterEnforcementState> _getState;
    private readonly CardQueryService _queryService;
    private readonly PlayerControlService _playerControlService;
    private readonly Action<string> _status;
    private readonly Action<ModelCard, ModelClip>? _replacementSelected;
    private readonly Timer _attachTimer;
    private readonly object _gate = new();

    private PlaybackBridgeClient? _bridge;
    private int _vghdProcessId;
    private bool _disposed;
    private bool _started;
    private bool _playerLocked;
    private string? _lastReplacement;

    public LockPlayerService(
        Func<FilterEnforcementState> getState,
        CardQueryService queryService,
        PlayerControlService playerControlService,
        Action<string> status,
        Action<ModelCard, ModelClip>? replacementSelected = null)
    {
        _getState = getState;
        _queryService = queryService;
        _playerControlService = playerControlService;
        _status = status;
        _replacementSelected = replacementSelected;
        _attachTimer = new Timer(AttachTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsPlayerLocked => _playerLocked;

    public void Start(bool playerLocked)
    {
        lock (_gate)
        {
            _playerLocked = playerLocked;
            if (_started)
            {
                ChangePlayerLocked();
                return;
            }

            _started = true;
        }

        _attachTimer.Change(100, 2000);
    }

    public void SetPlayerLocked(bool locked)
    {
        _playerLocked = locked;
        ChangePlayerLocked();
        _status(locked ? "iStripper player lock enabled" : "iStripper player lock disabled");
    }

    public void TogglePlayerLocked()
    {
        SetPlayerLocked(!_playerLocked);
    }

    private void AttachTimerTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_vghdProcessId != 0 && System.Diagnostics.Process.GetProcessesByName("vghd").Any(p => p.Id == _vghdProcessId))
            {
                return;
            }

            AttachToVghdProcess();
        }
        catch (Exception ex)
        {
            _status($"Playback bridge attach failed: {ex.Message}");
        }
    }

    private void AttachToVghdProcess()
    {
        foreach (System.Diagnostics.Process process in
            System.Diagnostics.Process.GetProcessesByName("vghd"))
        {
            using (process)
            {
                _bridge?.Dispose();
                _bridge = PlaybackBridgeClient.Attach(process.Id,
                    Path.Combine(AppContext.BaseDirectory,
                        "IStripperPlaybackBridge64.dll"),
                    HandleRegistryWrite);
                int hookResult = _bridge.StartRegistryHook();
                if (hookResult < 0)
                    throw new InvalidOperationException(
                        $"Registry hook setup failed (0x{hookResult:X8}).");
                _vghdProcessId = process.Id;
                ChangePlayerLocked();
                _status("Attached MinHook bridge to vghd.exe");
                return;
            }
        }
    }

    private void ChangePlayerLocked()
    {
        if (_bridge?.IsConnected != true)
        {
            return;
        }

        try
        {
            int result = _bridge.Call("IStripperSetPlayerLocked",
                _playerLocked ? 1UL : 0UL);
            if (result < 0)
                throw new InvalidOperationException(
                    $"Player lock failed (0x{result:X8}).");
        }
        catch (Exception ex)
        {
            _status($"Could not change player lock state: {ex.Message}");
        }
    }

    private bool HandleRegistryWrite(string valueName, byte[] data)
    {
        FilterEnforcementState state = _getState();
        if (!state.Settings.EnforceCardFilter)
            return false;

        if (valueName != "CurrentAnim" || data.Length < 2)
            return false;

        string requestedAnimation = Encoding.Unicode.GetString(data)
            .Replace("\0", string.Empty);
        if (string.IsNullOrWhiteSpace(requestedAnimation))
            return false;

        if (string.Equals(requestedAnimation, _lastReplacement, StringComparison.OrdinalIgnoreCase))
        {
            _lastReplacement = null;
            return false;
        }

        ClipReplacement? replacement = FindReplacement(requestedAnimation, state);
        if (replacement == null || replacement.Animation.Equals(requestedAnimation, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _lastReplacement = replacement.Animation;
        _playerControlService.ForceAnimation(replacement.Animation);
        _replacementSelected?.Invoke(replacement.Card, replacement.Clip);
        _status($"Blocked filtered clip and selected {replacement.Card.ModelName}: {replacement.Card.Outfit}");

        return true;
    }

    private ClipReplacement? FindReplacement(string requestedAnimation, FilterEnforcementState state)
    {
        string[] parts = requestedAnimation.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string requestedCardId = parts[0];
        string requestedClipName = parts[1];
        ModelCard? requestedCard = state.AllCards.FirstOrDefault(c => c.Name.Equals(requestedCardId, StringComparison.OrdinalIgnoreCase));
        if (requestedCard == null)
        {
            return null;
        }

        ModelCard? visibleRequestedCard = state.VisibleCards.FirstOrDefault(c => c.Name.Equals(requestedCardId, StringComparison.OrdinalIgnoreCase));
        if (visibleRequestedCard != null)
        {
            IReadOnlyList<ModelClip> requestedCardClips = _queryService.QueryClips(visibleRequestedCard.Clips, state.Settings);
            ModelClip? requestedClip = requestedCardClips.FirstOrDefault(c => c.ClipName?.Equals(requestedClipName, StringComparison.OrdinalIgnoreCase) == true);
            if (requestedClip != null)
            {
                return new ClipReplacement(requestedAnimation, visibleRequestedCard, requestedClip);
            }

            ModelClip? sameCardReplacement = ChooseClip(requestedCardClips);
            if (sameCardReplacement != null)
            {
                return new ClipReplacement(BuildAnimation(sameCardReplacement), visibleRequestedCard, sameCardReplacement);
            }
        }

        List<ModelCard> candidateCards = state.VisibleCards
            .Where(c => c.Clips.Count > 0)
            .ToList();
        if (candidateCards.Count == 0)
        {
            return null;
        }

        ModelCard replacementCard = ChooseReplacementCard(candidateCards, requestedCardId, state.Settings.Randomize);
        IReadOnlyList<ModelClip> clips = _queryService.QueryClips(replacementCard.Clips, state.Settings);
        ModelClip? replacementClip = ChooseClip(clips);
        return replacementClip == null
            ? null
            : new ClipReplacement(BuildAnimation(replacementClip), replacementCard, replacementClip);
    }

    private static ModelCard ChooseReplacementCard(IReadOnlyList<ModelCard> cards, string requestedCardId, bool randomize)
    {
        if (randomize && cards.Count > 1)
        {
            ModelCard selected;
            do
            {
                selected = cards[Random.Shared.Next(cards.Count)];
            }
            while (selected.Name.Equals(requestedCardId, StringComparison.OrdinalIgnoreCase));
            return selected;
        }

        int index = cards.ToList().FindIndex(c => c.Name.Equals(requestedCardId, StringComparison.OrdinalIgnoreCase));
        int nextIndex = index < 0 || index + 1 >= cards.Count ? 0 : index + 1;
        return cards[nextIndex];
    }

    private static ModelClip? ChooseClip(IReadOnlyList<ModelClip> clips)
    {
        if (clips.Count == 0)
        {
            return null;
        }

        return clips[Random.Shared.Next(clips.Count)];
    }

    private static string BuildAnimation(ModelClip clip)
    {
        string clipName = clip.ClipName ?? string.Empty;
        string folder = clipName.Split('_').FirstOrDefault() ?? string.Empty;
        return $@"{folder}\{clipName}";
    }

    public void Dispose()
    {
        _disposed = true;
        _attachTimer.Dispose();
        _bridge?.Dispose();
        _bridge = null;
    }

    private sealed record ClipReplacement(string Animation, ModelCard Card, ModelClip Clip);
}

public sealed record FilterEnforcementState(
    IReadOnlyList<ModelCard> AllCards,
    IReadOnlyList<ModelCard> VisibleCards,
    AppSettings Settings);
