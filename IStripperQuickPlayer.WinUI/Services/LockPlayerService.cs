using System.Runtime.InteropServices;
using System.Text;
using IStripperQuickPlayer.WinUI.Core;
using Nektra.Deviare2;

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

    private NktSpyMgr? _spyMgr;
    private NktHook? _registryHook;
    private NktHook? _windowProcHook;
    private NktProcess? _vghdProcess;
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

        try
        {
            _spyMgr = new NktSpyMgr();
            _spyMgr.Initialize();
            _spyMgr.OnFunctionCalled += OnFunctionCalled;
            _attachTimer.Change(100, 2000);
            _status("Deviare hook service started");
        }
        catch (Exception ex)
        {
            _status($"Deviare hook service could not start: {ex.Message}");
        }
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
        if (_disposed || _spyMgr == null)
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
            _status($"Deviare attach failed: {ex.Message}");
        }
    }

    private void AttachToVghdProcess()
    {
        if (_spyMgr == null)
        {
            return;
        }

        NktProcessesEnum enumProcess = _spyMgr.Processes();
        NktProcess process = enumProcess.First();
        while (process != null)
        {
            if (process.Name.Equals("vghd.exe", StringComparison.InvariantCultureIgnoreCase) && process.PlatformBits == 64)
            {
                _registryHook = _spyMgr.CreateHook(
                    "KernelBase.dll!RegSetValueExW",
                    (int)(eNktHookFlags.flgAutoHookChildProcess | eNktHookFlags.flgOnlyPreCall));
                _registryHook.Hook(true);

                _windowProcHook = _spyMgr.CreateHook(
                    "user32.dll!CallWindowProcW",
                    (int)eNktHookFlags.flgAutoHookChildProcess);
                _windowProcHook.Hook(true);

                _registryHook.Attach(process, true);
                _vghdProcess = process;
                _vghdProcessId = process.Id;
                ChangePlayerLocked();
                _status("Attached Deviare hooks to vghd.exe");
                return;
            }

            process = enumProcess.Next();
        }
    }

    private void ChangePlayerLocked()
    {
        if (_windowProcHook == null || _vghdProcess == null)
        {
            return;
        }

        try
        {
            eNktHookState state = _windowProcHook.State(_vghdProcess);
            if (_playerLocked)
            {
                if (state != eNktHookState.stActive && state != eNktHookState.stDisabled)
                {
                    _windowProcHook.Attach(_vghdProcess, true);
                }
                else if (state == eNktHookState.stDisabled)
                {
                    _windowProcHook.Enable(_vghdProcess, true);
                }
            }
            else if (state == eNktHookState.stActive)
            {
                _windowProcHook.Enable(_vghdProcess, false);
            }
        }
        catch (Exception ex)
        {
            _status($"Could not change player lock state: {ex.Message}");
        }
    }

    private void OnFunctionCalled(NktHook hook, NktProcess process, NktHookCallInfo hookCallInfo)
    {
        try
        {
            if (hook.FunctionName == "user32.dll!CallWindowProcW")
            {
                HandleWindowProc(hookCallInfo);
                return;
            }

            HandleRegistryWrite(hookCallInfo);
        }
        catch (Exception ex)
        {
            _status($"Deviare hook callback failed: {ex.Message}");
        }
    }

    private void HandleWindowProc(NktHookCallInfo hookCallInfo)
    {
        if (!_playerLocked)
        {
            return;
        }

        foreach (INktParam param in hookCallInfo.Params())
        {
            if (param.Name == "Msg" && Convert.ToUInt32(param.Value) == 132)
            {
                hookCallInfo.Result().LongVal = -1;
                hookCallInfo.Result().LongLongVal = -1;
                return;
            }
        }
    }

    private void HandleRegistryWrite(NktHookCallInfo hookCallInfo)
    {
        FilterEnforcementState state = _getState();
        if (!state.Settings.EnforceCardFilter)
        {
            return;
        }

        IntPtr lpData = IntPtr.Zero;
        string valueName = string.Empty;
        int dataLength = 0;
        foreach (INktParam param in hookCallInfo.Params())
        {
            if (param.Name == "lpData")
            {
                lpData = param.PointerVal;
            }
            else if (param.Name == "cbData")
            {
                dataLength = Convert.ToInt32(param.Value);
            }
            else if (param.Name == "lpValueName")
            {
                valueName = param.Value?.ToString() ?? string.Empty;
            }
        }

        if (valueName != "CurrentAnim" || lpData == IntPtr.Zero || dataLength < 2)
        {
            return;
        }

        string requestedAnimation = ReadRemoteUnicodeString(lpData, dataLength);
        if (string.IsNullOrWhiteSpace(requestedAnimation))
        {
            return;
        }

        if (string.Equals(requestedAnimation, _lastReplacement, StringComparison.OrdinalIgnoreCase))
        {
            _lastReplacement = null;
            return;
        }

        ClipReplacement? replacement = FindReplacement(requestedAnimation, state);
        if (replacement == null || replacement.Animation.Equals(requestedAnimation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastReplacement = replacement.Animation;
        _playerControlService.ForceAnimation(replacement.Animation);
        _replacementSelected?.Invoke(replacement.Card, replacement.Clip);
        _status($"Blocked filtered clip and selected {replacement.Card.ModelName}: {replacement.Card.Outfit}");

        hookCallInfo.Result().LongLongVal = -1;
        hookCallInfo.Result().LongVal = -1;
        hookCallInfo.Result().Value = -1;
        hookCallInfo.LastError = 5;
    }

    private string ReadRemoteUnicodeString(IntPtr address, int length)
    {
        if (_spyMgr == null || _vghdProcessId == 0)
        {
            return string.Empty;
        }

        INktProcessMemory processMemory = _spyMgr.ProcessMemoryFromPID(_vghdProcessId);
        byte[] buffer = new byte[length];
        GCHandle pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            processMemory.ReadMem(pinnedBuffer.AddrOfPinnedObject(), address, new IntPtr(length));
            return Encoding.Unicode.GetString(buffer).Replace("\0", string.Empty);
        }
        finally
        {
            pinnedBuffer.Free();
        }
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
        try
        {
            if (_spyMgr != null)
            {
                _spyMgr.OnFunctionCalled -= OnFunctionCalled;
            }
        }
        catch
        {
        }
    }

    private sealed record ClipReplacement(string Animation, ModelCard Card, ModelClip Clip);
}

public sealed record FilterEnforcementState(
    IReadOnlyList<ModelCard> AllCards,
    IReadOnlyList<ModelCard> VisibleCards,
    AppSettings Settings);
