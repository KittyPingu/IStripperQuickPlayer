#include <Windows.h>
#include "BridgeIpc.h"
#include <MinHook.h>
#include <TlHelp32.h>
#include <dwmapi.h>
#include <compressapi.h>
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <cstring>
#include <float.h>
#include <functional>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <utility>
#include <vector>
#include <winver.h>

#pragma comment(lib, "Cabinet.lib")
#pragma comment(lib, "Dwmapi.lib")
#pragma comment(lib, "Version.lib")

// This bridge discovers private vghd functions, vtables, hook sites, and object
// layouts from the loaded image. The INI is an audit trail, never trusted input.
namespace
{
    void DressingRoomLog(const char* format, ...)
    {
        char message[1024] = {};
        va_list arguments;
        va_start(arguments, format);
        vsnprintf_s(message, _countof(message), _TRUNCATE, format, arguments);
        va_end(arguments);
        wchar_t path[MAX_PATH] = {};
        DWORD length = GetTempPathW(_countof(path), path);
        if (length == 0 || length >= _countof(path) ||
            wcscat_s(path, L"IstripperQuickPlayer-dressingroom.log") != 0)
            return;
        HANDLE file = CreateFileW(path, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;
        SYSTEMTIME now = {};
        GetLocalTime(&now);
        char line[1280] = {};
        int count = snprintf(line, _countof(line),
            "%04u-%02u-%02u %02u:%02u:%02u.%03u pid=%lu tid=%lu %s\r\n",
            now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute,
            now.wSecond, now.wMilliseconds, GetCurrentProcessId(),
            GetCurrentThreadId(), message);
        DWORD written = 0;
        if (count > 0)
            WriteFile(file, line, static_cast<DWORD>(count), &written, nullptr);
        CloseHandle(file);
    }

    using AvformatOpenInput = int(__cdecl*)(void**, const char*, void*, void*);
    AvformatOpenInput g_originalAvformatOpenInput = nullptr;
    SRWLOCK g_dressingRoomUrlLock = SRWLOCK_INIT;
    std::string g_dressingRoomUrl;
    std::string g_selectedDressingRoomResource;
    volatile LONG g_dressingRoomHookInstalled = 0;
    volatile LONG g_dressingRoomCacheScanPending = 0;
    alignas(8) volatile LONG64 g_dressingRoomLastScanStarted = 0;
    PVOID volatile g_dressingRoomUrlRegion = nullptr;
    SRWLOCK g_dressingRoomObjectsLock = SRWLOCK_INIT;
    void* g_dressingRoomObjects[256] = {};
    LONG g_dressingRoomObjectCount = 0;

    std::uintptr_t AnimationFrameRva = 0;
    std::uintptr_t MovieAdvanceRva = 0;
    std::uintptr_t MoviePauseRva = 0;
    std::uintptr_t MovieResumeRva = 0;
    std::uintptr_t MovieSetPlayRateRva = 0;
    std::uintptr_t AvcodecOpenSlotRva = 0;
    std::uintptr_t AvSeekFrameSlotRva = 0;
    std::uintptr_t DecodeScaleCallRva = 0;
    std::uintptr_t DecodeScaleSlotRva = 0;
    std::uintptr_t DecoderWorkerTargetLoadRva = 0;
    std::uintptr_t VideoSeekRva = 0;
    std::uintptr_t MovieVtableRva = 0;
    std::uintptr_t VideoFfmpegVtableRva = 0;
    std::uintptr_t BpkSoundVtableRva = 0;
    std::uintptr_t BpkSoundCloseRva = 0;
    std::uintptr_t BpkSoundWriteRva = 0;
    std::uintptr_t AudioWriteCallRva = 0;
    std::uintptr_t BpkSoundRawWriteRva = 0;
    std::uintptr_t WmvAudioWriteCallRva = 0;
    std::uintptr_t VideoWmvCoreVtableRva = 0;
    std::uintptr_t SsvReaderVtableRva = 0;
    std::uintptr_t SsvFileVtableRva = 0;
    std::uintptr_t FsClipNodeVtableRva = 0;
    std::uintptr_t FsClipNodeNextShowClipRva = 0;
    std::uintptr_t PlayableCardVtableRva = 0;
    std::uintptr_t PlayableCardExactConstructorRva = 0;
    std::uintptr_t CardSequencerInsertNextRva = 0;
    std::uintptr_t CardSequencerTakeNextAtRva = 0;
    std::uintptr_t VghdOperatorNewRva = 0;
    std::uintptr_t WmvClearQueuesRva = 0;
    std::uintptr_t WmvPeekFrameRva = 0;

    std::size_t MovieStateOffset = 0;
    std::size_t MovieAnimationOffset = 0;
    std::size_t MovieCurrentFrameOffset = 0;
    std::size_t MovieMutexOffset = 0;
    std::size_t AnimationAlphaOutputOffset = 0;
    std::size_t AnimationAlphaWidthOffset = 0;
    std::size_t AnimationAlphaHeightOffset = 0;
    std::size_t AnimationAlphaScratch1Offset = 0;
    std::size_t AnimationAlphaScratch2Offset = 0;
    std::size_t AnimationAlphaScratchPointer1Offset = 0;
    std::size_t AnimationAlphaScratchPointer2Offset = 0;
    std::size_t AnimationAlphaGenerationOffset = 0;
    std::size_t AnimationAlphaFrameOffset = 0;
    std::size_t AnimationSsvOffset = 0;
    std::size_t AnimationInfoOffset = 0;
    std::size_t AnimationTotalFramesOffset = 0;
    std::size_t AnimationFramesPerSecondOffset = 0;
    // Current high-resolution cards can carry a 6016x3172 alpha plane
    // (19,082,752 bytes). Keep a conservative upper bound while allowing
    // those native-resolution masks.
    constexpr std::size_t MaximumAlphaOutputSize = 64 * 1024 * 1024;
    constexpr std::size_t MaximumAlphaCheckpointBytes = 128 * 1024 * 1024;
    constexpr int MaximumAlphaCheckpoints = 16;
    constexpr int AlphaCheckpointIntervalSeconds = 5;
    constexpr std::uint64_t DefaultPersistentAlphaCacheBytes =
        256ULL * 1024 * 1024;
    constexpr std::uint32_t PersistentAlphaMagic = 0x43415051;
    constexpr std::uint16_t PersistentAlphaVersion = 1;
    std::size_t SsvVideoDecoderOffset = 0;
    std::size_t VideoFormatContextOffset = 0;
    std::size_t VideoFrameQueueOffset = 0;
    std::size_t VideoFrameQueueMutexOffset = 0;
    std::size_t VideoCurrentFrameOffset = 0;
    std::size_t VideoSoundOffset = 0;
    std::size_t BpkSoundOutputOffset = 0;
    std::size_t BpkSoundDeviceOffset = 0;
    std::size_t BpkSoundPendingOffset = 0;
    std::size_t QueueBeginOffset = 0;
    std::size_t QueueEndOffset = 0;
    std::size_t QueueEntriesOffset = 0;
    std::size_t VideoQueueEntryReadyOffset = 0;
    std::size_t StreamIndexEntriesOffset = 0;
    std::size_t StreamIndexEntryCountOffset = 0;
    std::size_t WmvReaderObjectOffset = 0;
    std::size_t WmvFrameHeldOffset = 0;
    std::size_t WmvReaderInterfaceOffset = 0;
    std::size_t WmvAdvancedInterfaceOffset = 0;
    std::size_t WmvStatusEventOffset = 0;
    std::size_t WmvLastResultOffset = 0;
    std::size_t WmvSampleCounterOffset = 0;
    std::size_t WmvReaderPausedOffset = 0;
    std::size_t WmvQueueMutexOffset = 0;
    std::size_t WmvColorQueueOffset = 0;
    std::size_t FsClipNodeSceneOffset = 0;
    std::size_t SceneNodesOffset = 0;
    std::size_t FsClipNodePlayableCardOffset = 0;
    std::size_t PlayableCardTagOffset = 0;
    std::size_t PlayableCardClipsOffset = 0;
    std::size_t PlayableCardSize = 0;
    std::size_t CardSequencerNextOffset = 0;
    std::size_t NextCardPlayableCardOffset = 0;
    std::size_t SceneExpectedPendingOffset = 0;
    std::size_t SceneExpectedSelectionOffset = 0;
    std::size_t SceneExpectedCardOffset = 0;
    std::size_t SceneExpectedModeOffset = 0;

    constexpr int PlayingState = 3;
    constexpr int PausedState = 4;
    constexpr HRESULT BridgeSuccess = 1;
    bool IsWritable(const void* address, std::size_t length);
    constexpr HRESULT WmvInvalidRequest =
        static_cast<HRESULT>(0xC00D002B);
    constexpr unsigned ExpectedAvformatVersion =
        (57u << 16) | (47u << 8) | 101u;
    constexpr std::size_t FormatContextStreamCountOffset = 0x2C;
    constexpr std::size_t FormatContextStreamsOffset = 0x30;
    constexpr std::size_t StreamCodecContextOffset = 0x08;
    constexpr std::size_t CodecContextMediaTypeOffset = 0x0C;

    constexpr bool DecoderFramesReady(
        int movieFrame, int decoderFrame)
    {
        return movieFrame > 0 && decoderFrame >= movieFrame;
    }

    static_assert(!DecoderFramesReady(0, 0));
    static_assert(!DecoderFramesReady(30, 20));
    static_assert(DecoderFramesReady(30, 30));
    constexpr bool AlphaDecoderProgressed(int previousFrame,
        int currentFrame, int previousGeneration, int currentGeneration)
    {
        return currentFrame > previousFrame ||
            currentGeneration != previousGeneration;
    }

    static_assert(!AlphaDecoderProgressed(12, 12, 4, 4));
    static_assert(AlphaDecoderProgressed(12, 13, 4, 4));
    static_assert(AlphaDecoderProgressed(12, 12, 4, 5));
    constexpr int RequiredAlphaProgressObservations = 4;
    static_assert(RequiredAlphaProgressObservations > 1);

    constexpr unsigned char MoviePauseSignature[] = {
        0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B
    };

    constexpr unsigned char MovieResumeSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x18, 0x57, 0x48, 0x83
    };

    constexpr unsigned char MovieSetPlayRateSignature[] = {
        0x40, 0x53, 0x48, 0x83, 0xEC, 0x30, 0x0F, 0x29
    };

    constexpr unsigned char WmvClearQueuesSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x18, 0x48, 0x89, 0x74,
        0x24, 0x20, 0x55, 0x57, 0x41, 0x54, 0x41, 0x56,
        0x41, 0x57, 0x48, 0x8D, 0x6C, 0x24, 0xC9, 0x48,
        0x81, 0xEC, 0xD0, 0x00, 0x00, 0x00, 0x48, 0x8B,
        0xF1
    };

    constexpr unsigned char WmvPeekFrameSignature[] = {
        0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B,
        0xD9, 0x48, 0x83, 0xC1
    };

    constexpr unsigned char BpkSoundRawWriteSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57,
        0x41, 0x56, 0x41, 0x57, 0x48, 0x8B, 0xEC, 0x48,
        0x83, 0xEC, 0x40
    };

    constexpr unsigned char FsClipNodeStartNextShowSignature[] = {
        0x48, 0x89, 0x54, 0x24, 0x10, 0x55, 0x53, 0x56,
        0x57, 0x41, 0x54, 0x41, 0x56, 0x41, 0x57, 0x48,
        0x8D, 0xAC, 0x24, 0x20, 0xFF, 0xFF, 0xFF, 0x48
    };

    constexpr unsigned char CardSequencerInsertNextSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74,
        0x24, 0x18, 0x48, 0x89, 0x7C, 0x24, 0x20, 0x89,
        0x54, 0x24, 0x10, 0x55, 0x41, 0x54, 0x41, 0x55
    };

    constexpr unsigned char PlayableCardExactConstructorSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74,
        0x24, 0x20, 0x4C, 0x89, 0x44, 0x24, 0x18, 0x48,
        0x89, 0x4C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x30
    };

    constexpr unsigned char CardSequencerTakeNextAtSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x57, 0x48, 0x83,
        0xEC, 0x20, 0x48, 0x63, 0xDA, 0x45, 0x0F, 0xB6,
        0xD0, 0x48, 0x8B, 0xF9
    };

    constexpr std::size_t DecodeScaleCallLength = 6;
    std::size_t DecoderWorkerTargetLoadLength = 0;

    using ManagerAction = void(__fastcall*)(void* manager);
    using ManagerSetPlayRate = void(__fastcall*)(void* manager, double playRate);
    using MutexAction = void(__fastcall*)(void* mutex);
    using MutexTryLock = bool(__fastcall*)(void* mutex, int timeoutMilliseconds);
    using AvcodecOpen2 = int(__cdecl*)(void* codecContext, const void* codec, void* options);
    using AvcodecFlushBuffers = void(__cdecl*)(void* codecContext);
    using AvSeekFrame = int(__cdecl*)(void* formatContext, int streamIndex,
        std::int64_t timestamp, int flags);
    using AvOptSetInt = int(__cdecl*)(void* target, const char* name,
        std::int64_t value, int searchFlags);
    using AvformatVersion = unsigned(__cdecl*)();
    using VideoSeek = bool(__fastcall*)(void* video, int frame);
    using WmvAction = HRESULT(__fastcall*)(void* reader);
    using WmvStart = HRESULT(__fastcall*)(void* reader,
        std::uint64_t startTime, std::uint64_t duration, float rate,
        void* context);
    using WmvSetUserClock = HRESULT(__fastcall*)(void* reader, BOOL enabled);
    using WmvDeliverTime = HRESULT(__fastcall*)(void* reader,
        std::uint64_t time);
    using WmvClearQueues = void(__fastcall*)(void* reader);
    using WmvPeekFrame = void*(__fastcall*)(void* reader);
    using QThreadRequestInterruption = void(__fastcall*)(void* thread);
    using QThreadWait = bool(__fastcall*)(void* thread, unsigned long milliseconds);
    using QThreadStart = void(__fastcall*)(void* thread, int priority);
    using QAudioAction = void(__fastcall*)(void* output);
    using QAudioStart = void*(__fastcall*)(void* output);
    using QByteArrayAction = void(__fastcall*)(void* bytes);
    using BpkSoundWrite = void(__fastcall*)(void* sound, const void* begin,
        const void* end, int sampleCount);
    using BpkSoundRawWrite = void(__fastcall*)(void* sound,
        const void* bytes, int byteCount);
    using VirtualAlloc2Action = PVOID(WINAPI*)(HANDLE process, PVOID baseAddress,
        SIZE_T size, ULONG allocationType, ULONG pageProtection,
        MEM_EXTENDED_PARAMETER* parameters, ULONG parameterCount);
    using QOpenGLShaderProgramBind = bool(__fastcall*)(void* program);
    using QOpenGLShaderUniformLocation = int(__fastcall*)(
        const void* program, const char* name);
    using QOpenGLShaderSetUniform1 = void(__fastcall*)(
        void* program, int location, float value);
    using QOpenGLShaderSetUniformInt = void(__fastcall*)(
        void* program, int location, int value);
    using QOpenGLShaderSetUniform2 = void(__fastcall*)(
        void* program, int location, float x, float y);
    using QOpenGLShaderSetUniform4 = void(__fastcall*)(
        void* program, int location, float x, float y, float z, float w);

    using WglGetCurrentContext = HGLRC(WINAPI*)();
    using WglGetProcAddress = PROC(WINAPI*)(LPCSTR);
    using GlActiveTexture = void(APIENTRY*)(unsigned int texture);
    using GlGenTextures = void(APIENTRY*)(int count, unsigned int* textures);
    using GlBindTexture = void(APIENTRY*)(
        unsigned int target, unsigned int texture);
    using GlIsTexture = unsigned char(APIENTRY*)(unsigned int texture);
    using GlGetIntegerv = void(APIENTRY*)(unsigned int name, int* value);
    using GlPixelStorei = void(APIENTRY*)(unsigned int name, int value);
    using GlTexParameteri = void(APIENTRY*)(
        unsigned int target, unsigned int name, int value);
    using GlTexImage2D = void(APIENTRY*)(unsigned int target, int level,
        int internalFormat, int width, int height, int border,
        unsigned int format, unsigned int type, const void* pixels);
    using GlTexSubImage2D = void(APIENTRY*)(unsigned int target, int level,
        int x, int y, int width, int height, unsigned int format,
        unsigned int type, const void* pixels);

    struct FullScreenShaderDataPacket
    {
        float values[4] = {};
        std::uint32_t sequence = 0;
    };

    static_assert(sizeof(FullScreenShaderDataPacket) == 20);

    constexpr std::uint32_t FullScreenShaderTextureMagic = 0x58545051;
    constexpr std::uint32_t MaximumFullScreenShaderTextureDimension = 4096;
    constexpr std::size_t FullScreenShaderTextureNameCapacity = 32;
    constexpr std::size_t MaximumFullScreenShaderTextures = 16;
    constexpr std::size_t MaximumFullScreenShaderTextureBytes =
        MaximumFullScreenShaderTextureDimension *
        MaximumFullScreenShaderTextureDimension * 4;
    struct FullScreenShaderTexturePacket
    {
        std::uint32_t magic = FullScreenShaderTextureMagic;
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        std::uint32_t sequence = 0;
        std::uint32_t byteLength = 0;
        std::uint32_t nameLength = 0;
        char name[FullScreenShaderTextureNameCapacity] = {};
    };

    static_assert(sizeof(FullScreenShaderTexturePacket) == 56);

    struct FullScreenShaderTextureState
    {
        std::string name;
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        std::uint32_t sequence = 0;
        std::uint64_t generation = 0;
        std::shared_ptr<const std::vector<unsigned char>> rgba;
    };

    struct FullScreenShaderGlTexture
    {
        HGLRC context = nullptr;
        std::string name;
        unsigned int texture = 0;
        int textureUnit = -1;
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        std::uint64_t generation = 0;
    };

    PVOID volatile g_activeMovie = nullptr;
    PVOID volatile g_activeAnimation = nullptr;
    PVOID volatile g_consumedMovie = nullptr;
    PVOID volatile g_consumedAnimation = nullptr;
    PVOID volatile g_consumedSsv = nullptr;
    PVOID volatile g_consumedInfo = nullptr;
    PVOID volatile g_originalMovieAdvance = nullptr;
    LONG volatile g_movieCaptureArmed = 0;
    LONG volatile g_movieCaptureArmedFrame = -1;
    LONG volatile g_movieCaptureReady = 0;
    LONG volatile g_compatibilityMask = -1;
    LONG volatile g_fastForwardCompatibility = -1;
    PVOID volatile g_originalAvcodecOpen2 = nullptr;
    PVOID volatile g_originalAvSeekFrame = nullptr;
    LONG volatile g_decoderThreadCount = 0;
    LONG volatile g_decoderOpenCount = 0;
    LONG volatile g_lastThreadOptionResult = (-2147483647L - 1);
    LONG volatile g_skippedScaleCount = 0;
    PVOID volatile g_decodeScaleThunk = nullptr;
    PVOID volatile g_decoderWorkerTargetThunk = nullptr;
    PVOID volatile g_audioWriteThunk = nullptr;
    PVOID volatile g_originalBpkSoundWrite = nullptr;
    PVOID volatile g_wmvAudioWriteThunk = nullptr;
    PVOID volatile g_originalBpkSoundRawWrite = nullptr;
    PVOID volatile g_decoderCatchupVideo = nullptr;
    LONG volatile g_decoderCatchupTargetFrame = -1;
    LONG volatile g_lastDecoderCatchupDistance = 0;
    LONG volatile g_lastDroppedVideoFrames = 0;
    LONG volatile g_codecFlushCount = 0;
    LONG volatile g_alphaResetCount = 0;
    LONG volatile g_lastAlphaFrameBeforeReset = -1;
    LONG volatile g_alphaCheckpointRestoreCount = 0;
    LONG volatile g_lastAlphaCheckpointFrame = -1;
    LONGLONG volatile g_alphaCheckpointClipKey = 0;
    LONGLONG volatile g_persistentAlphaCacheLimit =
        DefaultPersistentAlphaCacheBytes;
    LONG volatile g_keyframeSeekCount = 0;
    LONG volatile g_lastKeyframeSeekFrame = -1;
    LONG volatile g_offsetsResolved = 0;
    LONG volatile g_offsetResolverMask = 0;
    LONG volatile g_movieResolverMask = 0;
    LONG volatile g_audioResolverMask = 0;
    LONG volatile g_fastDecodeResolverMask = 0;
    LONG volatile g_fastDecodeInstallStage = 0;
    LONG volatile g_playerLocked = 0;
    LONG volatile g_playerClickThrough = 0;
    LONG volatile g_playerWheelResize = 0;
    LONG volatile g_playerWheelWhileLocked = 0;
    LONG volatile g_playerWheelDelta = 0;
    LONG volatile g_playerVolumeWheelDelta = 0;
    LONG volatile g_playerSmallSizePercent = 0;
    LONG volatile g_playerLargeSizePercent = 0;
    LONG volatile g_playerMode = 2;
    PVOID volatile g_pointerMovieWindow = nullptr;
    LONG volatile g_pointerOverVisiblePixel = 0;
    LONGLONG volatile g_lastHitTestRefreshTick = 0;
    std::uintptr_t g_liveVtableRva = 0;
    PVOID volatile g_liveObject = nullptr;
    std::uintptr_t g_fullScreenVtableRva = 0;
    std::uintptr_t g_fullScreenRendererVtableRva = 0;
    std::uintptr_t g_sceneVtableRva = 0;
    std::uintptr_t g_cardSequencerVtableRva = 0;
    PVOID volatile g_fullScreenObject = nullptr;
    PVOID volatile g_cardSequencerObject = nullptr;
    PVOID volatile g_originalQMetaActivate = nullptr;
    PVOID volatile g_originalFsClipNodeStartNextShow = nullptr;
    PVOID volatile g_originalFsClipNodeNextShowClip = nullptr;
    PVOID volatile g_originalQOpenGLShaderProgramBind = nullptr;
    QOpenGLShaderUniformLocation g_qOpenGLShaderUniformLocation = nullptr;
    QOpenGLShaderSetUniform1 g_qOpenGLShaderSetUniform1 = nullptr;
    QOpenGLShaderSetUniformInt g_qOpenGLShaderSetUniformInt = nullptr;
    QOpenGLShaderSetUniform2 g_qOpenGLShaderSetUniform2 = nullptr;
    QOpenGLShaderSetUniform4 g_qOpenGLShaderSetUniform4 = nullptr;
    WglGetCurrentContext g_wglGetCurrentContext = nullptr;
    WglGetProcAddress g_wglGetProcAddress = nullptr;
    GlGenTextures g_glGenTextures = nullptr;
    GlBindTexture g_glBindTexture = nullptr;
    GlIsTexture g_glIsTexture = nullptr;
    GlGetIntegerv g_glGetIntegerv = nullptr;
    GlPixelStorei g_glPixelStorei = nullptr;
    GlTexParameteri g_glTexParameteri = nullptr;
    GlTexImage2D g_glTexImage2D = nullptr;
    GlTexSubImage2D g_glTexSubImage2D = nullptr;
    SRWLOCK g_fullScreenStateLock = SRWLOCK_INIT;
    SRWLOCK g_fullScreenHookLock = SRWLOCK_INIT;
    SRWLOCK g_fullScreenQueueOperationLock = SRWLOCK_INIT;
    SRWLOCK g_fullScreenShaderDataLock = SRWLOCK_INIT;
    FullScreenShaderDataPacket g_fullScreenShaderData;
    LONG volatile g_fullScreenShaderDataSet = 0;
    SRWLOCK g_fullScreenShaderTextureLock = SRWLOCK_INIT;
    std::vector<FullScreenShaderTextureState> g_fullScreenShaderTextures;
    LONG volatile g_fullScreenShaderTextureSet = 0;
    std::mutex g_fullScreenShaderGlTextureLock;
    std::vector<FullScreenShaderGlTexture> g_fullScreenShaderGlTextures;
    std::mutex g_fullScreenShaderPixelPoolLock;
    std::vector<std::shared_ptr<std::vector<unsigned char>>>
        g_fullScreenShaderPixelPool;
    std::vector<std::string> g_fullScreenClips;
    struct FullScreenQueueEntry
    {
        std::string card;
        std::string clip;
    };
    std::vector<FullScreenQueueEntry> g_fullScreenQueue;
    struct FullScreenSlot
    {
        DWORD id = 0;
        void* node = nullptr;
        void* parent = nullptr;
        std::string source;
        std::string card;
        std::string clip;
        bool active = false;
    };
    struct QtListData
    {
        LONG ref;
        int alloc;
        int begin;
        int end;
        void* values[1];
    };
    struct FullScreenReplacement
    {
        void* node = nullptr;
        void* playableCard = nullptr;
        std::string clip;
        ULONGLONG deadline = 0;
    };
    std::vector<FullScreenSlot> g_fullScreenSlots;
    DWORD g_nextFullScreenSlotId = 1;
    void* g_fullScreenSceneParent = nullptr;
    FullScreenReplacement g_fullScreenReplacement;
    enum class FullScreenQueueOperationKind
    {
        None,
        Executing,
        Insert,
        Add,
        Remove
    };
    struct FullScreenQueueOperation
    {
        FullScreenQueueOperationKind kind =
            FullScreenQueueOperationKind::None;
        std::string card;
        std::string clip;
        std::size_t index = 0;
        HANDLE completed = nullptr;
        HRESULT result = E_PENDING;

        ~FullScreenQueueOperation()
        {
            if (completed != nullptr)
                CloseHandle(completed);
        }
    };
    std::shared_ptr<FullScreenQueueOperation>
        g_fullScreenQueueOperation;
    thread_local void* g_activeFullScreenReplacementNode = nullptr;
    thread_local std::string g_activeFullScreenReplacementClip;
    LONG volatile g_bridgePinned = 0;
    PVOID volatile g_audioSeekMovie = nullptr;
    LONGLONG volatile g_audioPlaybackRateBits =
        static_cast<LONGLONG>(0x3FF0000000000000ULL);
    LONG volatile g_audioResetCount = 0;
    LONG volatile g_audioSuspendCount = 0;
    LONG volatile g_audioResumeCount = 0;
    PVOID volatile g_audioSuppressedSound = nullptr;
    PVOID volatile g_audioResetPendingSound = nullptr;
    PVOID volatile g_audioReleaseAfterResetSound = nullptr;
    LONG volatile g_audioResumeAfterReset = 0;
    LONG volatile g_audioWritesInFlight = 0;
    LONG volatile g_audioWritesSkipped = 0;
    SRWLOCK g_decodePatchLock = SRWLOCK_INIT;
    SRWLOCK g_offsetResolveLock = SRWLOCK_INIT;
    SRWLOCK g_alphaCheckpointLock = SRWLOCK_INIT;
    SRWLOCK g_seekReadinessLock = SRWLOCK_INIT;
    SRWLOCK g_audioControlLock = SRWLOCK_INIT;
    SRWLOCK g_wmvRestartLock = SRWLOCK_INIT;
    SRWLOCK g_movieWindowLock = SRWLOCK_INIT;
    INIT_ONCE g_movieWindowWatcherOnce = INIT_ONCE_STATIC_INIT;
    INIT_ONCE g_wmvClockWorkerOnce = INIT_ONCE_STATIC_INIT;
    HMODULE g_bridgeModule = nullptr;
    HWINEVENTHOOK g_movieWindowEventHook = nullptr;
    HHOOK g_movieMouseHook = nullptr;
    UINT g_wheelResizeMessage = 0;
    UINT g_volumeWheelMessage = 0;
    LONG volatile g_movieWindowWatcherResult = E_PENDING;
    UINT g_movieWindowProbeMessage = 0;
    PVOID volatile g_fastForwardMovie = nullptr;
    LONG volatile g_fastForwardTargetFrame = -1;
    void* g_wmvRateAnimation = nullptr;
    void* g_wmvRateVideo = nullptr;
    double g_wmvRate = 1.0;
    bool g_wmvUserClock = false;
    PVOID volatile g_wmvClockMovie = nullptr;
    PVOID volatile g_wmvClockVideo = nullptr;
    PVOID volatile g_wmvClockAdvanced = nullptr;
    LONG volatile g_wmvClockFramesPerSecond = 0;
    LONG volatile g_wmvClockTotalFrames = 0;
    LONG volatile g_wmvClockDeliveredUntilFrame = 0;
    LONG volatile g_wmvClockReleaseFrame = -1;
    LONG volatile g_wmvClockStreaming = 0;
    LONG volatile g_wmvClockResult = E_PENDING;
    LONG volatile g_wmvClockWorkerResult = E_PENDING;

    struct MovieWindowSubclass
    {
        HWND window = nullptr;
        WNDPROC original = nullptr;
    };

    // ponytail: fixed storage keeps window callbacks allocation-free; raise
    // this only if vghd ever creates more than 64 movie windows.
    constexpr int MaximumMovieWindows = 64;
    constexpr LRESULT MovieWindowProbeResult = 0x514C4F43;
    constexpr wchar_t MovieWindowClickThroughProperty[] =
        L"IStripperQuickPlayer.ClickThrough.v61";
    MovieWindowSubclass g_movieWindows[MaximumMovieWindows] = {};

    struct AlphaCheckpoint
    {
        int frame = -1;
        int bucket = -1;
        std::size_t outputSize = 0;
        unsigned char* data = nullptr;
    };

#pragma pack(push, 1)
    struct PersistentAlphaHeader
    {
        std::uint32_t magic = PersistentAlphaMagic;
        std::uint16_t version = PersistentAlphaVersion;
        std::uint16_t compression = 0;
        std::uint64_t clipKey = 0;
        std::int32_t frame = -1;
        std::int32_t bucket = -1;
        std::int32_t width = 0;
        std::int32_t height = 0;
        std::int32_t framesPerSecond = 0;
        std::int32_t totalFrames = 0;
        std::uint64_t outputSize = 0;
        std::uint64_t stateSize = 0;
        std::int64_t pointer1 = 0;
        std::int64_t pointer2 = 0;
        std::uint64_t checksum = 0;
        std::uint64_t storedSize = 0;
    };
#pragma pack(pop)

    struct PersistentAlphaWriteJob
    {
        PersistentAlphaHeader header;
        void* animation = nullptr;
        void* ssv = nullptr;
        void* info = nullptr;
        void* output = nullptr;
    };

    AlphaCheckpoint g_alphaCheckpoints[MaximumAlphaCheckpoints] = {};
    int g_alphaCheckpointCount = 0;
    std::size_t g_alphaCheckpointBytes = 0;
    void* g_alphaCheckpointAnimation = nullptr;
    void* g_alphaCheckpointSsv = nullptr;
    void* g_alphaCheckpointInfo = nullptr;
    void* g_alphaCheckpointOutput = nullptr;
    int g_alphaCheckpointWidth = 0;
    int g_alphaCheckpointHeight = 0;
    void* g_seekReadinessAnimation = nullptr;
    void* g_seekReadinessSsv = nullptr;
    void* g_seekReadinessInfo = nullptr;
    void* g_seekReadinessOutput = nullptr;
    int g_seekReadinessMovieFrame = -1;
    int g_seekReadinessAlphaFrame = -1;
    int g_seekReadinessAlphaGeneration = -1;
    int g_seekReadinessAlphaProgress = 0;
    void* g_seekReadinessWmvReader = nullptr;
    int g_seekReadinessWmvFrame = -1;
    int g_seekReadinessWmvProgress = 0;

    HRESULT PinBridge()
    {
        if (InterlockedCompareExchange(&g_bridgePinned, 0, 0) != 0)
        {
            return BridgeSuccess;
        }

        HMODULE pinnedModule = nullptr;
        if (!GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                reinterpret_cast<LPCWSTR>(&PinBridge), &pinnedModule))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        InterlockedExchange(&g_bridgePinned, 1);
        return BridgeSuccess;
    }

    bool IsMovieWindow(HWND window)
    {
        DWORD processId = 0;
        wchar_t className[128] = {};
        return window != nullptr &&
            GetWindowThreadProcessId(window, &processId) != 0 &&
            processId == GetCurrentProcessId() &&
            GetClassNameW(window, className,
                static_cast<int>(_countof(className))) > 0 &&
            std::wcsstr(className, L"QWindowToolSaveBitsOwnDC") != nullptr &&
            (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_LAYERED) != 0 &&
            GetWindow(window, GW_OWNER) == nullptr;
    }

    BOOL CALLBACK FindVisibleMovieWindow(HWND window, LPARAM parameter)
    {
        if (!IsWindowVisible(window) || !IsMovieWindow(window))
        {
            return TRUE;
        }
        *reinterpret_cast<HWND*>(parameter) = window;
        return FALSE;
    }

    struct MovieWindowAtPoint
    {
        POINT point = {};
        HWND window = nullptr;
        bool visiblePixel = false;
    };

    bool IsPointOverVisibleMoviePixel(HWND window, POINT point);

    MovieWindowAtPoint FindVisibleMovieWindowAtPoint(POINT point)
    {
        HWND windows[MaximumMovieWindows] = {};
        AcquireSRWLockShared(&g_movieWindowLock);
        for (int index = 0; index < MaximumMovieWindows; ++index)
        {
            windows[index] = g_movieWindows[index].window;
        }
        ReleaseSRWLockShared(&g_movieWindowLock);

        for (HWND window : windows)
        {
            RECT bounds = {};
            if (window == nullptr || !IsWindowVisible(window) ||
                !IsMovieWindow(window) ||
                FAILED(DwmGetWindowAttribute(window,
                    DWMWA_EXTENDED_FRAME_BOUNDS, &bounds,
                    sizeof(bounds))) || !PtInRect(&bounds, point))
            {
                continue;
            }
            return { point, window,
                IsPointOverVisibleMoviePixel(window, point) };
        }
        return { point, nullptr, false };
    }

    struct QtGenericArgument
    {
        const void* data = nullptr;
        const char* name = nullptr;
    };

    using QObjectInherits = bool(__cdecl*)(const void* object,
        const char* className);
    using QMetaInvoke = bool(__cdecl*)(void* object, const char* member,
        QtGenericArgument, QtGenericArgument, QtGenericArgument,
        QtGenericArgument, QtGenericArgument, QtGenericArgument,
        QtGenericArgument, QtGenericArgument, QtGenericArgument,
        QtGenericArgument);

    struct QtObjectFunctions
    {
        QObjectInherits inherits = nullptr;
        QMetaInvoke invoke = nullptr;
    };

    void* FindQtObject(const QtObjectFunctions& qt,
        std::uintptr_t& vtableRva, PVOID volatile* cachedObject,
        const char* decoratedClassName, const char* className);
    int RefreshDressingRoomDataUrls(const QtObjectFunctions& qt);

    int __cdecl DressingRoomAvformatOpenInput(void** context,
        const char* url, void* format, void* options)
    {
        if (url != nullptr &&
            std::strstr(url, "/fileaccess/dressingroom/") != nullptr)
        {
            DressingRoomLog("FFmpeg captured Dressing Room URL (redacted)");
            AcquireSRWLockExclusive(&g_dressingRoomUrlLock);
            g_dressingRoomUrl.assign(url);
            ReleaseSRWLockExclusive(&g_dressingRoomUrlLock);
        }
        return g_originalAvformatOpenInput(context, url, format, options);
    }

    HRESULT InstallDressingRoomUrlHook()
    {
        if (InterlockedCompareExchange(&g_dressingRoomHookInstalled, 0, 0) != 0)
        {
            DressingRoomLog("FFmpeg hook already installed");
            return BridgeSuccess;
        }
        HMODULE avformat = GetModuleHandleW(L"avformat-57.dll");
        void* target = avformat == nullptr ? nullptr :
            reinterpret_cast<void*>(GetProcAddress(avformat, "avformat_open_input"));
        if (target == nullptr)
        {
            DressingRoomLog("FFmpeg target unavailable module=%p target=%p",
                avformat, target);
            return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
        }
        MH_STATUS status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED)
            return E_FAIL;
        status = MH_CreateHook(target,
            reinterpret_cast<void*>(&DressingRoomAvformatOpenInput),
            reinterpret_cast<void**>(&g_originalAvformatOpenInput));
        if (status != MH_OK && status != MH_ERROR_ALREADY_CREATED)
            return E_FAIL;
        status = MH_EnableHook(target);
        if (status != MH_OK && status != MH_ERROR_ENABLED)
            return E_FAIL;
        InterlockedExchange(&g_dressingRoomHookInstalled, 1);
        DressingRoomLog("FFmpeg hook installed");
        return BridgeSuccess;
    }

    bool FindCachedDressingRoomUrl(const char* resourceName)
    {
        if (resourceName == nullptr || *resourceName == '\0')
            return false;
        // The URL includes a model folder between /dressingroom/ and the
        // resource name (for example /dressingroom/toree/toree_take4).
        const std::string marker = std::string(resourceName) + "?jwt=";
        std::wstring wideMarker(marker.begin(), marker.end());
        const auto asciiSearcher = std::boyer_moore_horspool_searcher(
            marker.begin(), marker.end());
        const auto wideSearcher = std::boyer_moore_horspool_searcher(
            wideMarker.begin(), wideMarker.end());
        SYSTEM_INFO systemInfo = {};
        GetSystemInfo(&systemInfo);
        const ULONGLONG started = GetTickCount64();
        std::size_t scannedRegions = 0;
        std::size_t scannedBytes = 0;
        void* cachedRegion = InterlockedCompareExchangePointer(
            &g_dressingRoomUrlRegion, nullptr, nullptr);
        for (int pass = cachedRegion == nullptr ? 1 : 0; pass < 2; ++pass)
        {
          auto cursor = pass == 0 ? static_cast<unsigned char*>(cachedRegion) :
              static_cast<unsigned char*>(systemInfo.lpMinimumApplicationAddress);
          const auto maximum = pass == 0 ? cursor + 1 :
              static_cast<unsigned char*>(systemInfo.lpMaximumApplicationAddress);
          while (cursor < maximum)
          {
            MEMORY_BASIC_INFORMATION memory = {};
            if (VirtualQuery(cursor, &memory, sizeof(memory)) != sizeof(memory))
                break;
            auto next = static_cast<unsigned char*>(memory.BaseAddress) +
                memory.RegionSize;
            const DWORD blocked = PAGE_GUARD | PAGE_NOACCESS;
            const DWORD writable = PAGE_READWRITE | PAGE_WRITECOPY |
                PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            if (memory.State == MEM_COMMIT &&
                memory.Type == MEM_PRIVATE &&
                (pass == 0 || memory.BaseAddress != cachedRegion) &&
                (memory.Protect & blocked) == 0 &&
                (memory.Protect & writable) != 0 &&
                memory.RegionSize > marker.size())
            {
                ++scannedRegions;
                scannedBytes += memory.RegionSize;
                const char* begin = static_cast<const char*>(memory.BaseAddress);
                const char* end = begin + memory.RegionSize;
                const char* match = std::search(begin, end, asciiSearcher);
                while (match != end)
                {
                    const char* urlStart = match;
                    const char* lower = match - (std::min<std::ptrdiff_t>)(
                        match - begin, 512);
                    while (urlStart > lower && *(urlStart - 1) >= 0x21 &&
                        *(urlStart - 1) <= 0x7e)
                        --urlStart;
                    if (std::strncmp(urlStart, "http://", 7) == 0 ||
                        std::strncmp(urlStart, "https://", 8) == 0)
                    {
                        const char* urlEnd = match + marker.size();
                        while (urlEnd < end && *urlEnd >= 0x21 &&
                            *urlEnd <= 0x7e && *urlEnd != '"' &&
                            *urlEnd != '\'')
                            ++urlEnd;
                        AcquireSRWLockExclusive(&g_dressingRoomUrlLock);
                        g_dressingRoomUrl.assign(urlStart, urlEnd);
                        ReleaseSRWLockExclusive(&g_dressingRoomUrlLock);
                        InterlockedExchangePointer(&g_dressingRoomUrlRegion,
                            memory.BaseAddress);
                        DressingRoomLog("Cached URL found in ASCII (value redacted) regions=%zu bytes=%zu elapsedMs=%llu",
                            scannedRegions, scannedBytes,
                            static_cast<unsigned long long>(GetTickCount64() - started));
                        return true;
                    }
                    match = std::search(match + 1, end, asciiSearcher);
                }

                const wchar_t* wideBegin = reinterpret_cast<const wchar_t*>(
                    (reinterpret_cast<std::uintptr_t>(begin) + 1) & ~std::uintptr_t(1));
                const wchar_t* wideEnd = reinterpret_cast<const wchar_t*>(
                    reinterpret_cast<std::uintptr_t>(end) & ~std::uintptr_t(1));
                const wchar_t* wideMatch = std::search(wideBegin, wideEnd,
                    wideSearcher);
                while (wideMatch != wideEnd)
                {
                    const wchar_t* urlStart = wideMatch;
                    const wchar_t* lower = wideMatch -
                        (std::min<std::ptrdiff_t>)(wideMatch - wideBegin, 512);
                    while (urlStart > lower && *(urlStart - 1) >= 0x21 &&
                        *(urlStart - 1) <= 0x7e)
                        --urlStart;
                    if (std::wcsncmp(urlStart, L"http://", 7) == 0 ||
                        std::wcsncmp(urlStart, L"https://", 8) == 0)
                    {
                        const wchar_t* urlEnd = wideMatch + wideMarker.size();
                        while (urlEnd < wideEnd && *urlEnd >= 0x21 &&
                            *urlEnd <= 0x7e && *urlEnd != L'"' &&
                            *urlEnd != L'\'')
                            ++urlEnd;
                        int byteCount = WideCharToMultiByte(CP_UTF8, 0,
                            urlStart, static_cast<int>(urlEnd - urlStart),
                            nullptr, 0, nullptr, nullptr);
                        if (byteCount > 0)
                        {
                            std::string url(static_cast<std::size_t>(byteCount), '\0');
                            WideCharToMultiByte(CP_UTF8, 0, urlStart,
                                static_cast<int>(urlEnd - urlStart), url.data(),
                                byteCount, nullptr, nullptr);
                            AcquireSRWLockExclusive(&g_dressingRoomUrlLock);
                            g_dressingRoomUrl = std::move(url);
                            ReleaseSRWLockExclusive(&g_dressingRoomUrlLock);
                            InterlockedExchangePointer(&g_dressingRoomUrlRegion,
                                memory.BaseAddress);
                            DressingRoomLog("Cached URL found in UTF-16 (value redacted) regions=%zu bytes=%zu elapsedMs=%llu",
                                scannedRegions, scannedBytes,
                                static_cast<unsigned long long>(GetTickCount64() - started));
                            return true;
                        }
                    }
                    wideMatch = std::search(wideMatch + 1, wideEnd,
                        wideSearcher);
                }
            }
            if (next <= cursor)
                break;
            cursor = next;
          }
        }
        DressingRoomLog("No cached URL marker found for resource=%s regions=%zu bytes=%zu elapsedMs=%llu",
            resourceName, scannedRegions, scannedBytes,
            static_cast<unsigned long long>(GetTickCount64() - started));
        return false;
    }

    bool CacheDressingRoomUrlRegion()
    {
        static const std::string marker = "/dressingroom/";
        static const std::wstring wideMarker(marker.begin(), marker.end());
        const auto asciiSearcher = std::boyer_moore_horspool_searcher(
            marker.begin(), marker.end());
        const auto wideSearcher = std::boyer_moore_horspool_searcher(
            wideMarker.begin(), wideMarker.end());
        SYSTEM_INFO systemInfo = {};
        GetSystemInfo(&systemInfo);
        const ULONGLONG started = GetTickCount64();
        std::size_t scannedRegions = 0;
        std::size_t scannedBytes = 0;
        auto cursor = static_cast<unsigned char*>(
            systemInfo.lpMinimumApplicationAddress);
        const auto maximum = static_cast<unsigned char*>(
            systemInfo.lpMaximumApplicationAddress);
        while (cursor < maximum)
        {
            MEMORY_BASIC_INFORMATION memory = {};
            if (VirtualQuery(cursor, &memory, sizeof(memory)) != sizeof(memory))
                break;
            auto next = static_cast<unsigned char*>(memory.BaseAddress) +
                memory.RegionSize;
            const DWORD blocked = PAGE_GUARD | PAGE_NOACCESS;
            const DWORD writable = PAGE_READWRITE | PAGE_WRITECOPY |
                PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            if (memory.State == MEM_COMMIT && memory.Type == MEM_PRIVATE &&
                (memory.Protect & blocked) == 0 &&
                (memory.Protect & writable) != 0 &&
                memory.RegionSize > marker.size())
            {
                ++scannedRegions;
                scannedBytes += memory.RegionSize;
                const char* begin = static_cast<const char*>(memory.BaseAddress);
                const char* end = begin + memory.RegionSize;
                const bool asciiFound = std::search(begin, end,
                    asciiSearcher) != end;
                const wchar_t* wideBegin = reinterpret_cast<const wchar_t*>(
                    (reinterpret_cast<std::uintptr_t>(begin) + 1) &
                    ~std::uintptr_t(1));
                const wchar_t* wideEnd = reinterpret_cast<const wchar_t*>(
                    reinterpret_cast<std::uintptr_t>(end) &
                    ~std::uintptr_t(1));
                const bool wideFound = !asciiFound && std::search(wideBegin,
                    wideEnd, wideSearcher) != wideEnd;
                if (asciiFound || wideFound)
                {
                    InterlockedExchangePointer(&g_dressingRoomUrlRegion,
                        memory.BaseAddress);
                    DressingRoomLog("Background URL region cached regions=%zu bytes=%zu elapsedMs=%llu",
                        scannedRegions, scannedBytes,
                        static_cast<unsigned long long>(
                            GetTickCount64() - started));
                    return true;
                }
            }
            if (next <= cursor)
                break;
            cursor = next;
        }
        DressingRoomLog("Background URL region not found regions=%zu bytes=%zu elapsedMs=%llu",
            scannedRegions, scannedBytes,
            static_cast<unsigned long long>(GetTickCount64() - started));
        return false;
    }

    HRESULT RequestDressingRoomUrl(int dressingRoomId)
    {
        HRESULT hook = InstallDressingRoomUrlHook();
        if (FAILED(hook))
            return hook;
        AcquireSRWLockExclusive(&g_dressingRoomUrlLock);
        g_dressingRoomUrl.clear();
        ReleaseSRWLockExclusive(&g_dressingRoomUrlLock);
        InterlockedExchange64(&g_dressingRoomLastScanStarted, 0);
        InterlockedExchange(&g_dressingRoomCacheScanPending, 1);

        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(core == nullptr ? nullptr :
                GetProcAddress(core, "?inherits@QObject@@QEBA_NPEBD@Z")),
            reinterpret_cast<QMetaInvoke>(core == nullptr ? nullptr :
                GetProcAddress(core,
                    "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
                    "VQGenericArgument@@222222222@Z"))
        };
        if (qt.inherits == nullptr || qt.invoke == nullptr)
            return E_NOINTERFACE;
        int refreshed = RefreshDressingRoomDataUrls(qt);
        DressingRoomLog("updateUrls invoked=%d", refreshed);
        if (refreshed > 0)
        {
            return BridgeSuccess;
        }
        struct Candidate { std::uintptr_t* vtable; PVOID volatile* object;
            const char* decorated; const char* className; };
        static std::uintptr_t dressingRoomModelVtable = 0;
        static PVOID volatile dressingRoomModelObject = nullptr;
        static std::uintptr_t collectionModelVtable = 0;
        static PVOID volatile collectionModelObject = nullptr;
        Candidate candidates[] = {
            { &dressingRoomModelVtable, &dressingRoomModelObject,
                ".?AVDressingRoomModel@ViewModel@@", "ViewModel::DressingRoomModel" },
            { &collectionModelVtable, &collectionModelObject,
                ".?AVCollectionDressingRoomProxyModel@ViewModel@@",
                "ViewModel::CollectionDressingRoomProxyModel" }
        };
        const QtGenericArgument argument = { &dressingRoomId, "int" };
        const QtGenericArgument empty = {};
        for (const Candidate& candidate : candidates)
        {
            void* object = FindQtObject(qt, *candidate.vtable,
                candidate.object, candidate.decorated, candidate.className);
            if (object == nullptr)
                continue;
            for (const char* method : { "requestOpenDressingRoomPIP", "openDressingRoomPIP" })
                if (qt.invoke(object, method, argument, empty, empty, empty,
                    empty, empty, empty, empty, empty, empty))
                    return BridgeSuccess;
        }
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }

    unsigned char* ImageBase();
    const IMAGE_NT_HEADERS64* ImageHeaders();
    bool IsReadable(const void* address, std::size_t length);
    std::uintptr_t FindVtableRva(const char* decoratedClassName);
    std::uintptr_t RvaFromAddress(const void* address);
    const unsigned char* FindSequence(const unsigned char* start,
        std::size_t length, const unsigned char* sequence,
        std::size_t sequenceLength);
    unsigned char* FindUniqueFunction(const unsigned char* signature,
        std::size_t signatureLength, int kind);
    bool ResolveFullScreenLayout(unsigned char* startNextShow);
    bool CompleteFullScreenQueueOperation(void* sequencer);
    bool OffsetProfilePath(wchar_t (&path)[MAX_PATH]);
    void SaveResolvedOffsets(const wchar_t* profilePath);
    bool IsQtObject(const QtObjectFunctions& qt, void* object,
        const void* expectedVtable, const char* className);

    void* FindQtObject(const QtObjectFunctions& qt,
        std::uintptr_t& vtableRva, PVOID volatile* cachedObject,
        const char* decoratedClassName, const char* className)
    {
        const auto headers = ImageHeaders();
        auto base = ImageBase();
        if (vtableRva == 0)
            vtableRva = FindVtableRva(decoratedClassName);
        if (headers == nullptr || base == nullptr || vtableRva == 0)
            return nullptr;

        const void* expectedVtable = base + vtableRva;
        void* cached = InterlockedCompareExchangePointer(
            cachedObject, nullptr, nullptr);
        if (IsQtObject(qt, cached, expectedVtable, className))
            return cached;
        InterlockedExchangePointer(cachedObject, nullptr);

        const auto sections = IMAGE_FIRST_SECTION(headers);
        for (unsigned index = 0;
            index < headers->FileHeader.NumberOfSections; ++index)
        {
            if ((sections[index].Characteristics & IMAGE_SCN_MEM_WRITE) == 0)
                continue;
            auto start = base + sections[index].VirtualAddress;
            const std::size_t size = sections[index].Misc.VirtualSize;
            for (std::size_t offset = 0;
                offset + sizeof(void*) <= size; offset += sizeof(void*))
            {
                auto slot = reinterpret_cast<void**>(start + offset);
                if (IsQtObject(qt, slot, expectedVtable, className))
                {
                    InterlockedExchangePointer(cachedObject, slot);
                    return slot;
                }
                if (IsQtObject(qt, *slot, expectedVtable, className))
                {
                    InterlockedExchangePointer(cachedObject, *slot);
                    return *slot;
                }
            }
        }
        return nullptr;
    }

    bool IsQtObject(const QtObjectFunctions& qt, void* object,
        const void* expectedVtable, const char* className)
    {
        if (object == nullptr)
        {
            return false;
        }
        MEMORY_BASIC_INFORMATION objectMemory = {};
        if (VirtualQuery(object, &objectMemory, sizeof(objectMemory)) !=
                sizeof(objectMemory) || objectMemory.State != MEM_COMMIT ||
            (objectMemory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0 ||
            reinterpret_cast<std::uintptr_t>(object) + sizeof(void*) * 2 >
                reinterpret_cast<std::uintptr_t>(objectMemory.BaseAddress) +
                    objectMemory.RegionSize)
        {
            return false;
        }
        __try
        {
            if (*reinterpret_cast<void**>(object) != expectedVtable)
            {
                return false;
            }
            void* privateData = *(reinterpret_cast<void**>(object) + 1);
            MEMORY_BASIC_INFORMATION privateMemory = {};
            if (privateData == nullptr ||
                VirtualQuery(privateData, &privateMemory,
                    sizeof(privateMemory)) != sizeof(privateMemory) ||
                privateMemory.State != MEM_COMMIT ||
                (privateMemory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0 ||
                reinterpret_cast<std::uintptr_t>(privateData) +
                    sizeof(void*) * 2 >
                    reinterpret_cast<std::uintptr_t>(
                        privateMemory.BaseAddress) + privateMemory.RegionSize)
            {
                return false;
            }
            return (*reinterpret_cast<void**>(privateData) == object ||
                    *(reinterpret_cast<void**>(privateData) + 1) == object) &&
                qt.inherits(object, className);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    void* FindLiveObject(const QtObjectFunctions& qt)
    {
        const auto headers = ImageHeaders();
        auto base = ImageBase();
        if (g_liveVtableRva == 0)
        {
            g_liveVtableRva = FindVtableRva(".?AVLive@@");
        }
        if (headers == nullptr || base == nullptr || g_liveVtableRva == 0)
        {
            return nullptr;
        }

        const void* expectedVtable = base + g_liveVtableRva;
        void* cached = InterlockedCompareExchangePointer(
            &g_liveObject, nullptr, nullptr);
        if (IsQtObject(qt, cached, expectedVtable, "Live"))
        {
            return cached;
        }
        InterlockedExchangePointer(&g_liveObject, nullptr);

        const auto sections = IMAGE_FIRST_SECTION(headers);
        for (unsigned index = 0; index < headers->FileHeader.NumberOfSections;
            ++index)
        {
            if ((sections[index].Characteristics & IMAGE_SCN_MEM_WRITE) == 0)
            {
                continue;
            }
            auto start = base + sections[index].VirtualAddress;
            const std::size_t size = sections[index].Misc.VirtualSize;
            for (std::size_t offset = 0;
                offset + sizeof(void*) <= size; offset += sizeof(void*))
            {
                auto slot = reinterpret_cast<void**>(start + offset);
                if (IsQtObject(qt, slot, expectedVtable, "Live"))
                {
                    InterlockedExchangePointer(&g_liveObject, slot);
                    return slot;
                }
                if (IsQtObject(qt, *slot, expectedVtable, "Live"))
                {
                    InterlockedExchangePointer(&g_liveObject, *slot);
                    return *slot;
                }
            }
        }
        return nullptr;
    }

    struct QtByteArray { void* data = nullptr; };
    using QMetaActivate = void(__cdecl*)(void*, const void*, int, void**);
    using QMetaClassName = const char*(__cdecl*)(const void*);
    using QMetaIndexOfSignal = int(__cdecl*)(const void*, const char*);
    using QMetaMethodOffset = int(__cdecl*)(const void*);
    using QStringToUtf8 = void*(__fastcall*)(const void*, QtByteArray*);
    using QByteArrayConstData = const char*(__cdecl*)(const void*);
    using QByteArrayDestroy = void(__cdecl*)(void*);

    bool IsSignal(const void* metaObject, int localSignalIndex,
        const char* signature)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto indexOfSignal = core == nullptr ? nullptr :
            reinterpret_cast<QMetaIndexOfSignal>(GetProcAddress(core,
                "?indexOfSignal@QMetaObject@@QEBAHPEBD@Z"));
        const auto methodOffset = core == nullptr ? nullptr :
            reinterpret_cast<QMetaMethodOffset>(GetProcAddress(core,
                "?methodOffset@QMetaObject@@QEBAHXZ"));
        return indexOfSignal != nullptr && methodOffset != nullptr &&
            indexOfSignal(metaObject, signature) - methodOffset(metaObject) ==
                localSignalIndex;
    }

    std::string QtStringUtf8(const void* value)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto toUtf8 = core == nullptr ? nullptr :
            reinterpret_cast<QStringToUtf8>(GetProcAddress(core,
                "?toUtf8@QString@@QEBA?AVQByteArray@@XZ"));
        const auto constData = core == nullptr ? nullptr :
            reinterpret_cast<QByteArrayConstData>(GetProcAddress(core,
                "?constData@QByteArray@@QEBAPEBDXZ"));
        const auto destroy = core == nullptr ? nullptr :
            reinterpret_cast<QByteArrayDestroy>(GetProcAddress(core,
                "??1QByteArray@@QEAA@XZ"));
        if (value == nullptr || toUtf8 == nullptr || constData == nullptr ||
            destroy == nullptr)
            return {};
        QtByteArray bytes;
        toUtf8(value, &bytes);
        const char* text = constData(&bytes);
        std::string result = text == nullptr ? "" : text;
        destroy(&bytes);
        return result;
    }

    std::string QtObjectName(const void* object)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        using QObjectObjectName = void*(__fastcall*)(const void*, void*);
        using QStringDestroy = void(__fastcall*)(void*);
        const auto objectName = core == nullptr ? nullptr :
            reinterpret_cast<QObjectObjectName>(GetProcAddress(core,
                "?objectName@QObject@@QEBA?AVQString@@XZ"));
        const auto destroy = core == nullptr ? nullptr :
            reinterpret_cast<QStringDestroy>(GetProcAddress(core,
                "??1QString@@QEAA@XZ"));
        if (object == nullptr || objectName == nullptr || destroy == nullptr)
            return {};
        void* name = nullptr;
        objectName(object, &name);
        std::string result = QtStringUtf8(&name);
        destroy(&name);
        return result;
    }

    bool ReadQtPointerList(void* list, std::size_t maximum,
        std::vector<void*>& values)
    {
        values.clear();
        __try
        {
            if (list == nullptr || !IsReadable(list, sizeof(void*)))
                return false;
            const auto data = *reinterpret_cast<QtListData**>(list);
            if (!IsReadable(data, offsetof(QtListData, values)) ||
                data->begin < 0 || data->end < data->begin ||
                data->alloc < data->end ||
                static_cast<std::size_t>(data->end - data->begin) > maximum ||
                !IsReadable(data, offsetof(QtListData, values) +
                    sizeof(void*) * data->end))
                return false;
            values.reserve(static_cast<std::size_t>(
                data->end - data->begin));
            for (int index = data->begin; index < data->end; ++index)
                values.push_back(data->values[index]);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            values.clear();
            return false;
        }
    }

    std::string CardTag(const std::string& clip);

    std::string PlayableCardTag(void* card)
    {
        auto base = ImageBase();
        if (card == nullptr || base == nullptr ||
            PlayableCardVtableRva == 0 || PlayableCardTagOffset == 0 ||
            !IsReadable(card, sizeof(void*)) ||
            *reinterpret_cast<void**>(card) !=
                base + PlayableCardVtableRva)
            return {};
        const auto tag = reinterpret_cast<unsigned char*>(card) +
            PlayableCardTagOffset;
        return IsReadable(tag, sizeof(void*)) ? QtStringUtf8(tag) :
            std::string();
    }

    std::string PlayableCardClip(void* card)
    {
        if (card == nullptr || PlayableCardClipsOffset == 0)
            return {};
        std::vector<void*> clips;
        if (!ReadQtPointerList(reinterpret_cast<unsigned char*>(card) +
                PlayableCardClipsOffset, 4096, clips) ||
            clips.empty())
            return {};
        void* clip = clips.front();
        return QtStringUtf8(&clip);
    }

    void* FindCardSequencer()
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(core == nullptr ? nullptr :
                GetProcAddress(core, "?inherits@QObject@@QEBA_NPEBD@Z")),
            nullptr
        };
        return qt.inherits == nullptr ? nullptr : FindQtObject(qt,
            g_cardSequencerVtableRva, &g_cardSequencerObject,
            ".?AVCardSequencer@Model@@", "Model::CardSequencer");
    }

    void* FindFullScreen()
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(core == nullptr ? nullptr :
                GetProcAddress(core, "?inherits@QObject@@QEBA_NPEBD@Z")),
            nullptr
        };
        return qt.inherits == nullptr ? nullptr : FindQtObject(qt,
            g_fullScreenVtableRva, &g_fullScreenObject,
            ".?AVFullScreen@@", "FullScreen");
    }

    void* FullScreenScene(void* node)
    {
        if (node == nullptr || FsClipNodeSceneOffset == 0)
            return nullptr;
        const auto field = reinterpret_cast<unsigned char*>(node) +
            FsClipNodeSceneOffset;
        return IsReadable(field, sizeof(void*)) ?
            *reinterpret_cast<void**>(field) : nullptr;
    }

    bool SynchronizeFullScreenScene(void* scene, void* openingNode = nullptr,
        const std::string& openingClip = {})
    {
        if (scene == nullptr || SceneNodesOffset == 0 ||
            FsClipNodePlayableCardOffset == 0)
            return false;
        std::vector<void*> nodes;
        if (!ReadQtPointerList(
                reinterpret_cast<unsigned char*>(scene) + SceneNodesOffset,
                256, nodes))
            return false;

        struct ObservedSlot
        {
            void* node;
            std::string source;
            std::string card;
        };
        std::vector<ObservedSlot> observed;
        auto base = ImageBase();
        const void* nodeVtable = base == nullptr || FsClipNodeVtableRva == 0 ?
            nullptr : base + FsClipNodeVtableRva;
        for (void* node : nodes)
        {
            if (node == nullptr || nodeVtable == nullptr ||
                !IsReadable(node, FsClipNodePlayableCardOffset +
                    sizeof(void*)) ||
                *reinterpret_cast<void**>(node) != nodeVtable)
                return false;
            void* card = *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(node) +
                    FsClipNodePlayableCardOffset);
            observed.push_back({ node, QtObjectName(node),
                PlayableCardTag(card) });
        }

        AcquireSRWLockExclusive(&g_fullScreenStateLock);
        for (FullScreenSlot& slot : g_fullScreenSlots)
            slot.active = false;
        g_fullScreenSceneParent = scene;
        for (const ObservedSlot& value : observed)
        {
            auto slot = std::find_if(g_fullScreenSlots.begin(),
                g_fullScreenSlots.end(), [&](const FullScreenSlot& candidate)
                {
                    return candidate.node == value.node;
                });
            if (slot == g_fullScreenSlots.end())
            {
                g_fullScreenSlots.push_back({ g_nextFullScreenSlotId++,
                    value.node, scene, value.source, value.card, "", true });
                slot = g_fullScreenSlots.end() - 1;
            }
            slot->parent = scene;
            slot->source = value.source;
            slot->active = true;
            if (value.node == openingNode && !openingClip.empty())
                slot->clip = openingClip;
            slot->card = slot->clip.empty() ? value.card :
                CardTag(slot->clip);
        }
        g_fullScreenClips.clear();
        for (const FullScreenSlot& slot : g_fullScreenSlots)
        {
            if (!slot.active || slot.parent != scene)
                continue;
            const std::string& value = slot.clip.empty() ? slot.card :
                slot.clip;
            if (!value.empty())
                g_fullScreenClips.push_back(value);
        }
        ReleaseSRWLockExclusive(&g_fullScreenStateLock);
        return true;
    }

    bool SynchronizeCurrentFullScreenScene()
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(core == nullptr ? nullptr :
                GetProcAddress(core, "?inherits@QObject@@QEBA_NPEBD@Z")),
            nullptr
        };
        auto base = ImageBase();
        void* fullScreen = qt.inherits == nullptr ? nullptr : FindFullScreen();
        if (base == nullptr || fullScreen == nullptr ||
            SceneNodesOffset == 0)
            return false;
        if (g_fullScreenRendererVtableRva == 0)
            g_fullScreenRendererVtableRva = FindVtableRva(
                ".?AVFullScreenRenderer@@");
        if (g_sceneVtableRva == 0)
            g_sceneVtableRva = FindVtableRva(".?AVScene@@");
        if (g_fullScreenRendererVtableRva == 0 || g_sceneVtableRva == 0)
            return false;

        void* renderer = nullptr;
        for (std::size_t offset = sizeof(void*); offset < 0x200;
            offset += sizeof(void*))
        {
            auto field = reinterpret_cast<unsigned char*>(fullScreen) + offset;
            if (!IsReadable(field, sizeof(void*)))
                break;
            void* candidate = *reinterpret_cast<void**>(field);
            if (IsQtObject(qt, candidate,
                    base + g_fullScreenRendererVtableRva,
                    "FullScreenRenderer"))
            {
                renderer = candidate;
                break;
            }
        }
        if (renderer == nullptr)
            return false;

        for (std::size_t offset = sizeof(void*); offset < 0x100;
            offset += sizeof(void*))
        {
            auto field = reinterpret_cast<unsigned char*>(renderer) + offset;
            if (!IsReadable(field, sizeof(void*)))
                break;
            void* scene = *reinterpret_cast<void**>(field);
            if (!IsQtObject(qt, scene, base + g_sceneVtableRva, "Scene"))
                continue;
            std::vector<void*> nodes;
            if (ReadQtPointerList(reinterpret_cast<unsigned char*>(scene) +
                    SceneNodesOffset, 256, nodes) && !nodes.empty())
                return SynchronizeFullScreenScene(scene);
        }
        return false;
    }

    bool IsFullScreenModeActive()
    {
        DWORD mode = 0;
        DWORD size = sizeof(mode);
        return RegGetValueW(HKEY_CURRENT_USER,
                L"Software\\Totem\\vghd\\player", L"playingMode",
                RRF_RT_REG_DWORD, nullptr, &mode, &size) == ERROR_SUCCESS &&
            mode == 3;
    }

    bool RefreshFullScreenQueue()
    {
        if (CardSequencerNextOffset == 0 ||
            NextCardPlayableCardOffset == 0)
            return false;
        void* sequencer = FindCardSequencer();
        if (sequencer == nullptr)
            return false;
        std::vector<void*> nextCards;
        if (!ReadQtPointerList(reinterpret_cast<unsigned char*>(sequencer) +
                CardSequencerNextOffset, 4096, nextCards))
            return false;
        std::vector<FullScreenQueueEntry> queue;
        queue.reserve(nextCards.size());
        for (void* nextCard : nextCards)
        {
            if (nextCard == nullptr || !IsReadable(nextCard,
                    NextCardPlayableCardOffset + sizeof(void*)))
                return false;
            void* card = *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(nextCard) +
                    NextCardPlayableCardOffset);
            queue.push_back({ PlayableCardTag(card),
                PlayableCardClip(card) });
        }
        AcquireSRWLockExclusive(&g_fullScreenStateLock);
        g_fullScreenQueue = std::move(queue);
        ReleaseSRWLockExclusive(&g_fullScreenStateLock);
        return true;
    }

    using VghdOperatorNew = void*(__fastcall*)(std::size_t);
    using PlayableCardExactConstructor = void*(__fastcall*)(
        void*, const void*, const void*, void*);
    using CardSequencerInsertNext = void(__fastcall*)(
        void*, int, const void*, int, int);
    using CardSequencerTakeNextAt = void*(__fastcall*)(void*, int, bool);
    using QtListDetach = void*(__fastcall*)(void*, int);
    using QtListDisposeData = void(__fastcall*)(void*);
    using QStringAnsiConstructor = void*(__fastcall*)(void*, const char*);
    using QStringDestructor = void(__fastcall*)(void*);
    using QObjectThread = void*(__fastcall*)(const void*);
    using QObjectMoveToThread = void(__fastcall*)(void*, void*);
    using QObjectDeleteLater = void(__fastcall*)(void*);
    using QStringAnsiAssignment = void*(__fastcall*)(void*, const char*);

    void DestroyPlayableCard(void* card)
    {
        if (card == nullptr || !IsReadable(card, sizeof(void*)))
            return;
        auto vtable = *reinterpret_cast<void***>(card);
        if (IsReadable(vtable, sizeof(void*)) && vtable[0] != nullptr)
            reinterpret_cast<void(__fastcall*)(void*, unsigned)>(
                vtable[0])(card, 1);
    }

    void DeletePlayableCardLater(void* card)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto deleteLater = core == nullptr ? nullptr :
            reinterpret_cast<QObjectDeleteLater>(GetProcAddress(core,
                "?deleteLater@QObject@@QEAAXXZ"));
        if (card != nullptr && deleteLater != nullptr)
            deleteLater(card);
    }

    bool MovePlayableCardToNodeThread(void* card, void* node)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto thread = core == nullptr ? nullptr :
            reinterpret_cast<QObjectThread>(GetProcAddress(core,
                "?thread@QObject@@QEBAPEAVQThread@@XZ"));
        const auto moveToThread = core == nullptr ? nullptr :
            reinterpret_cast<QObjectMoveToThread>(GetProcAddress(core,
                "?moveToThread@QObject@@QEAAXPEAVQThread@@@Z"));
        if (card == nullptr || node == nullptr || thread == nullptr ||
            moveToThread == nullptr)
            return false;
        void* targetThread = thread(node);
        if (targetThread == nullptr)
            return false;
        moveToThread(card, targetThread);
        return thread(card) == targetThread;
    }

    void* CreatePlayableCard(const std::string& card,
        const std::string& clip)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto constructString = core == nullptr ? nullptr :
            reinterpret_cast<QStringAnsiConstructor>(GetProcAddress(core,
                "??0QString@@QEAA@PEBD@Z"));
        const auto destroyString = core == nullptr ? nullptr :
            reinterpret_cast<QStringDestructor>(GetProcAddress(core,
                "??1QString@@QEAA@XZ"));
        auto base = ImageBase();
        if (constructString == nullptr || destroyString == nullptr ||
            base == nullptr || PlayableCardExactConstructorRva == 0 ||
            VghdOperatorNewRva == 0 || PlayableCardSize == 0)
            return nullptr;

        void* tag = nullptr;
        void* candidate = nullptr;
        constructString(&tag, card.c_str());
        constructString(&candidate, clip.c_str());
        void* memory = reinterpret_cast<VghdOperatorNew>(
            base + VghdOperatorNewRva)(PlayableCardSize);
        void* result = memory == nullptr ? nullptr :
            reinterpret_cast<PlayableCardExactConstructor>(
                base + PlayableCardExactConstructorRva)(memory, &tag,
                    &candidate, nullptr);
        if (memory == nullptr)
            destroyString(&candidate);
        destroyString(&tag);
        return result;
    }

    bool SetSceneExpectedCard(void* scene, void* playableCard)
    {
        __try
        {
            const std::size_t required = std::max({
                SceneExpectedPendingOffset + 1,
                SceneExpectedSelectionOffset + 1,
                SceneExpectedCardOffset + sizeof(void*),
                SceneExpectedModeOffset + sizeof(int) });
            if (!IsReadable(scene, required))
                return false;
            auto bytes = reinterpret_cast<unsigned char*>(scene);
            bytes[SceneExpectedPendingOffset] = 1;
            bytes[SceneExpectedSelectionOffset] = 1;
            *reinterpret_cast<void**>(bytes + SceneExpectedCardOffset) =
                playableCard;
            *reinterpret_cast<int*>(bytes + SceneExpectedModeOffset) = 0;
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    std::string PrepareFullScreenReplacement(void* node)
    {
        void* playableCard = nullptr;
        void* expiredCard = nullptr;
        std::string clip;
        AcquireSRWLockExclusive(&g_fullScreenStateLock);
        if (g_fullScreenReplacement.deadline < GetTickCount64())
        {
            expiredCard = g_fullScreenReplacement.playableCard;
            g_fullScreenReplacement = {};
        }
        if (g_fullScreenReplacement.node == node)
        {
            playableCard = g_fullScreenReplacement.playableCard;
            clip = std::move(g_fullScreenReplacement.clip);
            g_fullScreenReplacement = {};
        }
        ReleaseSRWLockExclusive(&g_fullScreenStateLock);
        DeletePlayableCardLater(expiredCard);
        if (playableCard == nullptr)
            return {};
        if (!SetSceneExpectedCard(FullScreenScene(node), playableCard))
        {
            DeletePlayableCardLater(playableCard);
            return {};
        }
        return clip;
    }

    void SetQtString(void* value, const std::string& text)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto assign = core == nullptr ? nullptr :
            reinterpret_cast<QStringAnsiAssignment>(GetProcAddress(core,
                "??4QString@@QEAAAEAV0@PEBD@Z"));
        if (value != nullptr && assign != nullptr)
            assign(value, text.c_str());
    }

    void PublishFullScreenState(bool discoverScene = false);

    using FsClipNodeStartNextShow = void*(__fastcall*)(void*, void*);
    using FsClipNodeNextShowClip = void*(__fastcall*)(void*, void*, bool);

    void* __fastcall CapturingFsClipNodeNextShowClip(
        void* node, void* result, bool selection)
    {
        const auto original = reinterpret_cast<FsClipNodeNextShowClip>(
            InterlockedCompareExchangePointer(
                &g_originalFsClipNodeNextShowClip, nullptr, nullptr));
        void* returned = original == nullptr ? result :
            original(node, result, selection);
        if (node == g_activeFullScreenReplacementNode &&
            !g_activeFullScreenReplacementClip.empty())
            SetQtString(result, g_activeFullScreenReplacementClip);
        return returned;
    }

    void* __fastcall CapturingFsClipNodeStartNextShow(
        void* node, void* result)
    {
        const std::string replacement = PrepareFullScreenReplacement(node);
        g_activeFullScreenReplacementNode = replacement.empty() ? nullptr :
            node;
        g_activeFullScreenReplacementClip = replacement;
        const auto original = reinterpret_cast<FsClipNodeStartNextShow>(
            InterlockedCompareExchangePointer(
                &g_originalFsClipNodeStartNextShow, nullptr, nullptr));
        void* returned = original == nullptr ? result : original(node, result);
        g_activeFullScreenReplacementClip.clear();
        g_activeFullScreenReplacementNode = nullptr;
        std::string clip = QtStringUtf8(result);
        if (!clip.empty())
        {
            const std::size_t separator = clip.find_last_of("\\/");
            if (separator != std::string::npos)
                clip.erase(0, separator + 1);
        }
        if (SynchronizeFullScreenScene(FullScreenScene(node), node, clip))
            PublishFullScreenState();
        return returned;
    }

    int RefreshDressingRoomDataUrls(const QtObjectFunctions& qt)
    {
        static std::uintptr_t vtableRva = 0;
        auto base = ImageBase();
        if (vtableRva == 0)
            vtableRva = FindVtableRva(
                ".?AVDressingRoomData@ViewModel@@");
        if (base == nullptr || vtableRva == 0)
            return 0;
        const void* expectedVtable = base + vtableRva;
        const QtGenericArgument empty = {};
        int invoked = 0;
        AcquireSRWLockShared(&g_dressingRoomObjectsLock);
        LONG cachedCount = g_dressingRoomObjectCount;
        for (LONG index = 0; index < cachedCount; ++index)
        {
            void* object = g_dressingRoomObjects[index];
            if (IsQtObject(qt, object, expectedVtable,
                "ViewModel::DressingRoomData") &&
                qt.invoke(object, "updateUrls", empty, empty, empty, empty,
                    empty, empty, empty, empty, empty, empty))
                ++invoked;
        }
        ReleaseSRWLockShared(&g_dressingRoomObjectsLock);
        if (invoked > 0)
        {
            DressingRoomLog("DressingRoomData cache reused objects=%d invoked=%d",
                cachedCount, invoked);
            return invoked;
        }
        AcquireSRWLockExclusive(&g_dressingRoomObjectsLock);
        g_dressingRoomObjectCount = 0;
        ReleaseSRWLockExclusive(&g_dressingRoomObjectsLock);

        int matches = 0;
        int valid = 0;
        int skippedRegions = 0;
        SYSTEM_INFO systemInfo = {};
        GetSystemInfo(&systemInfo);
        auto cursor = static_cast<unsigned char*>(
            systemInfo.lpMinimumApplicationAddress);
        const auto maximum = static_cast<unsigned char*>(
            systemInfo.lpMaximumApplicationAddress);
        while (cursor < maximum)
        {
            MEMORY_BASIC_INFORMATION memory = {};
            if (VirtualQuery(cursor, &memory, sizeof(memory)) != sizeof(memory))
                break;
            auto next = static_cast<unsigned char*>(memory.BaseAddress) +
                memory.RegionSize;
            if (memory.State == MEM_COMMIT &&
                (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0 &&
                (memory.Protect & (PAGE_READWRITE | PAGE_WRITECOPY |
                    PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0)
            {
                auto slots = static_cast<void**>(memory.BaseAddress);
                const std::size_t count = memory.RegionSize / sizeof(void*);
                __try
                {
                    for (std::size_t index = 0; index < count; ++index)
                    {
                        if (slots[index] != expectedVtable)
                            continue;
                        ++matches;
                        void* object = slots + index;
                        if (!IsQtObject(qt, object, expectedVtable,
                            "ViewModel::DressingRoomData"))
                            continue;
                    ++valid;
                    AcquireSRWLockExclusive(&g_dressingRoomObjectsLock);
                    if (g_dressingRoomObjectCount <
                        static_cast<LONG>(_countof(g_dressingRoomObjects)))
                        g_dressingRoomObjects[g_dressingRoomObjectCount++] = object;
                    ReleaseSRWLockExclusive(&g_dressingRoomObjectsLock);
                    if (qt.invoke(object, "updateUrls", empty, empty,
                            empty, empty, empty, empty, empty, empty, empty,
                            empty))
                            ++invoked;
                    }
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    ++skippedRegions;
                }
            }
            if (next <= cursor)
                break;
            cursor = next;
        }
        DressingRoomLog("DressingRoomData scan rva=0x%llx matches=%d valid=%d invoked=%d skippedRegions=%d",
            static_cast<unsigned long long>(vtableRva), matches, valid,
            invoked, skippedRegions);
        return invoked;
    }

    GlActiveTexture ResolveGlActiveTexture()
    {
        if (g_wglGetProcAddress == nullptr)
            return nullptr;
        PROC address = g_wglGetProcAddress("glActiveTexture");
        const std::uintptr_t value = reinterpret_cast<std::uintptr_t>(address);
        if (value <= 3 || value == static_cast<std::uintptr_t>(-1))
            return nullptr;
        return reinterpret_cast<GlActiveTexture>(address);
    }

    bool TryReadFullScreenShaderTextureName(
        const FullScreenShaderTexturePacket& packet, std::string& name)
    {
        if (packet.nameLength == 0 ||
            packet.nameLength > FullScreenShaderTextureNameCapacity)
            return false;
        for (std::uint32_t index = 0; index < packet.nameLength; ++index)
        {
            const unsigned char character =
                static_cast<unsigned char>(packet.name[index]);
            const bool letter = (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z');
            const bool digit = character >= '0' && character <= '9';
            if ((index == 0 && !letter) ||
                (index > 0 && !letter && !digit && character != '_'))
                return false;
        }
        name.assign(packet.name, packet.nameLength);
        return true;
    }

    bool ApplyFullScreenShaderTexture(void* program)
    {
        constexpr unsigned int GlTexture2D = 0x0DE1;
        constexpr unsigned int GlActiveTextureName = 0x84E0;
        constexpr unsigned int GlTexture0 = 0x84C0;
        constexpr unsigned int GlMaxTextureImageUnits = 0x8872;
        constexpr unsigned int GlMaxCombinedTextureImageUnits = 0x8B4D;
        constexpr unsigned int GlUnpackAlignment = 0x0CF5;
        constexpr unsigned int GlRgba = 0x1908;
        constexpr unsigned int GlUnsignedByte = 0x1401;
        constexpr unsigned int GlTextureMinFilter = 0x2801;
        constexpr unsigned int GlTextureMagFilter = 0x2800;
        constexpr unsigned int GlTextureWrapS = 0x2802;
        constexpr unsigned int GlTextureWrapT = 0x2803;
        constexpr int GlLinear = 0x2601;
        constexpr int GlClampToEdge = 0x812F;

        if (InterlockedCompareExchange(
                &g_fullScreenShaderTextureSet, 0, 0) == 0 ||
            g_qOpenGLShaderUniformLocation == nullptr ||
            g_qOpenGLShaderSetUniformInt == nullptr ||
            g_qOpenGLShaderSetUniform2 == nullptr ||
            g_qOpenGLShaderSetUniform1 == nullptr)
            return false;
        if (g_wglGetCurrentContext == nullptr ||
            g_glGenTextures == nullptr || g_glBindTexture == nullptr ||
            g_glIsTexture == nullptr || g_glGetIntegerv == nullptr ||
            g_glPixelStorei == nullptr || g_glTexParameteri == nullptr ||
            g_glTexImage2D == nullptr || g_glTexSubImage2D == nullptr)
            return false;

        HGLRC context = g_wglGetCurrentContext();
        GlActiveTexture glActiveTexture = ResolveGlActiveTexture();
        if (context == nullptr || glActiveTexture == nullptr)
            return false;

        try
        {
            std::vector<FullScreenShaderTextureState> snapshots;
            AcquireSRWLockShared(&g_fullScreenShaderTextureLock);
            try
            {
                snapshots = g_fullScreenShaderTextures;
            }
            catch (...)
            {
                ReleaseSRWLockShared(&g_fullScreenShaderTextureLock);
                throw;
            }
            ReleaseSRWLockShared(&g_fullScreenShaderTextureLock);

            std::lock_guard<std::mutex> guard(
                g_fullScreenShaderGlTextureLock);
            int previousActiveTexture = static_cast<int>(GlTexture0);
            g_glGetIntegerv(GlActiveTextureName, &previousActiveTexture);
            bool applied = false;
            for (const auto& snapshot : snapshots)
            {
                if (snapshot.width == 0 || snapshot.height == 0)
                    continue;
                const std::string samplerName =
                    "u_QuickPlayerTexture_" + snapshot.name;
                const std::string sizeName =
                    "u_QuickPlayerTextureSize_" + snapshot.name;
                const std::string sequenceName =
                    "u_QuickPlayerTextureSequence_" + snapshot.name;
                const int samplerLocation =
                    g_qOpenGLShaderUniformLocation(
                        program, samplerName.c_str());
                if (samplerLocation < 0)
                    continue;

                auto found = std::find_if(
                    g_fullScreenShaderGlTextures.begin(),
                    g_fullScreenShaderGlTextures.end(),
                    [context, &snapshot](
                        const FullScreenShaderGlTexture& texture)
                    {
                        return texture.context == context &&
                            texture.name == snapshot.name;
                    });
                if (found == g_fullScreenShaderGlTextures.end())
                {
                    if (g_fullScreenShaderGlTextures.size() >=
                        MaximumFullScreenShaderTextures * 16)
                        continue;
                    g_fullScreenShaderGlTextures.push_back({});
                    found = g_fullScreenShaderGlTextures.end() - 1;
                    found->context = context;
                    found->name = snapshot.name;
                }

                if (found->textureUnit < 0)
                {
                    int unitCount = 0;
                    g_glGetIntegerv(
                        GlMaxCombinedTextureImageUnits, &unitCount);
                    if (unitCount <= 0)
                        g_glGetIntegerv(
                            GlMaxTextureImageUnits, &unitCount);
                    for (int unit = unitCount - 1; unit > 0; --unit)
                    {
                        const bool used = std::any_of(
                            g_fullScreenShaderGlTextures.begin(),
                            g_fullScreenShaderGlTextures.end(),
                            [context, unit](
                                const FullScreenShaderGlTexture& texture)
                            {
                                return texture.context == context &&
                                    texture.textureUnit == unit;
                            });
                        if (!used)
                        {
                            found->textureUnit = unit;
                            break;
                        }
                    }
                    if (found->textureUnit < 0)
                        continue;
                }

                glActiveTexture(GlTexture0 + found->textureUnit);
                if (found->texture != 0 &&
                    !g_glIsTexture(found->texture))
                {
                    found->texture = 0;
                    found->generation = 0;
                    found->width = 0;
                    found->height = 0;
                }
                if (found->texture == 0)
                {
                    g_glGenTextures(1, &found->texture);
                    if (found->texture == 0)
                        continue;
                }

                g_glBindTexture(GlTexture2D, found->texture);
                if (found->generation != snapshot.generation)
                {
                    const std::size_t expected =
                        static_cast<std::size_t>(snapshot.width) *
                        snapshot.height * 4;
                    if (snapshot.rgba == nullptr ||
                        snapshot.rgba->size() != expected)
                        continue;

                    int previousUnpackAlignment = 4;
                    g_glGetIntegerv(GlUnpackAlignment,
                        &previousUnpackAlignment);
                    g_glPixelStorei(GlUnpackAlignment, 1);
                    if (found->width != snapshot.width ||
                        found->height != snapshot.height)
                    {
                        g_glTexImage2D(GlTexture2D, 0, GlRgba,
                            static_cast<int>(snapshot.width),
                            static_cast<int>(snapshot.height), 0, GlRgba,
                            GlUnsignedByte, snapshot.rgba->data());
                    }
                    else
                    {
                        g_glTexSubImage2D(GlTexture2D, 0, 0, 0,
                            static_cast<int>(snapshot.width),
                            static_cast<int>(snapshot.height), GlRgba,
                            GlUnsignedByte, snapshot.rgba->data());
                    }
                    g_glPixelStorei(GlUnpackAlignment,
                        previousUnpackAlignment);
                    g_glTexParameteri(GlTexture2D,
                        GlTextureMinFilter, GlLinear);
                    g_glTexParameteri(GlTexture2D,
                        GlTextureMagFilter, GlLinear);
                    g_glTexParameteri(GlTexture2D,
                        GlTextureWrapS, GlClampToEdge);
                    g_glTexParameteri(GlTexture2D,
                        GlTextureWrapT, GlClampToEdge);
                    found->width = snapshot.width;
                    found->height = snapshot.height;
                    found->generation = snapshot.generation;
                }

                g_qOpenGLShaderSetUniformInt(program,
                    samplerLocation, found->textureUnit);
                const int sizeLocation =
                    g_qOpenGLShaderUniformLocation(
                        program, sizeName.c_str());
                if (sizeLocation >= 0)
                    g_qOpenGLShaderSetUniform2(program, sizeLocation,
                        static_cast<float>(snapshot.width),
                        static_cast<float>(snapshot.height));
                const int sequenceLocation =
                    g_qOpenGLShaderUniformLocation(
                        program, sequenceName.c_str());
                if (sequenceLocation >= 0)
                    g_qOpenGLShaderSetUniform1(program, sequenceLocation,
                        static_cast<float>(snapshot.sequence));
                applied = true;
            }
            glActiveTexture(previousActiveTexture);
            return applied;
        }
        catch (...)
        {
            return false;
        }
    }

    HRESULT SetFullScreenShaderTexture(SIZE_T packetAddress)
    {
        try
        {
            auto packet = reinterpret_cast<FullScreenShaderTexturePacket*>(
                packetAddress);
            std::string textureName;
            if (!IsWritable(packet, sizeof(*packet)) ||
                packet->magic != FullScreenShaderTextureMagic ||
                !TryReadFullScreenShaderTextureName(
                    *packet, textureName) ||
                packet->width == 0 ||
                packet->width > MaximumFullScreenShaderTextureDimension ||
                packet->height == 0 ||
                packet->height > MaximumFullScreenShaderTextureDimension)
                return E_INVALIDARG;
            const std::size_t byteLength =
                static_cast<std::size_t>(packet->width) *
                packet->height * 4;
            if (byteLength > MaximumFullScreenShaderTextureBytes ||
                packet->byteLength != byteLength ||
                !IsWritable(packet, sizeof(*packet) + byteLength))
                return E_INVALIDARG;
            if (InterlockedCompareExchangePointer(
                    &g_originalQOpenGLShaderProgramBind,
                    nullptr, nullptr) == nullptr)
                return HRESULT_FROM_WIN32(ERROR_NOT_READY);

            const auto pixels = reinterpret_cast<const unsigned char*>(
                packet + 1);
            std::shared_ptr<std::vector<unsigned char>> writableRgba;
            {
                std::lock_guard<std::mutex> poolGuard(
                    g_fullScreenShaderPixelPoolLock);
                const auto available = std::find_if(
                    g_fullScreenShaderPixelPool.begin(),
                    g_fullScreenShaderPixelPool.end(),
                    [](const auto& candidate)
                    {
                        return candidate.use_count() == 1;
                    });
                if (available != g_fullScreenShaderPixelPool.end())
                {
                    writableRgba = *available;
                }
                else
                {
                    writableRgba = std::make_shared<
                        std::vector<unsigned char>>();
                    if (g_fullScreenShaderPixelPool.size() < 4)
                    {
                        g_fullScreenShaderPixelPool.push_back(writableRgba);
                    }
                }
                writableRgba->assign(pixels, pixels + byteLength);
            }
            std::shared_ptr<const std::vector<unsigned char>> rgba =
                writableRgba;
            AcquireSRWLockExclusive(&g_fullScreenShaderTextureLock);
            std::uint32_t sequence = 0;
            try
            {
                auto found = std::find_if(
                    g_fullScreenShaderTextures.begin(),
                    g_fullScreenShaderTextures.end(),
                    [&textureName](
                        const FullScreenShaderTextureState& texture)
                    {
                        return texture.name == textureName;
                    });
                if (found == g_fullScreenShaderTextures.end())
                {
                    if (g_fullScreenShaderTextures.size() >=
                        MaximumFullScreenShaderTextures)
                    {
                        ReleaseSRWLockExclusive(
                            &g_fullScreenShaderTextureLock);
                        return HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA);
                    }
                    g_fullScreenShaderTextures.push_back({});
                    found = g_fullScreenShaderTextures.end() - 1;
                    found->name = textureName;
                }
                sequence = found->sequence >= 0x00FFFFFF
                    ? 0 : found->sequence + 1;
                std::uint64_t generation = found->generation + 1;
                if (generation == 0)
                    generation = 1;
                found->width = packet->width;
                found->height = packet->height;
                found->sequence = sequence;
                found->generation = generation;
                found->rgba = std::move(rgba);
            }
            catch (...)
            {
                ReleaseSRWLockExclusive(
                    &g_fullScreenShaderTextureLock);
                throw;
            }
            ReleaseSRWLockExclusive(&g_fullScreenShaderTextureLock);
            packet->sequence = sequence;
            InterlockedExchange(&g_fullScreenShaderTextureSet, 1);
            return BridgeSuccess;
        }
        catch (const std::bad_alloc&)
        {
            return E_OUTOFMEMORY;
        }
        catch (...)
        {
            return E_UNEXPECTED;
        }
    }

    HRESULT GetFullScreenShaderTexture(SIZE_T packetAddress)
    {
        try
        {
            auto packet = reinterpret_cast<FullScreenShaderTexturePacket*>(
                packetAddress);
            std::string textureName;
            if (!IsWritable(packet, sizeof(*packet)) ||
                packet->magic != FullScreenShaderTextureMagic ||
                !TryReadFullScreenShaderTextureName(*packet, textureName))
                return E_INVALIDARG;
            FullScreenShaderTexturePacket current;
            current.nameLength = packet->nameLength;
            std::memcpy(current.name, packet->name,
                FullScreenShaderTextureNameCapacity);
            AcquireSRWLockShared(&g_fullScreenShaderTextureLock);
            const auto found = std::find_if(
                g_fullScreenShaderTextures.begin(),
                g_fullScreenShaderTextures.end(),
                [&textureName](
                    const FullScreenShaderTextureState& texture)
                {
                    return texture.name == textureName;
                });
            if (found != g_fullScreenShaderTextures.end())
            {
                current.width = found->width;
                current.height = found->height;
                current.sequence = found->sequence;
                current.byteLength = found->rgba == nullptr
                    ? 0
                    : static_cast<std::uint32_t>(found->rgba->size());
            }
            ReleaseSRWLockShared(&g_fullScreenShaderTextureLock);
            *packet = current;
            return BridgeSuccess;
        }
        catch (const std::bad_alloc&)
        {
            return E_OUTOFMEMORY;
        }
        catch (...)
        {
            return E_UNEXPECTED;
        }
    }

    bool __fastcall CapturingQOpenGLShaderProgramBind(void* program)
    {
        const auto original = reinterpret_cast<QOpenGLShaderProgramBind>(
            InterlockedCompareExchangePointer(
                &g_originalQOpenGLShaderProgramBind, nullptr, nullptr));
        const bool bound = original != nullptr && original(program);
        if (!bound)
            return bound;
        if (InterlockedCompareExchange(
                &g_fullScreenShaderDataSet, 0, 0) != 0)
        {
            FullScreenShaderDataPacket packet;
            AcquireSRWLockShared(&g_fullScreenShaderDataLock);
            packet = g_fullScreenShaderData;
            ReleaseSRWLockShared(&g_fullScreenShaderDataLock);

            const int dataLocation = g_qOpenGLShaderUniformLocation == nullptr
                ? -1 : g_qOpenGLShaderUniformLocation(
                    program, "u_QuickPlayerData");
            if (dataLocation >= 0 && g_qOpenGLShaderSetUniform4 != nullptr)
            {
                g_qOpenGLShaderSetUniform4(program, dataLocation,
                    packet.values[0], packet.values[1],
                    packet.values[2], packet.values[3]);
            }
            const int sequenceLocation =
                g_qOpenGLShaderUniformLocation == nullptr
                ? -1 : g_qOpenGLShaderUniformLocation(
                    program, "u_QuickPlayerSequence");
            if (sequenceLocation >= 0 &&
                g_qOpenGLShaderSetUniform1 != nullptr)
            {
                g_qOpenGLShaderSetUniform1(program, sequenceLocation,
                    static_cast<float>(packet.sequence));
            }
        }
        ApplyFullScreenShaderTexture(program);
        return bound;
    }

    void AppendJsonString(std::string& json, const std::string& value)
    {
        json.push_back('"');
        for (const char character : value)
        {
            if (character == '"' || character == '\\')
                json.push_back('\\');
            json.push_back(character);
        }
        json.push_back('"');
    }

    void PublishFullScreenState(bool discoverScene)
    {
        bool missingScene = false;
        AcquireSRWLockShared(&g_fullScreenStateLock);
        missingScene = std::none_of(g_fullScreenSlots.begin(),
            g_fullScreenSlots.end(), [](const FullScreenSlot& slot)
            {
                return slot.active;
            });
        ReleaseSRWLockShared(&g_fullScreenStateLock);
        if (discoverScene && missingScene && IsFullScreenModeActive())
            SynchronizeCurrentFullScreenScene();
        RefreshFullScreenQueue();
        std::string json = "{\"clips\":[";
        AcquireSRWLockShared(&g_fullScreenStateLock);
        for (std::size_t index = 0; index < g_fullScreenClips.size(); ++index)
        {
            if (index != 0) json.push_back(',');
            AppendJsonString(json, g_fullScreenClips[index]);
        }
        json += "],\"queue\":[";
        for (std::size_t index = 0; index < g_fullScreenQueue.size(); ++index)
        {
            if (index != 0) json.push_back(',');
            json += "{\"card\":";
            AppendJsonString(json, g_fullScreenQueue[index].card);
            json += ",\"clip\":";
            AppendJsonString(json, g_fullScreenQueue[index].clip);
            json.push_back('}');
        }
        json += "],\"slots\":[";
        bool firstSlot = true;
        for (std::size_t index = 0; index < g_fullScreenSlots.size(); ++index)
        {
            if (!g_fullScreenSlots[index].active ||
                (g_fullScreenSceneParent != nullptr &&
                    g_fullScreenSlots[index].parent !=
                        g_fullScreenSceneParent))
                continue;
            if (!firstSlot) json.push_back(',');
            firstSlot = false;
            json += "{\"id\":" + std::to_string(
                g_fullScreenSlots[index].id) + ",\"source\":";
            AppendJsonString(json, g_fullScreenSlots[index].source);
            json += ",\"card\":";
            AppendJsonString(json, g_fullScreenSlots[index].card);
            json += ",\"clip\":";
            AppendJsonString(json, g_fullScreenSlots[index].clip);
            json.push_back('}');
        }
        ReleaseSRWLockShared(&g_fullScreenStateLock);
        json += "]}";
        SendBridgeEvent(L"FullscreenState", json.data(),
            static_cast<DWORD>(json.size()));
    }

    std::string CardTag(const std::string& clip)
    {
        const std::size_t start = clip.find_last_of("\\/") + 1;
        const std::size_t separator = clip.find('_', start);
        return clip.substr(start, separator - start);
    }

    void __cdecl CapturingQMetaActivate(void* sender,
        const void* metaObject, int signalIndex, void** arguments)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto className = core == nullptr ? nullptr :
            reinterpret_cast<QMetaClassName>(GetProcAddress(core,
                "?className@QMetaObject@@QEBAPEBDXZ"));
        bool changed = false;
        const char* senderClass = className == nullptr ||
            metaObject == nullptr ? nullptr : className(metaObject);
        if (senderClass != nullptr &&
            std::strcmp(senderClass, "FsClipNode") == 0)
        {
            if (IsSignal(metaObject, signalIndex, "clipOpened()"))
                changed = SynchronizeFullScreenScene(
                    FullScreenScene(sender));
            else if (IsSignal(metaObject, signalIndex, "clipClosed()"))
            {
                AcquireSRWLockExclusive(&g_fullScreenStateLock);
                const auto slot = std::find_if(g_fullScreenSlots.begin(),
                    g_fullScreenSlots.end(), [&](const FullScreenSlot& value)
                    {
                        return value.node == sender;
                    });
                if (slot != g_fullScreenSlots.end())
                {
                    slot->card.clear();
                    slot->clip.clear();
                    changed = true;
                }
                g_fullScreenClips.clear();
                for (const FullScreenSlot& value : g_fullScreenSlots)
                    if (value.active && !value.clip.empty())
                        g_fullScreenClips.push_back(value.clip);
                ReleaseSRWLockExclusive(&g_fullScreenStateLock);
            }
        }
        else if (senderClass != nullptr &&
            std::strcmp(senderClass, "FullScreen") == 0)
        {
            if (IsSignal(metaObject, signalIndex, "stopped()"))
            {
                void* pendingCard = nullptr;
                AcquireSRWLockExclusive(&g_fullScreenStateLock);
                changed = !g_fullScreenClips.empty() ||
                    !g_fullScreenSlots.empty();
                g_fullScreenClips.clear();
                g_fullScreenSlots.clear();
                pendingCard = g_fullScreenReplacement.playableCard;
                g_fullScreenReplacement = {};
                g_fullScreenSceneParent = nullptr;
                g_nextFullScreenSlotId = 1;
                ReleaseSRWLockExclusive(&g_fullScreenStateLock);
                DeletePlayableCardLater(pendingCard);
            }
            else if (IsSignal(metaObject, signalIndex,
                         "currentSceneChanged(Scene*)") &&
                arguments != nullptr && arguments[1] != nullptr)
            {
                changed = SynchronizeFullScreenScene(
                    *reinterpret_cast<void**>(arguments[1]));
            }
        }
        else if (senderClass != nullptr &&
            std::strcmp(senderClass, "Model::CardSequencer") == 0)
        {
            InterlockedExchangePointer(&g_cardSequencerObject, sender);
            if (IsSignal(metaObject, signalIndex, "frequencyUpdated()"))
                changed = CompleteFullScreenQueueOperation(sender);
            changed = IsSignal(metaObject, signalIndex,
                    "nextInserted(int,Model::NextCard)") ||
                IsSignal(metaObject, signalIndex, "nextRemoved(int)") ||
                IsSignal(metaObject, signalIndex, "nextRemoved()") || changed;
        }
        const auto original = reinterpret_cast<QMetaActivate>(
            InterlockedCompareExchangePointer(
                &g_originalQMetaActivate, nullptr, nullptr));
        if (original != nullptr)
            original(sender, metaObject, signalIndex, arguments);
        if (changed)
            PublishFullScreenState();
    }

    HRESULT InstallFullScreenHook()
    {
        if (CardTag("models:f0971\\f0971_2052402.vghd") != "f0971")
            return E_UNEXPECTED;
        AcquireSRWLockExclusive(&g_fullScreenHookLock);
        if (InterlockedCompareExchangePointer(
                &g_originalQMetaActivate, nullptr, nullptr) != nullptr)
        {
            ReleaseSRWLockExclusive(&g_fullScreenHookLock);
            return BridgeSuccess;
        }
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const HMODULE gui = GetModuleHandleW(L"Qt5Gui.dll");
        const HMODULE openGl = GetModuleHandleW(L"opengl32.dll");
        void* target = core == nullptr ? nullptr : GetProcAddress(core,
            "?activate@QMetaObject@@SAXPEAVQObject@@PEBU1@HPEAPEAX@Z");
        void* shaderBind = gui == nullptr ? nullptr : GetProcAddress(gui,
            "?bind@QOpenGLShaderProgram@@QEAA_NXZ");
        g_qOpenGLShaderUniformLocation = gui == nullptr ? nullptr :
            reinterpret_cast<QOpenGLShaderUniformLocation>(GetProcAddress(gui,
                "?uniformLocation@QOpenGLShaderProgram@@QEBAHPEBD@Z"));
        g_qOpenGLShaderSetUniform1 = gui == nullptr ? nullptr :
            reinterpret_cast<QOpenGLShaderSetUniform1>(GetProcAddress(gui,
                "?setUniformValue@QOpenGLShaderProgram@@QEAAXHM@Z"));
        g_qOpenGLShaderSetUniformInt = gui == nullptr ? nullptr :
            reinterpret_cast<QOpenGLShaderSetUniformInt>(GetProcAddress(gui,
                "?setUniformValue@QOpenGLShaderProgram@@QEAAXHH@Z"));
        g_qOpenGLShaderSetUniform2 = gui == nullptr ? nullptr :
            reinterpret_cast<QOpenGLShaderSetUniform2>(GetProcAddress(gui,
                "?setUniformValue@QOpenGLShaderProgram@@QEAAXHMM@Z"));
        g_qOpenGLShaderSetUniform4 = gui == nullptr ? nullptr :
            reinterpret_cast<QOpenGLShaderSetUniform4>(GetProcAddress(gui,
                "?setUniformValue@QOpenGLShaderProgram@@QEAAXHMMMM@Z"));
        g_wglGetCurrentContext = openGl == nullptr ? nullptr :
            reinterpret_cast<WglGetCurrentContext>(GetProcAddress(openGl,
                "wglGetCurrentContext"));
        g_wglGetProcAddress = openGl == nullptr ? nullptr :
            reinterpret_cast<WglGetProcAddress>(GetProcAddress(openGl,
                "wglGetProcAddress"));
        g_glGenTextures = openGl == nullptr ? nullptr :
            reinterpret_cast<GlGenTextures>(GetProcAddress(openGl,
                "glGenTextures"));
        g_glBindTexture = openGl == nullptr ? nullptr :
            reinterpret_cast<GlBindTexture>(GetProcAddress(openGl,
                "glBindTexture"));
        g_glIsTexture = openGl == nullptr ? nullptr :
            reinterpret_cast<GlIsTexture>(GetProcAddress(openGl,
                "glIsTexture"));
        g_glGetIntegerv = openGl == nullptr ? nullptr :
            reinterpret_cast<GlGetIntegerv>(GetProcAddress(openGl,
                "glGetIntegerv"));
        g_glPixelStorei = openGl == nullptr ? nullptr :
            reinterpret_cast<GlPixelStorei>(GetProcAddress(openGl,
                "glPixelStorei"));
        g_glTexParameteri = openGl == nullptr ? nullptr :
            reinterpret_cast<GlTexParameteri>(GetProcAddress(openGl,
                "glTexParameteri"));
        g_glTexImage2D = openGl == nullptr ? nullptr :
            reinterpret_cast<GlTexImage2D>(GetProcAddress(openGl,
                "glTexImage2D"));
        g_glTexSubImage2D = openGl == nullptr ? nullptr :
            reinterpret_cast<GlTexSubImage2D>(GetProcAddress(openGl,
                "glTexSubImage2D"));
        void* startNextShow = FindUniqueFunction(
            FsClipNodeStartNextShowSignature,
            sizeof(FsClipNodeStartNextShowSignature), -1);
        const bool layoutResolved = startNextShow != nullptr &&
            ResolveFullScreenLayout(
                reinterpret_cast<unsigned char*>(startNextShow));
        auto base = ImageBase();
        void* nextShowClip = base == nullptr ||
            FsClipNodeNextShowClipRva == 0 ? nullptr :
            base + FsClipNodeNextShowClipRva;
        wchar_t profilePath[MAX_PATH] = {};
        if (OffsetProfilePath(profilePath))
            SaveResolvedOffsets(profilePath);
        if (target == nullptr || shaderBind == nullptr ||
            g_qOpenGLShaderUniformLocation == nullptr ||
            g_qOpenGLShaderSetUniform1 == nullptr ||
            g_qOpenGLShaderSetUniformInt == nullptr ||
            g_qOpenGLShaderSetUniform2 == nullptr ||
            g_qOpenGLShaderSetUniform4 == nullptr ||
            g_wglGetCurrentContext == nullptr ||
            g_wglGetProcAddress == nullptr ||
            g_glGenTextures == nullptr ||
            g_glBindTexture == nullptr ||
            g_glIsTexture == nullptr ||
            g_glGetIntegerv == nullptr ||
            g_glPixelStorei == nullptr ||
            g_glTexParameteri == nullptr ||
            g_glTexImage2D == nullptr ||
            g_glTexSubImage2D == nullptr ||
            startNextShow == nullptr ||
            nextShowClip == nullptr ||
            !layoutResolved ||
            PinBridge() < 0)
        {
            ReleaseSRWLockExclusive(&g_fullScreenHookLock);
            return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        }
        MH_STATUS status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED)
        {
            ReleaseSRWLockExclusive(&g_fullScreenHookLock);
            return E_FAIL;
        }
        void* original = nullptr;
        status = MH_CreateHook(target,
            reinterpret_cast<void*>(&CapturingQMetaActivate), &original);
        if (status == MH_OK)
            InterlockedExchangePointer(&g_originalQMetaActivate, original);
        if (status == MH_OK)
            status = MH_EnableHook(target);
        if (status == MH_OK || status == MH_ERROR_ENABLED)
        {
            void* startNextShowOriginal = nullptr;
            status = MH_CreateHook(startNextShow,
                reinterpret_cast<void*>(
                    &CapturingFsClipNodeStartNextShow),
                &startNextShowOriginal);
            if (status == MH_OK)
                InterlockedExchangePointer(
                    &g_originalFsClipNodeStartNextShow,
                    startNextShowOriginal);
            if (status == MH_OK)
                status = MH_EnableHook(startNextShow);
        }
        if (status == MH_OK || status == MH_ERROR_ENABLED)
        {
            void* nextShowClipOriginal = nullptr;
            status = MH_CreateHook(nextShowClip,
                reinterpret_cast<void*>(
                    &CapturingFsClipNodeNextShowClip),
                &nextShowClipOriginal);
            if (status == MH_OK)
                InterlockedExchangePointer(
                    &g_originalFsClipNodeNextShowClip,
                    nextShowClipOriginal);
            if (status == MH_OK)
                status = MH_EnableHook(nextShowClip);
        }
        if (status == MH_OK || status == MH_ERROR_ENABLED)
        {
            void* shaderBindOriginal = nullptr;
            status = MH_CreateHook(shaderBind,
                reinterpret_cast<void*>(
                    &CapturingQOpenGLShaderProgramBind),
                &shaderBindOriginal);
            if (status == MH_OK)
                InterlockedExchangePointer(
                    &g_originalQOpenGLShaderProgramBind,
                    shaderBindOriginal);
            if (status == MH_OK)
                status = MH_EnableHook(shaderBind);
        }
        const HRESULT result = status == MH_OK || status == MH_ERROR_ENABLED
            ? BridgeSuccess : E_FAIL;
        if (result < 0)
        {
            MH_DisableHook(shaderBind);
            MH_DisableHook(nextShowClip);
            MH_DisableHook(startNextShow);
            MH_DisableHook(target);
            InterlockedExchangePointer(&g_originalQMetaActivate, nullptr);
            InterlockedExchangePointer(
                &g_originalFsClipNodeStartNextShow, nullptr);
            InterlockedExchangePointer(
                &g_originalFsClipNodeNextShowClip, nullptr);
            InterlockedExchangePointer(
                &g_originalQOpenGLShaderProgramBind, nullptr);
            g_qOpenGLShaderUniformLocation = nullptr;
            g_qOpenGLShaderSetUniform1 = nullptr;
            g_qOpenGLShaderSetUniformInt = nullptr;
            g_qOpenGLShaderSetUniform2 = nullptr;
            g_qOpenGLShaderSetUniform4 = nullptr;
            g_wglGetCurrentContext = nullptr;
            g_wglGetProcAddress = nullptr;
            g_glGenTextures = nullptr;
            g_glBindTexture = nullptr;
            g_glIsTexture = nullptr;
            g_glGetIntegerv = nullptr;
            g_glPixelStorei = nullptr;
            g_glTexParameteri = nullptr;
            g_glTexImage2D = nullptr;
            g_glTexSubImage2D = nullptr;
        }
        ReleaseSRWLockExclusive(&g_fullScreenHookLock);
        return result;
    }

    HRESULT InvokeQtSingleton(std::uintptr_t& vtableRva,
        PVOID volatile* cachedObject, const char* decoratedClassName,
        const char* className, const char* method)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        if (core == nullptr)
            return E_NOINTERFACE;
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(GetProcAddress(core,
                "?inherits@QObject@@QEBA_NPEBD@Z")),
            reinterpret_cast<QMetaInvoke>(GetProcAddress(core,
                "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
                "VQGenericArgument@@222222222@Z"))
        };
        if (qt.inherits == nullptr || qt.invoke == nullptr)
            return E_NOINTERFACE;
        void* object = FindQtObject(qt, vtableRva, cachedObject,
            decoratedClassName, className);
        if (object == nullptr)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        const QtGenericArgument empty = {};
        return qt.invoke(object, method, empty, empty, empty, empty, empty,
            empty, empty, empty, empty, empty) ? BridgeSuccess : E_FAIL;
    }

    HRESULT InvokeDesktopNext()
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        using QObjectChildren = const void*(__fastcall*)(const void*);
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(core == nullptr ? nullptr :
                GetProcAddress(core, "?inherits@QObject@@QEBA_NPEBD@Z")),
            reinterpret_cast<QMetaInvoke>(core == nullptr ? nullptr :
                GetProcAddress(core,
                    "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
                    "VQGenericArgument@@222222222@Z"))
        };
        const auto children = reinterpret_cast<QObjectChildren>(
            core == nullptr ? nullptr : GetProcAddress(core,
                "?children@QObject@@QEBAAEBV?$QList@PEAVQObject@@@@XZ"));
        if (qt.inherits == nullptr || qt.invoke == nullptr ||
            children == nullptr)
            return E_NOINTERFACE;

        void* live = FindLiveObject(qt);
        if (live == nullptr)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);

        std::vector<void*> objects = { live };
        const QtGenericArgument empty = {};
        for (std::size_t index = 0;
            index < objects.size() && objects.size() <= 4096; ++index)
        {
            void* object = objects[index];
            if (object != live && qt.inherits(object, "LiveActor"))
            {
                return qt.invoke(object, "doNext", empty, empty, empty,
                    empty, empty, empty, empty, empty, empty, empty)
                    ? BridgeSuccess : E_FAIL;
            }

            std::vector<void*> childObjects;
            if (ReadQtPointerList(
                    const_cast<void*>(children(object)), 4096, childObjects))
                objects.insert(objects.end(), childObjects.begin(),
                    childObjects.end());
        }
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }

    HRESULT DumpFullScreenObjectTree()
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        if (core == nullptr)
            return E_NOINTERFACE;
        using QObjectChildren = const void*(__fastcall*)(const void*);
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(GetProcAddress(core,
                "?inherits@QObject@@QEBA_NPEBD@Z")), nullptr
        };
        const auto children = reinterpret_cast<QObjectChildren>(
            GetProcAddress(core,
                "?children@QObject@@QEBAAEBV?$QList@PEAVQObject@@@@XZ"));
        if (qt.inherits == nullptr || children == nullptr)
            return E_NOINTERFACE;

        std::vector<FullScreenSlot> trackedSlots;
        void* sceneParent = nullptr;
        AcquireSRWLockShared(&g_fullScreenStateLock);
        sceneParent = g_fullScreenSceneParent;
        for (const FullScreenSlot& slot : g_fullScreenSlots)
            if (slot.active && slot.parent == sceneParent)
                trackedSlots.push_back(slot);
        ReleaseSRWLockShared(&g_fullScreenStateLock);
        if (sceneParent == nullptr ||
            !IsReadable(sceneParent, sizeof(void*)))
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);

        std::vector<void*> objects = { sceneParent };
        const void* list = children(sceneParent);
        if (!IsReadable(list, sizeof(void*)))
            return E_UNEXPECTED;
        const auto data = *reinterpret_cast<QtListData* const*>(list);
        if (!IsReadable(data, sizeof(QtListData)) || data->begin < 0 ||
            data->end < data->begin || data->alloc < data->end ||
            data->end > 4096 || !IsReadable(data,
                offsetof(QtListData, values) + sizeof(void*) * data->end))
            return E_UNEXPECTED;
        for (int index = data->begin; index < data->end; ++index)
            if (data->values[index] != nullptr &&
                IsReadable(data->values[index], sizeof(void*)))
                objects.push_back(data->values[index]);

        std::string json = "[";
        const char* knownClasses[] = {
            "FsClipNode", "FsClipSpriteNode", "FsClipNameSpriteNode",
            "FsRootNode", "FsAbstractSpriteNode", "FsSpriteNode",
            "FsTextureNode", "FsFrameBufferNode", "FsCameraNode",
            "FullScreenRenderer", "FullScreen", "Scene", "QQuickWindow",
            "QWindow", "QApplication", "QGuiApplication",
            "QCoreApplication", "QObject"
        };
        for (std::size_t index = 0; index < objects.size(); ++index)
        {
            void* object = objects[index];
            const char* name = "unknown";
            for (const char* candidate : knownClasses)
                if (qt.inherits(object, candidate))
                {
                    name = candidate;
                    break;
                }
            if (index != 0) json.push_back(',');
            json += "{\"address\":" + std::to_string(
                reinterpret_cast<std::uintptr_t>(object)) +
                ",\"parent\":" + std::to_string(
                    reinterpret_cast<std::uintptr_t>(
                        index == 0 ? nullptr : sceneParent)) +
                ",\"depth\":" + std::to_string(index == 0 ? 0 : 1) +
                ",\"class\":";
            AppendJsonString(json, name);
            json += ",\"objectName\":";
            AppendJsonString(json, QtObjectName(object));
            const auto tracked = std::find_if(trackedSlots.begin(),
                trackedSlots.end(), [&](const FullScreenSlot& slot)
                {
                    return slot.node == object;
                });
            json += ",\"slotId\":" + std::to_string(
                tracked == trackedSlots.end() ? 0 : tracked->id) +
                ",\"active\":";
            json += tracked != trackedSlots.end() && tracked->active
                ? "true" : "false";
            json += ",\"clip\":";
            AppendJsonString(json, tracked == trackedSlots.end()
                ? "" : tracked->clip);
            json += ",\"card\":";
            AppendJsonString(json, tracked == trackedSlots.end()
                ? "" : tracked->card);
            json += ",\"source\":";
            AppendJsonString(json, tracked == trackedSlots.end()
                ? "" : tracked->source);
            json.push_back('}');
        }
        json.push_back(']');
        SendBridgeEvent(L"FullscreenTree", json.data(),
            static_cast<DWORD>(json.size()));
        return BridgeSuccess;
    }

    bool StopFullScreenNode(void* node)
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto invoke = core == nullptr ? nullptr :
            reinterpret_cast<QMetaInvoke>(GetProcAddress(core,
                "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
                "VQGenericArgument@@222222222@Z"));
        if (node == nullptr || invoke == nullptr)
            return false;
        const QtGenericArgument empty = {};
        return invoke(node, "stopAllCards", empty, empty, empty, empty,
            empty, empty, empty, empty, empty, empty) != false;
    }

    void* FullScreenSlotNode(DWORD slotId)
    {
        void* node = nullptr;
        AcquireSRWLockShared(&g_fullScreenStateLock);
        const auto slot = std::find_if(g_fullScreenSlots.begin(),
            g_fullScreenSlots.end(), [&](const FullScreenSlot& value)
            {
                return value.id == slotId && value.active &&
                    (g_fullScreenSceneParent == nullptr ||
                        value.parent == g_fullScreenSceneParent);
            });
        if (slot != g_fullScreenSlots.end())
            node = slot->node;
        ReleaseSRWLockShared(&g_fullScreenStateLock);
        return node;
    }

    HRESULT AdvanceFullScreenSlot(DWORD slotId)
    {
        void* node = FullScreenSlotNode(slotId);
        if (node == nullptr)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        return StopFullScreenNode(node) ? BridgeSuccess : E_FAIL;
    }

    HRESULT ReplaceFullScreenSlot(DWORD slotId, const std::string& card,
        const std::string& clip)
    {
        void* node = FullScreenSlotNode(slotId);
        if (node == nullptr)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        void* playableCard = CreatePlayableCard(card, clip);
        if (playableCard == nullptr)
            return E_FAIL;
        if (PlayableCardTag(playableCard) != card)
        {
            DestroyPlayableCard(playableCard);
            return E_UNEXPECTED;
        }
        if (!MovePlayableCardToNodeThread(playableCard, node))
        {
            DestroyPlayableCard(playableCard);
            return E_FAIL;
        }
        if (FullScreenScene(node) == nullptr)
        {
            DeletePlayableCardLater(playableCard);
            return E_FAIL;
        }
        void* replacedCard = nullptr;
        AcquireSRWLockExclusive(&g_fullScreenStateLock);
        replacedCard = g_fullScreenReplacement.playableCard;
        g_fullScreenReplacement = { node, playableCard,
            "models:" + card + "\\" + clip,
            GetTickCount64() + 15'000 };
        ReleaseSRWLockExclusive(&g_fullScreenStateLock);
        DeletePlayableCardLater(replacedCard);
        if (StopFullScreenNode(node))
            return BridgeSuccess;
        void* failedCard = nullptr;
        AcquireSRWLockExclusive(&g_fullScreenStateLock);
        if (g_fullScreenReplacement.node == node &&
            g_fullScreenReplacement.playableCard == playableCard)
        {
            failedCard = playableCard;
            g_fullScreenReplacement = {};
        }
        ReleaseSRWLockExclusive(&g_fullScreenStateLock);
        DeletePlayableCardLater(failedCard);
        return E_FAIL;
    }

    bool CopyFullScreenRequest(SIZE_T address, char* destination,
        std::size_t capacity)
    {
        if (address == 0 || destination == nullptr || capacity < 2)
            return false;
        __try
        {
            const char* source = reinterpret_cast<const char*>(address);
            for (std::size_t index = 0; index < capacity; ++index)
            {
                if (!IsReadable(source + index, 1))
                    return false;
                destination[index] = source[index];
                if (destination[index] == '\0')
                    return index > 0;
            }
            destination[capacity - 1] = '\0';
            return false;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            destination[0] = '\0';
            return false;
        }
    }

    bool IsFullScreenCardRequest(const std::string& card,
        const std::string& clip)
    {
        if (card.empty() || card.size() > 64)
            return false;
        for (char value : card)
            if (!((value >= 'a' && value <= 'z') ||
                  (value >= 'A' && value <= 'Z') ||
                  (value >= '0' && value <= '9') || value == '-' ||
                  value == '_'))
                return false;
        if (clip.empty() || clip.size() > 260 ||
            clip.find_first_of("\\/:\r\n") != std::string::npos)
            return false;
        const std::size_t extension = clip.find_last_of('.');
        return extension != std::string::npos &&
            (_stricmp(clip.c_str() + extension, ".vghd") == 0 ||
             _stricmp(clip.c_str() + extension, ".demo") == 0);
    }

    bool ParseFullScreenQueueRequest(SIZE_T address, std::string& card,
        std::string& clip)
    {
        char request[1024] = {};
        if (!CopyFullScreenRequest(address, request, sizeof(request)))
            return false;
        char* separator = std::strchr(request, '\n');
        if (separator == nullptr)
            return false;
        *separator = '\0';
        card = request;
        clip = separator + 1;
        return IsFullScreenCardRequest(card, clip);
    }

    HRESULT InsertFullScreenQueueOnThread(void* sequencer,
        const std::string& card, const std::string& clip, bool prepend)
    {
        auto base = ImageBase();
        if (base == nullptr || sequencer == nullptr ||
            CardSequencerInsertNextRva == 0)
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);

        void* playableCard = CreatePlayableCard(card, clip);
        if (playableCard == nullptr ||
            PlayableCardTag(playableCard) != card)
        {
            DestroyPlayableCard(playableCard);
            return E_UNEXPECTED;
        }
        if (!MovePlayableCardToNodeThread(playableCard, sequencer))
        {
            DestroyPlayableCard(playableCard);
            return E_FAIL;
        }

        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        void* const sharedNull = core == nullptr ? nullptr :
            reinterpret_cast<void*>(GetProcAddress(core,
                "?shared_null@QListData@@2UData@1@B"));
        const auto detach = core == nullptr ? nullptr :
            reinterpret_cast<QtListDetach>(GetProcAddress(core,
                "?detach@QListData@@QEAAPEAUData@1@H@Z"));
        const auto disposeData = core == nullptr ? nullptr :
            reinterpret_cast<QtListDisposeData>(GetProcAddress(core,
                "?dispose@QListData@@SAXPEAUData@1@@Z"));
        if (sharedNull == nullptr || detach == nullptr ||
            disposeData == nullptr)
        {
            DeletePlayableCardLater(playableCard);
            return E_NOINTERFACE;
        }

        void* cards = sharedNull;
        const auto releaseCards = [&cards, disposeData]()
        {
            auto data = reinterpret_cast<QtListData*>(cards);
            if (data != nullptr && data->ref != -1 &&
                InterlockedDecrement(&data->ref) == 0)
                disposeData(data);
        };
        detach(&cards, 1);
        auto data = reinterpret_cast<QtListData*>(cards);
        if (data == nullptr || data == sharedNull || data->alloc < 1 ||
            data->begin < 0 || data->begin != data->end ||
            data->end >= data->alloc)
        {
            releaseCards();
            DeletePlayableCardLater(playableCard);
            return E_OUTOFMEMORY;
        }
        data->values[data->end++] = playableCard;

        std::vector<void*> nextCards;
        if (!ReadQtPointerList(reinterpret_cast<unsigned char*>(sequencer) +
                CardSequencerNextOffset, 4096, nextCards))
        {
            releaseCards();
            DeletePlayableCardLater(playableCard);
            return E_UNEXPECTED;
        }
        const int index = prepend ? 0 : static_cast<int>(nextCards.size());
        reinterpret_cast<CardSequencerInsertNext>(
            base + CardSequencerInsertNextRva)(sequencer, index, &cards, 0, 0);
        releaseCards();
        return BridgeSuccess;
    }

    HRESULT RemoveFullScreenQueueOnThread(void* sequencer,
        std::size_t index)
    {
        auto base = ImageBase();
        if (base == nullptr || sequencer == nullptr ||
            CardSequencerTakeNextAtRva == 0)
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        std::vector<void*> nextCards;
        if (!ReadQtPointerList(reinterpret_cast<unsigned char*>(sequencer) +
                CardSequencerNextOffset, 4096, nextCards))
            return E_UNEXPECTED;
        if (index >= nextCards.size() || index > MAXINT)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        void* playableCard = reinterpret_cast<CardSequencerTakeNextAt>(
            base + CardSequencerTakeNextAtRva)(sequencer,
                static_cast<int>(index), false);
        if (playableCard == nullptr)
            return E_FAIL;
        DeletePlayableCardLater(playableCard);
        return BridgeSuccess;
    }

    bool CompleteFullScreenQueueOperation(void* sequencer)
    {
        AcquireSRWLockExclusive(&g_fullScreenQueueOperationLock);
        if (g_fullScreenQueueOperation == nullptr ||
            g_fullScreenQueueOperation->kind ==
                FullScreenQueueOperationKind::Executing)
        {
            ReleaseSRWLockExclusive(&g_fullScreenQueueOperationLock);
            return false;
        }

        const std::shared_ptr<FullScreenQueueOperation> operation =
            g_fullScreenQueueOperation;
        const FullScreenQueueOperationKind kind =
            operation->kind;
        const std::string card = operation->card;
        const std::string clip = operation->clip;
        const std::size_t index = operation->index;
        operation->kind = FullScreenQueueOperationKind::Executing;
        ReleaseSRWLockExclusive(&g_fullScreenQueueOperationLock);

        HRESULT result = E_UNEXPECTED;
        switch (kind)
        {
        case FullScreenQueueOperationKind::Insert:
        case FullScreenQueueOperationKind::Add:
            result = InsertFullScreenQueueOnThread(sequencer, card, clip,
                kind == FullScreenQueueOperationKind::Insert);
            break;
        case FullScreenQueueOperationKind::Remove:
            result = RemoveFullScreenQueueOnThread(sequencer, index);
            break;
        }

        AcquireSRWLockExclusive(&g_fullScreenQueueOperationLock);
        operation->result = result;
        SetEvent(operation->completed);
        ReleaseSRWLockExclusive(&g_fullScreenQueueOperationLock);
        return result >= 0;
    }

    HRESULT RunFullScreenQueueOperation(FullScreenQueueOperationKind kind,
        const std::string& card = {}, const std::string& clip = {},
        std::size_t index = 0)
    {
        const std::shared_ptr<FullScreenQueueOperation> operation =
            std::make_shared<FullScreenQueueOperation>();
        operation->kind = kind;
        operation->card = card;
        operation->clip = clip;
        operation->index = index;
        operation->completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (operation->completed == nullptr)
            return HRESULT_FROM_WIN32(GetLastError());

        AcquireSRWLockExclusive(&g_fullScreenQueueOperationLock);
        if (g_fullScreenQueueOperation != nullptr)
        {
            ReleaseSRWLockExclusive(&g_fullScreenQueueOperationLock);
            return HRESULT_FROM_WIN32(ERROR_BUSY);
        }
        g_fullScreenQueueOperation = operation;
        ReleaseSRWLockExclusive(&g_fullScreenQueueOperationLock);

        HRESULT result = InvokeQtSingleton(g_cardSequencerVtableRva,
            &g_cardSequencerObject, ".?AVCardSequencer@Model@@",
            "Model::CardSequencer", "frequencyUpdated");
        if (result >= 0 && WaitForSingleObject(operation->completed, 5'000) ==
                WAIT_OBJECT_0)
        {
            AcquireSRWLockShared(&g_fullScreenQueueOperationLock);
            result = operation->result;
            ReleaseSRWLockShared(&g_fullScreenQueueOperationLock);
        }
        else if (result >= 0)
        {
            result = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }

        AcquireSRWLockExclusive(&g_fullScreenQueueOperationLock);
        if (g_fullScreenQueueOperation == operation)
            g_fullScreenQueueOperation.reset();
        ReleaseSRWLockExclusive(&g_fullScreenQueueOperationLock);
        return result;
    }

    bool SetMovieWindowPercent(LONG mode, DWORD newPercent, bool notify)
    {
        if ((mode != 1 && mode != 2) ||
            newPercent < 1 || newPercent > 200)
        {
            return false;
        }

        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        if (core == nullptr)
        {
            return false;
        }
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(GetProcAddress(core,
                "?inherits@QObject@@QEBA_NPEBD@Z")),
            reinterpret_cast<QMetaInvoke>(GetProcAddress(core,
                "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
                "VQGenericArgument@@222222222@Z"))
        };
        if (qt.inherits == nullptr || qt.invoke == nullptr)
        {
            return false;
        }
        void* live = FindLiveObject(qt);
        if (live == nullptr)
        {
            return false;
        }

        const bool large = mode == 1;
        const int percent = static_cast<int>(newPercent);
        const QtGenericArgument argument = { &percent, "int" };
        const QtGenericArgument empty = {};
        bool invoked = false;
        __try
        {
            invoked = qt.invoke(live,
                large ? "setExpectedLargeHeightPercent" :
                    "setExpectedHeightPercent",
                argument, empty, empty, empty, empty,
                empty, empty, empty, empty, empty);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
        if (invoked)
        {
            if (notify && g_wheelResizeMessage != 0)
            {
                PostMessageW(HWND_BROADCAST, g_wheelResizeMessage,
                    newPercent, 0);
            }
            return true;
        }
        return false;
    }

    bool ResizeMovieWindow(int steps)
    {
        if (steps == 0)
        {
            return false;
        }
        const LONG mode = InterlockedCompareExchange(&g_playerMode, 0, 0);
        if (mode != 1 && mode != 2)
        {
            return false;
        }
        const bool large = mode == 1;
        const wchar_t* valueName = large
            ? L"expectedLargeHeightPercent" : L"expectedHeightPercent";
        LONG volatile* cachedPercent = large
            ? &g_playerLargeSizePercent : &g_playerSmallSizePercent;
        LONG currentPercent = InterlockedCompareExchange(
            cachedPercent, 0, 0);
        if (currentPercent == 0)
        {
            DWORD registryPercent = large ? 100 : 30;
            DWORD valueSize = sizeof(registryPercent);
            const LSTATUS readResult = RegGetValueW(HKEY_CURRENT_USER,
                L"Software\\Totem\\vghd\\player", valueName,
                RRF_RT_REG_DWORD, nullptr, &registryPercent, &valueSize);
            if (readResult != ERROR_SUCCESS &&
                readResult != ERROR_FILE_NOT_FOUND)
            {
                return false;
            }
            currentPercent = static_cast<LONG>(registryPercent);
        }
        const DWORD newPercent = static_cast<DWORD>(std::clamp(
            static_cast<int>(currentPercent) + steps * 2,
            large ? 60 : 10, 200));
        if (newPercent == static_cast<DWORD>(currentPercent))
        {
            return true;
        }
        if (!SetMovieWindowPercent(mode, newPercent, true))
        {
            return false;
        }
        InterlockedExchange(cachedPercent, static_cast<LONG>(newPercent));
        return true;
    }

    void RefreshStaleMovieHitTest(HWND window)
    {
        const LONGLONG now = static_cast<LONGLONG>(GetTickCount64());
        const LONGLONG previous = InterlockedCompareExchange64(
            &g_lastHitTestRefreshTick, now, now);
        if (now - previous < 1'000 ||
            InterlockedCompareExchange64(
                &g_lastHitTestRefreshTick, now, previous) != previous)
        {
            return;
        }
        SetWindowPos(window, nullptr, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE |
            SWP_FRAMECHANGED);
    }

    LRESULT CALLBACK MovieMouseHook(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code == HC_ACTION)
        {
            const bool mouseMove = wParam == WM_MOUSEMOVE;
            const bool wheel = wParam == WM_MOUSEWHEEL ||
                wParam == WM_MOUSEHWHEEL;
            const bool buttonDown = wParam == WM_LBUTTONDOWN ||
                wParam == WM_RBUTTONDOWN || wParam == WM_MBUTTONDOWN ||
                wParam == WM_XBUTTONDOWN;
            if (!mouseMove && !wheel && !buttonDown)
            {
                return CallNextHookEx(
                    g_movieMouseHook, code, wParam, lParam);
            }

            static ULONGLONG lastMoveSample = 0;
            const ULONGLONG now = GetTickCount64();
            if (mouseMove && now - lastMoveSample < 16)
            {
                return CallNextHookEx(
                    g_movieMouseHook, code, wParam, lParam);
            }
            if (mouseMove)
            {
                lastMoveSample = now;
            }

            const auto mouse = reinterpret_cast<const MSLLHOOKSTRUCT*>(lParam);
            MovieWindowAtPoint search =
                FindVisibleMovieWindowAtPoint(mouse->pt);
            InterlockedExchangePointer(&g_pointerMovieWindow, search.window);
            InterlockedExchange(&g_pointerOverVisiblePixel,
                search.visiblePixel ? 1 : 0);

            if (wheel && search.window != nullptr &&
                !search.visiblePixel)
            {
                RefreshStaleMovieHitTest(search.window);
            }

            if (mouseMove || buttonDown)
            {
                return CallNextHookEx(
                    g_movieMouseHook, code, wParam, lParam);
            }

            if (wheel && search.window != nullptr &&
                search.visiblePixel)
            {
                if (InterlockedCompareExchange(&g_playerLocked, 0, 0) != 0 &&
                    InterlockedCompareExchange(
                        &g_playerWheelWhileLocked, 0, 0) == 0)
                {
                    return CallNextHookEx(
                        g_movieMouseHook, code, wParam, lParam);
                }
                const int delta = GET_WHEEL_DELTA_WPARAM(mouse->mouseData);
                if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                {
                    const LONG accumulated = InterlockedExchangeAdd(
                        &g_playerVolumeWheelDelta, delta) + delta;
                    const int steps = accumulated / WHEEL_DELTA;
                    if (steps != 0 && g_volumeWheelMessage != 0)
                    {
                        InterlockedExchangeAdd(&g_playerVolumeWheelDelta,
                            -steps * WHEEL_DELTA);
                        PostMessageW(HWND_BROADCAST, g_volumeWheelMessage,
                            static_cast<WPARAM>(steps),
                            static_cast<LPARAM>(InterlockedCompareExchange(
                                &g_playerMode, 0, 0)));
                    }
                    return 1;
                }
                if (InterlockedCompareExchange(
                        &g_playerWheelResize, 0, 0) == 0)
                {
                    return CallNextHookEx(
                        g_movieMouseHook, code, wParam, lParam);
                }
                const LONG accumulated = InterlockedExchangeAdd(
                    &g_playerWheelDelta, delta) + delta;
                const int steps = accumulated / WHEEL_DELTA;
                if (steps != 0 && g_wheelResizeMessage != 0)
                {
                    InterlockedExchangeAdd(&g_playerWheelDelta,
                        -steps * WHEEL_DELTA);
                    PostMessageW(HWND_BROADCAST, g_wheelResizeMessage,
                        static_cast<WPARAM>(steps),
                        static_cast<LPARAM>(InterlockedCompareExchange(
                            &g_playerMode, 0, 0)));
                }
                return 1;
            }
        }
        return CallNextHookEx(g_movieMouseHook, code, wParam, lParam);
    }

    void SetMovieWindowClickThrough(HWND window, bool enabled)
    {
        const LONG_PTR style = GetWindowLongPtrW(window, GWL_EXSTYLE);
        if (enabled)
        {
            if ((style & WS_EX_TRANSPARENT) != 0)
            {
                return;
            }
            SetWindowLongPtrW(window, GWL_EXSTYLE,
                style | WS_EX_TRANSPARENT);
            if (!SetPropW(window, MovieWindowClickThroughProperty,
                    reinterpret_cast<HANDLE>(static_cast<INT_PTR>(1))))
            {
                SetWindowLongPtrW(window, GWL_EXSTYLE, style);
            }
        }
        else if (RemovePropW(window,
            MovieWindowClickThroughProperty) != nullptr)
        {
            SetWindowLongPtrW(window, GWL_EXSTYLE,
                style & ~WS_EX_TRANSPARENT);
        }
    }

    WNDPROC OriginalMovieWindowProc(HWND window)
    {
        WNDPROC original = nullptr;
        AcquireSRWLockShared(&g_movieWindowLock);
        for (const auto& entry : g_movieWindows)
        {
            if (entry.window == window)
            {
                original = entry.original;
                break;
            }
        }
        ReleaseSRWLockShared(&g_movieWindowLock);
        return original;
    }

    bool IsLockedPlayerInteractionMessage(UINT message)
    {
        return message == WM_LBUTTONDOWN || message == WM_LBUTTONUP ||
            message == WM_LBUTTONDBLCLK || message == WM_RBUTTONDOWN ||
            message == WM_RBUTTONUP || message == WM_RBUTTONDBLCLK ||
            message == WM_MBUTTONDOWN || message == WM_MBUTTONUP ||
            message == WM_MBUTTONDBLCLK || message == WM_XBUTTONDOWN ||
            message == WM_XBUTTONUP || message == WM_XBUTTONDBLCLK ||
            message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL ||
            message == WM_CONTEXTMENU;
    }

    bool IsMouseButtonDown()
    {
        return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_XBUTTON1) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_XBUTTON2) & 0x8000) != 0;
    }

    LRESULT CALLBACK MovieWindowProc(HWND window, UINT message,
        WPARAM wParam, LPARAM lParam)
    {
        if (g_movieWindowProbeMessage != 0 &&
            message == g_movieWindowProbeMessage)
        {
            return MovieWindowProbeResult;
        }
        if (message == WM_NCHITTEST &&
            InterlockedCompareExchange(&g_playerLocked, 0, 0) != 0)
        {
            if (InterlockedCompareExchange(
                    &g_playerClickThrough, 0, 0) != 0)
            {
                RefreshStaleMovieHitTest(window);
                return HTTRANSPARENT;
            }
            const HWND pointerWindow = static_cast<HWND>(
                InterlockedCompareExchangePointer(
                    &g_pointerMovieWindow, nullptr, nullptr));
            if (pointerWindow == window && InterlockedCompareExchange(
                    &g_pointerOverVisiblePixel, 0, 0) == 0)
            {
                RefreshStaleMovieHitTest(window);
                return HTTRANSPARENT;
            }
            // Windows normally excludes zero-alpha layered pixels before
            // reaching this procedure. If that cached state goes stale, do
            // an immediate alpha check for a real click. Ordinary mouse-move
            // hit tests use the 16 ms hook cache above.
            if (IsMouseButtonDown())
            {
                POINT point = {
                    static_cast<short>(LOWORD(lParam)),
                    static_cast<short>(HIWORD(lParam))
                };
                if (!IsPointOverVisibleMoviePixel(window, point))
                {
                    return HTTRANSPARENT;
                }
            }
            return HTCLIENT;
        }
        if (InterlockedCompareExchange(&g_playerLocked, 0, 0) != 0 &&
            IsLockedPlayerInteractionMessage(message))
        {
            // Opaque pixels still hit this HWND so the locked player remains
            // stationary, but must not receive iStripper's click actions.
            // Transparent layered pixels are excluded by Windows before this
            // procedure is called, so underlying applications remain usable.
            return 0;
        }

        WNDPROC original = OriginalMovieWindowProc(window);
        const LRESULT result = original == nullptr
            ? DefWindowProcW(window, message, wParam, lParam)
            : CallWindowProcW(original, window, message, wParam, lParam);
        if (message == WM_STYLECHANGING &&
            static_cast<int>(wParam) == GWL_EXSTYLE && lParam != 0 &&
            InterlockedCompareExchange(&g_playerLocked, 0, 0) != 0 &&
            InterlockedCompareExchange(&g_playerClickThrough, 0, 0) != 0)
        {
            reinterpret_cast<STYLESTRUCT*>(lParam)->styleNew |=
                WS_EX_TRANSPARENT;
        }
        return result;
    }

    bool TryHasMovieWindowProc(HWND window, bool& active)
    {
        DWORD_PTR result = 0;
        if (g_movieWindowProbeMessage == 0 ||
            SendMessageTimeoutW(window, g_movieWindowProbeMessage, 0, 0,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, 250, &result) == 0)
        {
            return false;
        }
        active = result == static_cast<DWORD_PTR>(MovieWindowProbeResult);
        return true;
    }

    bool SubclassMovieWindow(HWND window)
    {
        if (InterlockedCompareExchange(&g_playerLocked, 0, 0) == 0 ||
            !IsMovieWindow(window))
        {
            return false;
        }

        if (InterlockedCompareExchange(&g_playerClickThrough, 0, 0) != 0)
        {
            SetMovieWindowClickThrough(window, true);
        }
        if (InterlockedCompareExchange(&g_playerLocked, 0, 0) == 0)
        {
            SetMovieWindowClickThrough(window, false);
            return false;
        }
        if (InterlockedCompareExchange(&g_playerClickThrough, 0, 0) == 0)
        {
            SetMovieWindowClickThrough(window, false);
        }

        bool alreadySubclassed = false;
        const bool probeCompleted =
            TryHasMovieWindowProc(window, alreadySubclassed);
        AcquireSRWLockExclusive(&g_movieWindowLock);
        MovieWindowSubclass* empty = nullptr;
        MovieWindowSubclass* existing = nullptr;
        for (auto& entry : g_movieWindows)
        {
            if (entry.window == window)
            {
                existing = &entry;
                break;
            }
            if (empty == nullptr && entry.window == nullptr)
            {
                empty = &entry;
            }
        }
        if (alreadySubclassed)
        {
            ReleaseSRWLockExclusive(&g_movieWindowLock);
            return existing != nullptr;
        }
        if (existing != nullptr && !probeCompleted)
        {
            ReleaseSRWLockExclusive(&g_movieWindowLock);
            return false;
        }
        if (existing == nullptr && empty == nullptr)
        {
            ReleaseSRWLockExclusive(&g_movieWindowLock);
            return false;
        }

        SetLastError(ERROR_SUCCESS);
        const LONG_PTR previous = SetWindowLongPtrW(window, GWLP_WNDPROC,
            reinterpret_cast<LONG_PTR>(&MovieWindowProc));
        if (previous == 0 && GetLastError() != ERROR_SUCCESS)
        {
            ReleaseSRWLockExclusive(&g_movieWindowLock);
            return false;
        }
        MovieWindowSubclass* entry = existing == nullptr ? empty : existing;
        entry->window = window;
        entry->original = reinterpret_cast<WNDPROC>(previous);
        ReleaseSRWLockExclusive(&g_movieWindowLock);
        return true;
    }

    void RemoveMovieWindow(HWND window)
    {
        AcquireSRWLockExclusive(&g_movieWindowLock);
        for (auto& entry : g_movieWindows)
        {
            if (entry.window == window)
            {
                entry = {};
                break;
            }
        }
        ReleaseSRWLockExclusive(&g_movieWindowLock);
    }

    BOOL CALLBACK FindMovieWindow(HWND window, LPARAM)
    {
        SubclassMovieWindow(window);
        return TRUE;
    }

    BOOL CALLBACK UnlockMovieWindow(HWND window, LPARAM)
    {
        if (IsMovieWindow(window))
        {
            SetMovieWindowClickThrough(window, false);
        }
        return TRUE;
    }

    void CALLBACK MovieWindowEvent(HWINEVENTHOOK, DWORD event,
        HWND window, LONG objectId, LONG childId, DWORD, DWORD)
    {
        if (objectId != OBJID_WINDOW || childId != CHILDID_SELF)
        {
            return;
        }
        if (event == EVENT_OBJECT_DESTROY)
        {
            RemoveMovieWindow(window);
        }
        else
        {
            SubclassMovieWindow(window);
        }
    }

    DWORD WINAPI MovieWindowWatcher(void* parameter)
    {
        const HANDLE readyEvent = static_cast<HANDLE>(parameter);
        MSG message = {};
        PeekMessageW(&message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
        g_movieWindowEventHook = SetWinEventHook(
            EVENT_OBJECT_CREATE, EVENT_OBJECT_LOCATIONCHANGE,
            g_bridgeModule, &MovieWindowEvent,
            GetCurrentProcessId(), 0, WINEVENT_OUTOFCONTEXT);
        g_movieMouseHook = SetWindowsHookExW(
            WH_MOUSE_LL, &MovieMouseHook, g_bridgeModule, 0);
        const HRESULT result = g_movieWindowEventHook == nullptr
            ? HRESULT_FROM_WIN32(
                GetLastError() == ERROR_SUCCESS
                    ? ERROR_HOOK_NOT_INSTALLED
                    : GetLastError())
            : BridgeSuccess;
        InterlockedExchange(&g_movieWindowWatcherResult, result);
        SetEvent(readyEvent);
        if (result < 0)
        {
            if (g_movieMouseHook != nullptr)
            {
                UnhookWindowsHookEx(g_movieMouseHook);
                g_movieMouseHook = nullptr;
            }
            return 0;
        }

        while (GetMessageW(&message, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        UnhookWindowsHookEx(g_movieMouseHook);
        UnhookWinEvent(g_movieWindowEventHook);
        g_movieMouseHook = nullptr;
        g_movieWindowEventHook = nullptr;
        return 0;
    }

    BOOL CALLBACK StartMovieWindowWatcher(
        PINIT_ONCE, PVOID, PVOID*)
    {
        const HANDLE readyEvent =
            CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (readyEvent == nullptr)
        {
            InterlockedExchange(&g_movieWindowWatcherResult,
                HRESULT_FROM_WIN32(GetLastError()));
            return TRUE;
        }

        const HANDLE thread = CreateThread(nullptr, 0,
            &MovieWindowWatcher, readyEvent, 0, nullptr);
        if (thread == nullptr)
        {
            InterlockedExchange(&g_movieWindowWatcherResult,
                HRESULT_FROM_WIN32(GetLastError()));
            CloseHandle(readyEvent);
            return TRUE;
        }
        CloseHandle(thread);

        const DWORD waitResult =
            WaitForSingleObject(readyEvent, 5000);
        if (waitResult == WAIT_OBJECT_0)
        {
            CloseHandle(readyEvent);
        }
        else
        {
            InterlockedExchange(&g_movieWindowWatcherResult,
                HRESULT_FROM_WIN32(
                    waitResult == WAIT_TIMEOUT
                        ? ERROR_TIMEOUT
                        : GetLastError()));
            // The watcher still owns this handle until it signals readiness.
        }
        return TRUE;
    }

    HRESULT EnsureMovieWindowWatcher()
    {
        if (!InitOnceExecuteOnce(&g_movieWindowWatcherOnce,
                &StartMovieWindowWatcher, nullptr, nullptr))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        return static_cast<HRESULT>(InterlockedCompareExchange(
            &g_movieWindowWatcherResult, E_PENDING, E_PENDING));
    }

    HRESULT SetPlayerLocked(bool locked)
    {
        if (locked)
        {
            const HRESULT pinResult = PinBridge();
            if (pinResult < 0)
            {
                return pinResult;
            }
            if (g_movieWindowProbeMessage == 0)
            {
                g_movieWindowProbeMessage = RegisterWindowMessageW(
                    L"IStripperQuickPlayer.MovieWindowProc.v19");
                if (g_movieWindowProbeMessage == 0)
                {
                    return HRESULT_FROM_WIN32(GetLastError());
                }
            }
            InterlockedExchange(&g_playerLocked, 1);
            const HRESULT watcherResult = EnsureMovieWindowWatcher();
            if (watcherResult < 0)
            {
                InterlockedExchange(&g_playerLocked, 0);
                return watcherResult;
            }
            EnumWindows(&FindMovieWindow, 0);
            return BridgeSuccess;
        }

        InterlockedExchange(&g_playerLocked, 0);
        EnumWindows(&UnlockMovieWindow, 0);
        return BridgeSuccess;
    }

    HRESULT SetPlayerClickThrough(bool enabled)
    {
        InterlockedExchange(&g_playerClickThrough, enabled ? 1 : 0);
        if (enabled && InterlockedCompareExchange(&g_playerLocked, 0, 0) != 0)
        {
            EnumWindows(&FindMovieWindow, 0);
        }
        else if (!enabled)
        {
            EnumWindows(&UnlockMovieWindow, 0);
        }
        return BridgeSuccess;
    }

    HRESULT SetPlayerWheelResize(SIZE_T options)
    {
        const bool enabled = (options & 1) != 0;
        InterlockedExchange(&g_playerWheelResize, enabled ? 1 : 0);
        InterlockedExchange(
            &g_playerWheelWhileLocked, (options & 2) != 0 ? 1 : 0);
        InterlockedExchange(&g_playerWheelDelta, 0);
        InterlockedExchange(&g_playerVolumeWheelDelta, 0);

        if (g_wheelResizeMessage == 0)
        {
            g_wheelResizeMessage = RegisterWindowMessageW(
                L"IStripperQuickPlayer.WheelResize.v1");
            if (g_wheelResizeMessage == 0)
            {
                InterlockedExchange(&g_playerWheelResize, 0);
                return HRESULT_FROM_WIN32(GetLastError());
            }
        }
        if (g_volumeWheelMessage == 0)
        {
            g_volumeWheelMessage = RegisterWindowMessageW(
                L"IStripperQuickPlayer.VolumeWheel.v1");
            if (g_volumeWheelMessage == 0)
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }
        }

        const HRESULT pinResult = PinBridge();
        if (pinResult < 0)
        {
            InterlockedExchange(&g_playerWheelResize, 0);
            return pinResult;
        }
        const HRESULT watcherResult = EnsureMovieWindowWatcher();
        if (watcherResult < 0 || g_movieMouseHook == nullptr)
        {
            InterlockedExchange(&g_playerWheelResize, 0);
            return watcherResult < 0 ? watcherResult :
                HRESULT_FROM_WIN32(ERROR_HOOK_NOT_INSTALLED);
        }
        return BridgeSuccess;
    }

    HRESULT SetPlayerMode(SIZE_T mode)
    {
        if (mode < 1 || mode > 3)
        {
            return E_INVALIDARG;
        }
        const LONG previous = InterlockedExchange(
            &g_playerMode, static_cast<LONG>(mode));
        if (previous != static_cast<LONG>(mode))
        {
            InterlockedExchange(&g_playerSmallSizePercent, 0);
            InterlockedExchange(&g_playerLargeSizePercent, 0);
        }
        if (previous == 3 && mode != 3)
        {
            void* pendingCard = nullptr;
            AcquireSRWLockExclusive(&g_fullScreenStateLock);
            g_fullScreenClips.clear();
            g_fullScreenSlots.clear();
            pendingCard = g_fullScreenReplacement.playableCard;
            g_fullScreenReplacement = {};
            g_fullScreenSceneParent = nullptr;
            g_nextFullScreenSlotId = 1;
            ReleaseSRWLockExclusive(&g_fullScreenStateLock);
            DeletePlayableCardLater(pendingCard);
            PublishFullScreenState();
        }
        return BridgeSuccess;
    }

    HRESULT SetPlayerSizePercent(SIZE_T packed)
    {
        const auto value = static_cast<std::uint64_t>(packed);
        const DWORD mode = static_cast<DWORD>(value >> 32);
        const DWORD percent = static_cast<DWORD>(value);
        if ((mode != 1 && mode != 2) ||
            percent < 1 || percent > 200)
        {
            return E_INVALIDARG;
        }
        if (!SetMovieWindowPercent(
                static_cast<LONG>(mode), percent, false))
        {
            return E_FAIL;
        }
        InterlockedExchange(mode == 1
            ? &g_playerLargeSizePercent : &g_playerSmallSizePercent,
            static_cast<LONG>(percent));
        return BridgeSuccess;
    }

    HRESULT SetPlayerLarge(bool large)
    {
        DWORD mode = 0;
        DWORD modeSize = sizeof(mode);
        const LSTATUS modeResult = RegGetValueW(HKEY_CURRENT_USER,
            L"Software\\Totem\\vghd\\player", L"playingMode",
            RRF_RT_REG_DWORD, nullptr, &mode, &modeSize);
        if (modeResult != ERROR_SUCCESS)
        {
            return HRESULT_FROM_WIN32(modeResult);
        }
        if (mode == (large ? 1u : 2u))
        {
            return BridgeSuccess;
        }

        HWND window = nullptr;
        EnumWindows(&FindVisibleMovieWindow,
            reinterpret_cast<LPARAM>(&window));
        if (window == nullptr)
        {
            return BridgeSuccess;
        }

        RECT client = {};
        if (!GetClientRect(window, &client))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        const LPARAM point = MAKELPARAM(
            (client.right - client.left) / 2,
            (client.bottom - client.top) / 2);
        if (!PostMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, point) ||
            !PostMessageW(window, WM_LBUTTONUP, 0, point))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        return BridgeSuccess;
    }

    bool IsReadable(const void* address, std::size_t length)
    {
        if (address == nullptr || length == 0)
        {
            return false;
        }

        const auto start = reinterpret_cast<std::uintptr_t>(address);
        if (length > static_cast<std::size_t>(UINTPTR_MAX - start))
        {
            return false;
        }
        const auto end = start + length;
        auto current = start;
        while (current < end)
        {
            MEMORY_BASIC_INFORMATION memory = {};
            if (VirtualQuery(reinterpret_cast<const void*>(current), &memory,
                    sizeof(memory)) != sizeof(memory) ||
                memory.State != MEM_COMMIT ||
                (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
            {
                return false;
            }
            const auto regionStart =
                reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
            const auto regionEnd = regionStart + memory.RegionSize;
            if (current < regionStart || regionEnd <= current)
            {
                return false;
            }
            current = regionEnd < end ? regionEnd : end;
        }
        return true;
    }

    bool IsWritable(const void* address, std::size_t length)
    {
        if (!IsReadable(address, length))
        {
            return false;
        }

        MEMORY_BASIC_INFORMATION memory = {};
        if (VirtualQuery(address, &memory, sizeof(memory)) != sizeof(memory))
        {
            return false;
        }

        switch (memory.Protect & 0xFF)
        {
        case PAGE_READWRITE:
        case PAGE_WRITECOPY:
        case PAGE_EXECUTE_READWRITE:
        case PAGE_EXECUTE_WRITECOPY:
            return true;
        default:
            return false;
        }
    }

    unsigned char* ImageBase()
    {
        return reinterpret_cast<unsigned char*>(GetModuleHandleW(L"vghd.exe"));
    }

    bool IsRvaInImage(std::uintptr_t rva, std::size_t length)
    {
        const auto base = ImageBase();
        if (!IsReadable(base, sizeof(IMAGE_DOS_HEADER)))
        {
            return false;
        }

        const auto dosHeader = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE || dosHeader->e_lfanew <= 0)
        {
            return false;
        }

        const auto ntHeader = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dosHeader->e_lfanew);
        if (!IsReadable(ntHeader, sizeof(*ntHeader)) || ntHeader->Signature != IMAGE_NT_SIGNATURE)
        {
            return false;
        }

        const std::size_t imageSize = ntHeader->OptionalHeader.SizeOfImage;
        return rva != 0 && rva < imageSize &&
            length <= imageSize - rva;
    }

    const IMAGE_NT_HEADERS64* ImageHeaders()
    {
        const auto base = ImageBase();
        if (!IsReadable(base, sizeof(IMAGE_DOS_HEADER)))
        {
            return nullptr;
        }
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
        {
            return nullptr;
        }
        const auto headers = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            base + dos->e_lfanew);
        return IsReadable(headers, sizeof(*headers)) &&
            headers->Signature == IMAGE_NT_SIGNATURE
            ? headers
            : nullptr;
    }

    bool OffsetProfilePath(wchar_t (&path)[MAX_PATH])
    {
        wchar_t localAppData[MAX_PATH] = {};
        const DWORD localLength = GetEnvironmentVariableW(
            L"LOCALAPPDATA", localAppData, MAX_PATH);
        if (localLength > 0 && localLength < MAX_PATH)
        {
            wchar_t directory[MAX_PATH] = {};
            if (swprintf_s(directory, L"%s\\IStripperQuickPlayer",
                    localAppData) > 0 &&
                (CreateDirectoryW(directory, nullptr) ||
                    GetLastError() == ERROR_ALREADY_EXISTS) &&
                swprintf_s(path, L"%s\\vghd-offsets.ini", directory) > 0)
            {
                return true;
            }
        }

        HMODULE module = nullptr;
        if (!GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&OffsetProfilePath), &module) ||
            GetModuleFileNameW(module, path, MAX_PATH) == 0)
        {
            return false;
        }

        wchar_t* fileName = std::wcsrchr(path, L'\\');
        if (fileName == nullptr)
        {
            return false;
        }
        return wcscpy_s(fileName + 1,
            MAX_PATH - static_cast<std::size_t>(fileName + 1 - path),
            L"vghd-offsets.ini") == 0;
    }

    bool ImageVersion(wchar_t (&version)[64])
    {
        wchar_t imagePath[MAX_PATH] = {};
        if (GetModuleFileNameW(GetModuleHandleW(L"vghd.exe"),
                imagePath, MAX_PATH) == 0)
        {
            return false;
        }

        DWORD ignored = 0;
        const DWORD size = GetFileVersionInfoSizeW(imagePath, &ignored);
        if (size == 0)
        {
            return false;
        }
        void* data = HeapAlloc(GetProcessHeap(), 0, size);
        if (data == nullptr)
        {
            return false;
        }

        VS_FIXEDFILEINFO* info = nullptr;
        UINT infoSize = 0;
        const bool found = GetFileVersionInfoW(imagePath, 0, size, data) &&
            VerQueryValueW(data, L"\\",
                reinterpret_cast<void**>(&info), &infoSize) &&
            info != nullptr && infoSize >= sizeof(*info) &&
            info->dwSignature == VS_FFI_SIGNATURE;
        if (found)
        {
            swprintf_s(version, L"%u.%u.%u.%u",
                HIWORD(info->dwFileVersionMS),
                LOWORD(info->dwFileVersionMS),
                HIWORD(info->dwFileVersionLS),
                LOWORD(info->dwFileVersionLS));
        }
        HeapFree(GetProcessHeap(), 0, data);
        return found;
    }

    bool IsExecutableAddress(const unsigned char* address)
    {
        const auto headers = ImageHeaders();
        const auto base = ImageBase();
        if (headers == nullptr || base == nullptr)
        {
            return false;
        }
        const auto sections = IMAGE_FIRST_SECTION(headers);
        for (unsigned index = 0; index < headers->FileHeader.NumberOfSections;
            index++)
        {
            const std::size_t size = sections[index].Misc.VirtualSize;
            const auto start = base + sections[index].VirtualAddress;
            if ((sections[index].Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0 &&
                address >= start && address < start + size)
            {
                return true;
            }
        }
        return false;
    }

    bool IsExecutableMemory(const void* address)
    {
        MEMORY_BASIC_INFORMATION memory = {};
        if (address == nullptr ||
            VirtualQuery(address, &memory, sizeof(memory)) != sizeof(memory) ||
            memory.State != MEM_COMMIT ||
            (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
        {
            return false;
        }

        switch (memory.Protect & 0xFF)
        {
        case PAGE_EXECUTE:
        case PAGE_EXECUTE_READ:
        case PAGE_EXECUTE_READWRITE:
        case PAGE_EXECUTE_WRITECOPY:
            return true;
        default:
            return false;
        }
    }

    const unsigned char* FindSequence(const unsigned char* start,
        std::size_t length, const unsigned char* sequence,
        std::size_t sequenceLength)
    {
        if (!IsReadable(start, length) || sequenceLength == 0 ||
            sequenceLength > length)
        {
            return nullptr;
        }
        for (std::size_t offset = 0;
            offset <= length - sequenceLength; offset++)
        {
            if (std::memcmp(start + offset, sequence, sequenceLength) == 0)
            {
                return start + offset;
            }
        }
        return nullptr;
    }

    bool ResolveFullScreenLayout(unsigned char* startNextShow)
    {
        FsClipNodeNextShowClipRva = 0;
        FsClipNodeSceneOffset = 0;
        SceneNodesOffset = 0;
        FsClipNodePlayableCardOffset = 0;
        PlayableCardTagOffset = 0;
        PlayableCardClipsOffset = 0;
        PlayableCardSize = 0;
        CardSequencerNextOffset = 0;
        NextCardPlayableCardOffset = 0;
        SceneExpectedPendingOffset = 0;
        SceneExpectedSelectionOffset = 0;
        SceneExpectedCardOffset = 0;
        SceneExpectedModeOffset = 0;
        PlayableCardExactConstructorRva = 0;
        CardSequencerInsertNextRva = 0;
        CardSequencerTakeNextAtRva = 0;
        VghdOperatorNewRva = 0;
        FsClipNodeVtableRva = FindVtableRva(".?AVFsClipNode@@");
        PlayableCardVtableRva = FindVtableRva(
            ".?AVPlayableCard@Model@@");
        g_cardSequencerVtableRva = FindVtableRva(
            ".?AVCardSequencer@Model@@");
        auto insertNext = FindUniqueFunction(
            CardSequencerInsertNextSignature,
            sizeof(CardSequencerInsertNextSignature), 5);
        auto exactConstructor = FindUniqueFunction(
            PlayableCardExactConstructorSignature,
            sizeof(PlayableCardExactConstructorSignature), -1);
        auto takeNextAt = FindUniqueFunction(
            CardSequencerTakeNextAtSignature,
            sizeof(CardSequencerTakeNextAtSignature), -1);
        if (startNextShow == nullptr || insertNext == nullptr ||
            exactConstructor == nullptr ||
            !IsReadable(startNextShow, 1024) ||
            !IsReadable(insertNext, 1536) || FsClipNodeVtableRva == 0 ||
            PlayableCardVtableRva == 0 || g_cardSequencerVtableRva == 0)
            return false;
        PlayableCardExactConstructorRva = RvaFromAddress(exactConstructor);
        CardSequencerInsertNextRva = RvaFromAddress(insertNext);
        if (takeNextAt != nullptr)
            CardSequencerTakeNextAtRva = RvaFromAddress(takeNextAt);
        unsigned char* nextShowClip = nullptr;

        if (!IsReadable(exactConstructor, 192))
            return false;
        std::size_t constructorTagOffset = 0;
        for (std::size_t offset = 0; offset + 9 <= 192; ++offset)
        {
            const auto candidate = exactConstructor + offset;
            if (candidate[0] == 0x48 && candidate[1] == 0x8D &&
                candidate[2] == 0x4F && candidate[4] == 0x48 &&
                candidate[5] == 0x8B && candidate[6] == 0xD3)
                constructorTagOffset = candidate[3];
            if (candidate[0] == 0x48 && candidate[1] == 0x8D &&
                candidate[2] == 0x5F && candidate[4] == 0x48 &&
                candidate[5] == 0x89 && candidate[6] == 0x5C &&
                candidate[7] == 0x24)
                PlayableCardClipsOffset = candidate[3];
        }

        for (std::size_t offset = 0; offset + 27 <= 1024; ++offset)
        {
            const auto candidate = startNextShow + offset;
            if (candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x56 && candidate[4] == 0x48 &&
                candidate[5] == 0x81 && candidate[6] == 0xC2 &&
                candidate[11] == 0x48 && candidate[12] == 0x8D &&
                candidate[13] == 0x4C && candidate[14] == 0x24)
            {
                FsClipNodeSceneOffset = candidate[3];
                SceneNodesOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 7);
            }
            if (candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x90 && candidate[7] == 0x48 &&
                candidate[8] == 0x85 && candidate[9] == 0xD2 &&
                candidate[10] == 0x74 && candidate[12] == 0x48 &&
                candidate[13] == 0x83 && candidate[14] == 0xC2)
            {
                FsClipNodePlayableCardOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 3);
                PlayableCardTagOffset = candidate[15];
            }
            if (candidate[0] == 0x80 && candidate[1] == 0xBA &&
                candidate[6] == 0 && candidate[7] == 0x0F &&
                candidate[8] == 0x84 && candidate[13] == 0x48 &&
                candidate[14] == 0x8B && candidate[15] == 0x82 &&
                candidate[20] == 0x48 && candidate[21] == 0x89 &&
                candidate[22] == 0x86)
            {
                SceneExpectedPendingOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 2);
                SceneExpectedCardOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 16);
                const std::size_t assignedNodeCard =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 23);
                if (FsClipNodePlayableCardOffset != 0 &&
                    assignedNodeCard != FsClipNodePlayableCardOffset)
                    return false;
            }
            if (candidate[0] == 0x83 && candidate[1] == 0xBA &&
                candidate[6] == 0x01 && candidate[7] == 0x0F &&
                candidate[8] == 0x44 && candidate[9] == 0xC1 &&
                candidate[10] == 0x89 && candidate[11] == 0x86)
                SceneExpectedModeOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 2);
            if (candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x4E && candidate[4] == 0x0F &&
                candidate[5] == 0xB6 && candidate[6] == 0x99)
            {
                if (FsClipNodeSceneOffset != 0 &&
                    candidate[3] != FsClipNodeSceneOffset)
                    return false;
                SceneExpectedSelectionOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 7);
            }
            if (candidate[0] == 0x44 && candidate[1] == 0x0F &&
                candidate[2] == 0xB6 && candidate[3] == 0xC3 &&
                candidate[4] == 0x49 && candidate[5] == 0x8B &&
                candidate[6] == 0xD4 && candidate[7] == 0x48 &&
                candidate[8] == 0x8B && candidate[9] == 0xCE &&
                candidate[10] == 0xE8)
            {
                auto target = candidate + 15 +
                    *reinterpret_cast<const std::int32_t*>(candidate + 11);
                if (nextShowClip != nullptr && nextShowClip != target)
                    return false;
                nextShowClip = target;
            }
        }
        if (nextShowClip != nullptr && IsReadable(nextShowClip, 128))
            FsClipNodeNextShowClipRva = RvaFromAddress(nextShowClip);

        const auto headers = ImageHeaders();
        auto base = ImageBase();
        unsigned char* operatorNew = nullptr;
        const auto sections = headers == nullptr ? nullptr :
            IMAGE_FIRST_SECTION(headers);
        for (unsigned sectionIndex = 0; sections != nullptr &&
            sectionIndex < headers->FileHeader.NumberOfSections;
            ++sectionIndex)
        {
            if ((sections[sectionIndex].Characteristics &
                    IMAGE_SCN_MEM_EXECUTE) == 0)
                continue;
            auto start = base + sections[sectionIndex].VirtualAddress;
            const std::size_t size = sections[sectionIndex].Misc.VirtualSize;
            if (!IsReadable(start, size))
                continue;
            for (std::size_t offset = 5; offset + 5 <= size; ++offset)
            {
                auto call = start + offset;
                if (call[0] != 0xE8 || call + 5 +
                    *reinterpret_cast<const std::int32_t*>(call + 1) !=
                        exactConstructor)
                    continue;
                const std::size_t earliest = offset > 96 ? offset - 96 : 0;
                for (std::size_t previous = offset; previous-- > earliest;)
                {
                    auto allocation = start + previous;
                    if (previous + 10 > offset || allocation[0] != 0xB9 ||
                        allocation[5] != 0xE8)
                        continue;
                    const std::size_t allocationSize =
                        *reinterpret_cast<const std::uint32_t*>(
                            allocation + 1);
                    if (allocationSize < 0x20 || allocationSize > 0x400 ||
                        (allocationSize & 7) != 0)
                        continue;
                    auto candidate = allocation + 10 +
                        *reinterpret_cast<const std::int32_t*>(
                            allocation + 6);
                    if (operatorNew != nullptr && operatorNew != candidate)
                        return false;
                    if (PlayableCardSize != 0 &&
                        PlayableCardSize != allocationSize)
                        return false;
                    operatorNew = candidate;
                    PlayableCardSize = allocationSize;
                    break;
                }
            }
        }
        if (operatorNew != nullptr)
            VghdOperatorNewRva = RvaFromAddress(operatorNew);

        for (std::size_t offset = 0; offset + 12 <= 1536; ++offset)
        {
            const auto candidate = insertNext + offset;
            if (candidate[0] == 0x49 && candidate[1] == 0x8B &&
                candidate[2] == 0x45 && candidate[4] == 0x8B &&
                candidate[5] == 0x48 && candidate[6] == 0x0C &&
                candidate[7] == 0x2B && candidate[8] == 0x48 &&
                candidate[9] == 0x08)
                CardSequencerNextOffset = candidate[3];
            if (candidate[0] == 0x4C && candidate[1] == 0x89 &&
                candidate[2] == 0x65 && candidate[4] == 0x48 &&
                candidate[5] == 0x89 && candidate[6] == 0x5D &&
                candidate[8] == 0x44 && candidate[9] == 0x89 &&
                candidate[10] == 0x75)
            {
                const int objectStart = static_cast<signed char>(
                    candidate[3]);
                const int cardField = static_cast<signed char>(
                    candidate[7]);
                if (cardField <= objectStart)
                    return false;
                NextCardPlayableCardOffset = static_cast<std::size_t>(
                    cardField - objectStart);
            }
        }

        return FsClipNodeSceneOffset > 0 &&
            FsClipNodeSceneOffset <= 0x100 && SceneNodesOffset > 0 &&
            SceneNodesOffset <= 0x400 &&
            FsClipNodePlayableCardOffset > 0 &&
            FsClipNodePlayableCardOffset <= 0x400 &&
            PlayableCardTagOffset > 0 && PlayableCardTagOffset <= 0x100 &&
            constructorTagOffset == PlayableCardTagOffset &&
            PlayableCardClipsOffset > PlayableCardTagOffset &&
            PlayableCardClipsOffset <= 0x100 &&
            PlayableCardSize >= PlayableCardClipsOffset + sizeof(void*) &&
            CardSequencerNextOffset > 0 && CardSequencerNextOffset <= 0x200 &&
            NextCardPlayableCardOffset > 0 &&
            NextCardPlayableCardOffset <= 0x100 &&
            SceneExpectedPendingOffset > 0 &&
            SceneExpectedSelectionOffset > 0 &&
            SceneExpectedCardOffset > 0 && SceneExpectedModeOffset > 0 &&
            FsClipNodeNextShowClipRva > 0 &&
            PlayableCardExactConstructorRva > 0 &&
            VghdOperatorNewRva > 0;
    }

    bool IsCompatibleBpkSoundRawWrite(const unsigned char* call,
        const unsigned char* write)
    {
        if (!IsReadable(write, 64) ||
            std::memcmp(write, BpkSoundRawWriteSignature,
                sizeof(BpkSoundRawWriteSignature) - 1) != 0)
        {
            return false;
        }
        if (std::memcmp(write, BpkSoundRawWriteSignature,
                sizeof(BpkSoundRawWriteSignature)) == 0)
        {
            return true;
        }

        const unsigned char arguments[] = {
            0x49, 0x63, 0xF0, // movsxd rsi,r8d
            0x4C, 0x8B, 0xFA, // mov r15,rdx
            0x48, 0x8B, 0xF9  // mov rdi,rcx
        };
        return write[18] >= 0x20 && write[18] <= 0x80 &&
            (write[18] & 0x0F) == 0 &&
            call[-14] == 0x44 && call[-13] == 0x8B &&
            call[-12] == 0x44 && call[-11] == 0x24 &&
            call[-9] == 0x48 && call[-8] == 0x8B &&
            call[-7] == 0x54 && call[-6] == 0x24 &&
            call[-4] == 0x48 && call[-3] == 0x8B &&
            call[-2] == 0x48 &&
            call[-1] == static_cast<unsigned char>(VideoSoundOffset) &&
            FindSequence(write + sizeof(BpkSoundRawWriteSignature),
                64 - sizeof(BpkSoundRawWriteSignature),
                arguments, sizeof(arguments)) != nullptr;
    }

    bool ValidFunctionCandidate(const unsigned char* candidate, int kind)
    {
        if (!IsReadable(candidate, 128))
        {
            return false;
        }
        if (kind == 0)
        {
            const unsigned char mutex[] = { 0x48, 0x81, 0xC1 };
            const unsigned char state[] = { 0x8B, 0x43 };
            const auto stateLoad = FindSequence(candidate, 96,
                state, sizeof(state));
            return FindSequence(candidate, 48, mutex, sizeof(mutex)) != nullptr &&
                stateLoad != nullptr && stateLoad[3] == 0x83 &&
                stateLoad[4] == 0xF8 && stateLoad[5] == 0x01;
        }
        if (kind == 1)
        {
            const unsigned char mutex[] = { 0x48, 0x8D, 0xB9 };
            const unsigned char state[] = { 0x8B, 0x43 };
            const auto stateLoad = FindSequence(candidate, 112,
                state, sizeof(state));
            return FindSequence(candidate, 48, mutex, sizeof(mutex)) != nullptr &&
                stateLoad != nullptr && stateLoad[3] == 0x83 &&
                stateLoad[4] == 0xF8;
        }
        if (kind == 4)
        {
            if (WmvQueueMutexOffset == 0 ||
                WmvColorQueueOffset == 0 ||
                QueueBeginOffset == 0 ||
                QueueEndOffset == 0 ||
                QueueEntriesOffset == 0 ||
                candidate[12] != WmvQueueMutexOffset)
            {
                return false;
            }

            for (std::size_t offset = 16; offset + 24 <= 96; offset++)
            {
                std::size_t queue = 0;
                std::size_t next = 0;
                if (candidate[offset] == 0x48 &&
                    candidate[offset + 1] == 0x8B &&
                    candidate[offset + 2] == 0x5B)
                {
                    queue = candidate[offset + 3];
                    next = offset + 4;
                }
                else if (candidate[offset] == 0x48 &&
                    candidate[offset + 1] == 0x8B &&
                    candidate[offset + 2] == 0x9B)
                {
                    queue = *reinterpret_cast<const std::uint32_t*>(
                        candidate + offset + 3);
                    next = offset + 7;
                }
                if (queue != WmvColorQueueOffset ||
                    candidate[next] != 0x48 ||
                    candidate[next + 1] != 0x63 ||
                    candidate[next + 2] != 0x43 ||
                    candidate[next + 3] != QueueBeginOffset ||
                    candidate[next + 4] != 0x39 ||
                    candidate[next + 5] != 0x43 ||
                    candidate[next + 6] != QueueEndOffset)
                {
                    continue;
                }
                for (std::size_t entry = next + 7;
                    entry + 5 <= next + 24; entry++)
                {
                    if (candidate[entry] == 0x48 &&
                        candidate[entry + 1] == 0x8B &&
                        candidate[entry + 2] == 0x5C &&
                        candidate[entry + 3] == 0xC3 &&
                        candidate[entry + 4] == QueueEntriesOffset)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        if (kind == 5)
        {
            if (!IsReadable(candidate, 1536))
                return false;
            const unsigned char queue[] = {
                0x49, 0x8B, 0x45, 0x00, 0x8B, 0x48, 0x0C,
                0x2B, 0x48, 0x08
            };
            bool hasCard = false;
            for (std::size_t offset = 0; offset + 11 <= 1536; ++offset)
                if (candidate[offset] == 0x4C &&
                    candidate[offset + 1] == 0x89 &&
                    candidate[offset + 2] == 0x65 &&
                    candidate[offset + 4] == 0x48 &&
                    candidate[offset + 5] == 0x89 &&
                    candidate[offset + 6] == 0x5D &&
                    candidate[offset + 8] == 0x44 &&
                    candidate[offset + 9] == 0x89 &&
                    candidate[offset + 10] == 0x75)
                {
                    hasCard = true;
                    break;
                }
            if (!hasCard)
                return false;
            for (std::size_t offset = 0; offset + sizeof(queue) <= 1536;
                ++offset)
            {
                if (candidate[offset] == queue[0] &&
                    std::memcmp(candidate + offset + 4, queue + 4,
                        sizeof(queue) - 4) == 0)
                    return true;
            }
            return false;
        }
        const unsigned char mutex[] = { 0x48, 0x81, 0xC1 };
        const unsigned char rate[] = { 0xF2, 0x0F, 0x11, 0x73 };
        return FindSequence(candidate, 48, mutex, sizeof(mutex)) != nullptr &&
            FindSequence(candidate, 96, rate, sizeof(rate)) != nullptr;
    }

    unsigned char* FindUniqueFunction(const unsigned char* signature,
        std::size_t signatureLength, int kind)
    {
        const auto headers = ImageHeaders();
        auto base = ImageBase();
        if (headers == nullptr || base == nullptr)
        {
            return nullptr;
        }

        unsigned char* result = nullptr;
        const auto sections = IMAGE_FIRST_SECTION(headers);
        for (unsigned sectionIndex = 0;
            sectionIndex < headers->FileHeader.NumberOfSections;
            sectionIndex++)
        {
            if ((sections[sectionIndex].Characteristics &
                    IMAGE_SCN_MEM_EXECUTE) == 0)
            {
                continue;
            }
            auto start = base + sections[sectionIndex].VirtualAddress;
            const std::size_t size = sections[sectionIndex].Misc.VirtualSize;
            if (!IsReadable(start, size) || signatureLength > size)
            {
                continue;
            }
            for (std::size_t offset = 0;
                offset <= size - signatureLength; offset++)
            {
                auto candidate = start + offset;
                if (std::memcmp(candidate, signature, signatureLength) == 0 &&
                    (kind < 0 || ValidFunctionCandidate(candidate, kind)))
                {
                    if (result != nullptr)
                    {
                        return nullptr;
                    }
                    result = candidate;
                }
            }
        }
        return result;
    }

    std::uintptr_t FindVtableRva(const char* decoratedClassName)
    {
        const auto headers = ImageHeaders();
        auto base = ImageBase();
        if (headers == nullptr || base == nullptr)
        {
            return 0;
        }

        const std::size_t nameLength = std::strlen(decoratedClassName) + 1;
        unsigned char* name = nullptr;
        const auto sections = IMAGE_FIRST_SECTION(headers);
        for (unsigned sectionIndex = 0;
            sectionIndex < headers->FileHeader.NumberOfSections &&
                name == nullptr; sectionIndex++)
        {
            auto start = base + sections[sectionIndex].VirtualAddress;
            const std::size_t size = sections[sectionIndex].Misc.VirtualSize;
            if (!IsReadable(start, size) || nameLength > size)
            {
                continue;
            }
            for (std::size_t offset = 0; offset <= size - nameLength; offset++)
            {
                if (std::memcmp(start + offset, decoratedClassName,
                        nameLength) == 0)
                {
                    name = start + offset;
                    break;
                }
            }
        }
        if (name == nullptr || name < base + 16)
        {
            return 0;
        }

        const std::uint32_t typeDescriptorRva =
            static_cast<std::uint32_t>((name - 16) - base);
        unsigned char* locator = nullptr;
        for (unsigned sectionIndex = 0;
            sectionIndex < headers->FileHeader.NumberOfSections &&
                locator == nullptr; sectionIndex++)
        {
            auto start = base + sections[sectionIndex].VirtualAddress;
            const std::size_t size = sections[sectionIndex].Misc.VirtualSize;
            if (!IsReadable(start, size) || size < 24)
            {
                continue;
            }
            for (std::size_t offset = 0; offset <= size - 24; offset += 4)
            {
                auto candidate = reinterpret_cast<const std::uint32_t*>(
                    start + offset);
                const std::uint32_t candidateRva =
                    static_cast<std::uint32_t>(start + offset - base);
                if (candidate[0] == 1 &&
                    candidate[3] == typeDescriptorRva &&
                    candidate[5] == candidateRva)
                {
                    locator = start + offset;
                    break;
                }
            }
        }
        if (locator == nullptr)
        {
            return 0;
        }

        const void* locatorAddress = locator;
        for (unsigned sectionIndex = 0;
            sectionIndex < headers->FileHeader.NumberOfSections;
            sectionIndex++)
        {
            auto start = base + sections[sectionIndex].VirtualAddress;
            const std::size_t size = sections[sectionIndex].Misc.VirtualSize;
            if (!IsReadable(start, size) || size < sizeof(void*) * 2)
            {
                continue;
            }
            for (std::size_t offset = 0;
                offset <= size - sizeof(void*) * 2; offset += sizeof(void*))
            {
                auto candidate = reinterpret_cast<void**>(start + offset);
                if (candidate[0] == locatorAddress &&
                    IsExecutableAddress(
                        reinterpret_cast<unsigned char*>(candidate[1])))
                {
                    return static_cast<std::uintptr_t>(
                        start + offset + sizeof(void*) - base);
                }
            }
        }
        return 0;
    }

    std::uintptr_t RvaFromAddress(const void* address)
    {
        return static_cast<std::uintptr_t>(
            reinterpret_cast<const unsigned char*>(address) - ImageBase());
    }

    unsigned char* DirectCallTarget(const unsigned char* instruction)
    {
        if (!IsReadable(instruction, 5) || instruction[0] != 0xE8)
        {
            return nullptr;
        }
        const auto displacement =
            *reinterpret_cast<const std::int32_t*>(instruction + 1);
        auto target = const_cast<unsigned char*>(
            instruction + 5 + displacement);
        return IsExecutableAddress(target) ? target : nullptr;
    }

    bool HasAnimationRenderCall(const unsigned char* wrapper)
    {
        if (!IsReadable(wrapper, 96))
        {
            return false;
        }

        for (std::size_t callOffset = 0; callOffset + 5 <= 96; callOffset++)
        {
            const auto target = DirectCallTarget(wrapper + callOffset);
            if (target == nullptr || !IsReadable(target, 512))
            {
                continue;
            }
            for (std::size_t offset = 0; offset + 12 <= 480; offset++)
            {
                const auto candidate = target + offset;
                if (candidate[0] == 0x44 && candidate[1] == 0x8B &&
                    candidate[2] == 0x4E &&
                    candidate[4] == 0x44 && candidate[5] == 0x8B &&
                    candidate[6] == 0x46 &&
                    candidate[8] == 0x48 && candidate[9] == 0x8B &&
                    candidate[10] == 0x56)
                {
                    return true;
                }
            }
        }
        return false;
    }

    struct AnimationCallPath
    {
        unsigned char* Wrapper = nullptr;
        unsigned char* MemberFunction = nullptr;
        const unsigned char* Call = nullptr;
    };

    bool FindDirectAnimationCall(unsigned char* function,
        std::size_t scanLength, AnimationCallPath& path)
    {
        if (!IsReadable(function, scanLength))
        {
            return false;
        }
        for (std::size_t offset = 0; offset + 5 <= scanLength; offset++)
        {
            auto target = DirectCallTarget(function + offset);
            if (target != nullptr && HasAnimationRenderCall(target))
            {
                path.Wrapper = target;
                path.MemberFunction = function;
                path.Call = function + offset;
                return true;
            }
        }
        return false;
    }

    bool FindAnimationCallPath(unsigned char* advance,
        AnimationCallPath& path)
    {
        if (FindDirectAnimationCall(advance, 1024, path))
        {
            return true;
        }
        if (!IsReadable(advance, 1024))
        {
            return false;
        }
        for (std::size_t offset = 0; offset + 5 <= 1024; offset++)
        {
            auto helper = DirectCallTarget(advance + offset);
            if (helper != nullptr &&
                FindDirectAnimationCall(helper, 384, path))
            {
                return true;
            }
        }
        return false;
    }

    bool DecodeMemberMove(const unsigned char* instruction,
        unsigned char opcode, bool qword, unsigned& baseRegister,
        unsigned& valueRegister, std::size_t& field)
    {
        if (!IsReadable(instruction, 7))
        {
            return false;
        }
        std::size_t offset = 0;
        unsigned char rex = 0;
        if (instruction[offset] >= 0x40 && instruction[offset] <= 0x4F)
        {
            rex = instruction[offset++];
        }
        if (instruction[offset++] != opcode || ((rex & 0x08) != 0) != qword)
        {
            return false;
        }
        const unsigned char modrm = instruction[offset++];
        const unsigned mode = modrm >> 6;
        const unsigned base = modrm & 7;
        if ((mode != 1 && mode != 2) || base == 4)
        {
            return false;
        }
        baseRegister = base | ((rex & 0x01) != 0 ? 8 : 0);
        valueRegister = ((modrm >> 3) & 7) |
            ((rex & 0x04) != 0 ? 8 : 0);
        if (mode == 1)
        {
            const auto displacement =
                static_cast<std::int8_t>(instruction[offset]);
            if (displacement <= 0)
            {
                return false;
            }
            field = static_cast<std::size_t>(displacement);
        }
        else
        {
            field = *reinterpret_cast<const std::uint32_t*>(
                instruction + offset);
        }
        return field > 0;
    }

    bool ResolveMovieMemberLayout(const AnimationCallPath& path,
        std::size_t& animationOffset, std::size_t& currentFrameOffset)
    {
        if (path.MemberFunction == nullptr || path.Call == nullptr ||
            path.Call < path.MemberFunction ||
            !IsReadable(path.MemberFunction,
                static_cast<std::size_t>(path.Call - path.MemberFunction) + 101))
        {
            return false;
        }

        unsigned movieRegister = 0;
        animationOffset = 0;
        const std::size_t callOffset =
            static_cast<std::size_t>(path.Call - path.MemberFunction);
        const std::size_t animationStart = callOffset > 32
            ? callOffset - 32
            : 0;
        for (std::size_t offset = animationStart;
            offset < callOffset; offset++)
        {
            unsigned base = 0;
            unsigned value = 0;
            std::size_t field = 0;
            if (DecodeMemberMove(path.MemberFunction + offset,
                    0x8B, true, base, value, field) &&
                value == 1 && field > MovieStateOffset &&
                field < MovieMutexOffset && (field & 7) == 0)
            {
                movieRegister = base;
                animationOffset = field;
            }
        }
        if (animationOffset == 0)
        {
            return false;
        }

        currentFrameOffset = 0;
        for (std::size_t loadOffset = 0;
            loadOffset < callOffset; loadOffset++)
        {
            unsigned loadBase = 0;
            unsigned loadValue = 0;
            std::size_t loadField = 0;
            if (!DecodeMemberMove(path.MemberFunction + loadOffset,
                    0x8B, false, loadBase, loadValue, loadField) ||
                loadBase != movieRegister ||
                loadField <= animationOffset ||
                loadField >= MovieMutexOffset || (loadField & 3) != 0)
            {
                continue;
            }

            for (std::size_t storeOffset = callOffset + 5;
                storeOffset < callOffset + 101; storeOffset++)
            {
                unsigned storeBase = 0;
                unsigned storeValue = 0;
                std::size_t storeField = 0;
                if (DecodeMemberMove(path.MemberFunction + storeOffset,
                        0x89, false, storeBase, storeValue, storeField) &&
                    storeBase == loadBase && storeValue == loadValue &&
                    storeField == loadField)
                {
                    if (currentFrameOffset != 0 &&
                        currentFrameOffset != loadField)
                    {
                        return false;
                    }
                    currentFrameOffset = loadField;
                }
            }
        }
        return currentFrameOffset > animationOffset;
    }

    bool ReadAlphaStateLayout(const unsigned char* function,
        std::size_t& scratch1, std::size_t& scratch2,
        std::size_t& pointer1, std::size_t& pointer2,
        std::size_t& generation)
    {
        constexpr std::size_t ScanLength = 384;
        if (!IsReadable(function, ScanLength))
        {
            return false;
        }

        for (std::size_t offset = 0;
            offset + 160 <= ScanLength; offset++)
        {
            const auto load = function + offset;
            if (load[0] != 0x48 || load[1] != 0x8B || load[2] != 0x89)
            {
                continue;
            }
            const std::size_t candidatePointer1 =
                *reinterpret_cast<const std::uint32_t*>(load + 3);
            std::size_t candidateScratch1 = 0;
            const unsigned char* pointer1Store = nullptr;
            for (std::size_t next = offset + 7;
                next + 7 <= offset + 64; next++)
            {
                const auto candidate = function + next;
                if (candidate[0] == 0x48 && candidate[1] == 0x8D &&
                    candidate[2] == 0x4E)
                {
                    candidateScratch1 = candidate[3];
                }
                else if (candidate[0] == 0x48 && candidate[1] == 0x8D &&
                    candidate[2] == 0x8E)
                {
                    candidateScratch1 =
                        *reinterpret_cast<const std::uint32_t*>(candidate + 3);
                }
                if (candidate[0] == 0x48 && candidate[1] == 0x89 &&
                    candidate[2] == 0x8E &&
                    *reinterpret_cast<const std::uint32_t*>(candidate + 3) ==
                        candidatePointer1)
                {
                    pointer1Store = candidate;
                    break;
                }
            }
            if (candidateScratch1 == 0 || pointer1Store == nullptr)
            {
                continue;
            }

            for (std::size_t next = static_cast<std::size_t>(
                    pointer1Store + 7 - function);
                next + 14 <= offset + 128; next++)
            {
                const auto candidate = function + next;
                if (candidate[0] != 0x48 || candidate[1] != 0x8D ||
                    candidate[2] != 0x86)
                {
                    continue;
                }
                const std::size_t candidateScratch2 =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 3);
                const auto store = candidate + 7;
                if (store[0] != 0x48 || store[1] != 0x89 ||
                    store[2] != 0x86)
                {
                    continue;
                }
                const std::size_t candidatePointer2 =
                    *reinterpret_cast<const std::uint32_t*>(store + 3);

                for (std::size_t state = next + 14;
                    state + 6 <= offset + 160; state++)
                {
                    const auto stateStore = function + state;
                    if (stateStore[0] == 0x89 && stateStore[1] == 0x96)
                    {
                        const std::size_t candidateGeneration =
                            *reinterpret_cast<const std::uint32_t*>(
                                stateStore + 2);
                        if (candidateScratch1 < candidateScratch2 &&
                            candidateScratch2 < candidatePointer1 &&
                            candidatePointer1 < candidatePointer2 &&
                            candidateScratch2 - candidateScratch1 ==
                                candidatePointer1 - candidateScratch2)
                        {
                            scratch1 = candidateScratch1;
                            scratch2 = candidateScratch2;
                            pointer1 = candidatePointer1;
                            pointer2 = candidatePointer2;
                            generation = candidateGeneration;
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    bool ResolveFramesPerSecondOffset()
    {
        const auto headers = ImageHeaders();
        auto base = ImageBase();
        if (headers == nullptr || base == nullptr)
        {
            return false;
        }

        std::size_t result = 0;
        const auto sections = IMAGE_FIRST_SECTION(headers);
        for (unsigned sectionIndex = 0;
            sectionIndex < headers->FileHeader.NumberOfSections;
            sectionIndex++)
        {
            if ((sections[sectionIndex].Characteristics &
                    IMAGE_SCN_MEM_EXECUTE) == 0)
            {
                continue;
            }
            auto start = base + sections[sectionIndex].VirtualAddress;
            const std::size_t size = sections[sectionIndex].Misc.VirtualSize;
            if (!IsReadable(start, size))
            {
                continue;
            }

            for (std::size_t offset = 0; offset + 15 <= size; offset++)
            {
                const auto candidate = start + offset;
                if (candidate[0] != 0x48 || candidate[1] != 0x8B ||
                    (candidate[2] & 0xC0) != 0x80 ||
                    *reinterpret_cast<const std::uint32_t*>(candidate + 3) !=
                        AnimationInfoOffset)
                {
                    continue;
                }

                const unsigned sourceRegister =
                    (candidate[2] >> 3) & 7;
                std::size_t next = 7;
                if ((candidate[next] & 0xF0) == 0x40)
                {
                    next++;
                }
                if (candidate[next] != 0x8B ||
                    (candidate[next + 1] & 0xC0) != 0x80 ||
                    static_cast<unsigned>(candidate[next + 1] & 7) !=
                        sourceRegister)
                {
                    continue;
                }
                const std::size_t field =
                    *reinterpret_cast<const std::uint32_t*>(
                        candidate + next + 2);
                if (field != AnimationTotalFramesOffset + sizeof(int))
                {
                    continue;
                }
                if (result != 0 && result != field)
                {
                    return false;
                }
                result = field;
            }
        }
        AnimationFramesPerSecondOffset = result;
        return result > 0;
    }

    bool ResolveSsvFileLayout()
    {
        SsvFileVtableRva = FindVtableRva(".?AVCSsvFile@@");
        if (!IsRvaInImage(SsvFileVtableRva, sizeof(void*) * 13))
        {
            return false;
        }

        int counts[256] = {};
        auto vtable = reinterpret_cast<unsigned char**>(
            ImageBase() + SsvFileVtableRva);
        for (int slot = 0; slot < 13; slot++)
        {
            const auto function = vtable[slot];
            if (!IsExecutableAddress(function) ||
                !IsReadable(function, 8) ||
                function[0] != 0x48 || function[1] != 0x8B ||
                (function[2] != 0x41 && function[2] != 0x49))
            {
                continue;
            }
            const unsigned field = function[3];
            if (field > 0 && (field & 7) == 0)
            {
                counts[field]++;
            }
        }

        int bestCount = 0;
        std::size_t bestField = 0;
        for (std::size_t field = 8; field < 256; field += 8)
        {
            if (counts[field] > bestCount)
            {
                bestCount = counts[field];
                bestField = field;
            }
        }
        SsvVideoDecoderOffset = bestCount >= 3 ? bestField : 0;
        return SsvVideoDecoderOffset > 0;
    }

    bool ResolveAnimationLayout()
    {
        const auto wrapper = ImageBase() + AnimationFrameRva;
        if (!IsReadable(wrapper, 96))
        {
            return false;
        }

        const unsigned char* prepare = nullptr;
        const unsigned char* render = nullptr;
        for (std::size_t callOffset = 0;
            callOffset + 5 <= 96; callOffset++)
        {
            const auto target = DirectCallTarget(wrapper + callOffset);
            if (target == nullptr || !IsReadable(target, 512))
            {
                continue;
            }
            for (std::size_t offset = 0; offset + 12 <= 480; offset++)
            {
                const auto candidate = target + offset;
                if (candidate[0] == 0x44 && candidate[1] == 0x8B &&
                    candidate[2] == 0x4E &&
                    candidate[4] == 0x44 && candidate[5] == 0x8B &&
                    candidate[6] == 0x46 &&
                    candidate[8] == 0x48 && candidate[9] == 0x8B &&
                    candidate[10] == 0x56)
                {
                    AnimationAlphaHeightOffset = candidate[3];
                    AnimationAlphaWidthOffset = candidate[7];
                    AnimationAlphaOutputOffset = candidate[11];
                    render = target;
                    break;
                }
            }

            for (std::size_t offset = 0; offset + 80 <= 256; offset++)
            {
                const auto infoLoad = target + offset;
                if (infoLoad[0] != 0x48 || infoLoad[1] != 0x8B ||
                    infoLoad[2] != 0x83)
                {
                    continue;
                }
                const std::size_t info =
                    *reinterpret_cast<const std::uint32_t*>(infoLoad + 3);
                for (std::size_t next = offset + 7;
                    next + 7 <= offset + 48; next++)
                {
                    const auto totalLoad = target + next;
                    if (totalLoad[0] != 0x3B || totalLoad[1] != 0x88)
                    {
                        continue;
                    }
                    const std::size_t total =
                        *reinterpret_cast<const std::uint32_t*>(
                            totalLoad + 2);
                    for (std::size_t ssvSearch = next + 6;
                        ssvSearch + 7 <= next + 48; ssvSearch++)
                    {
                        const auto ssvLoad = target + ssvSearch;
                        if (ssvLoad[0] == 0x48 && ssvLoad[1] == 0x8B &&
                            ssvLoad[2] == 0x8B)
                        {
                            const std::size_t ssv =
                                *reinterpret_cast<const std::uint32_t*>(
                                    ssvLoad + 3);
                            if (info > ssv && total > 0)
                            {
                                AnimationInfoOffset = info;
                                AnimationSsvOffset = ssv;
                                AnimationTotalFramesOffset = total;
                                prepare = target;
                                break;
                            }
                        }
                    }
                    if (prepare != nullptr)
                    {
                        break;
                    }
                }
                if (prepare != nullptr)
                {
                    break;
                }
            }
        }
        if (prepare == nullptr || render == nullptr)
        {
            return false;
        }

        AnimationAlphaFrameOffset = 0;
        for (std::size_t offset = 0; offset + 7 <= 512; offset++)
        {
            const auto store = prepare + offset;
            if (store[0] != 0x89 || store[1] != 0xBB)
            {
                continue;
            }
            const std::size_t field =
                *reinterpret_cast<const std::uint32_t*>(store + 2);
            for (std::size_t next = offset + 6;
                next + 7 <= 512; next++)
            {
                const auto load = prepare + next;
                if (load[0] == 0x8B && load[1] == 0x83 &&
                    *reinterpret_cast<const std::uint32_t*>(load + 2) ==
                        field)
                {
                    AnimationAlphaFrameOffset = field;
                    break;
                }
            }
            if (AnimationAlphaFrameOffset > 0)
            {
                break;
            }
        }

        bool hasAlphaState = false;
        for (std::size_t offset = 0; offset + 5 <= 512; offset++)
        {
            const auto target = DirectCallTarget(prepare + offset);
            std::size_t scratch1 = 0;
            std::size_t scratch2 = 0;
            std::size_t pointer1 = 0;
            std::size_t pointer2 = 0;
            std::size_t generation = 0;
            if (target != nullptr &&
                ReadAlphaStateLayout(target, scratch1, scratch2,
                    pointer1, pointer2, generation))
            {
                AnimationAlphaScratch1Offset = scratch1;
                AnimationAlphaScratch2Offset = scratch2;
                AnimationAlphaScratchPointer1Offset = pointer1;
                AnimationAlphaScratchPointer2Offset = pointer2;
                AnimationAlphaGenerationOffset = generation;
                hasAlphaState = true;
                break;
            }
        }

        // FFmpeg 3.1's public AVStream layout belongs to avformat-57, not
        // vghd. These are the only intentionally ABI-pinned fields.
        StreamIndexEntriesOffset = 0x1C8;
        StreamIndexEntryCountOffset = 0x1D0;

        return hasAlphaState &&
            ResolveFramesPerSecondOffset() &&
            ResolveSsvFileLayout() &&
            AnimationAlphaOutputOffset > 0 &&
            AnimationAlphaWidthOffset > AnimationAlphaOutputOffset &&
            AnimationAlphaHeightOffset > AnimationAlphaWidthOffset &&
            AnimationAlphaScratch1Offset > AnimationAlphaHeightOffset &&
            AnimationAlphaScratch2Offset > AnimationAlphaScratch1Offset &&
            AnimationAlphaScratchPointer1Offset >
                AnimationAlphaScratch2Offset &&
            AnimationAlphaScratchPointer2Offset ==
                AnimationAlphaScratchPointer1Offset + sizeof(void*) &&
            AnimationAlphaGenerationOffset ==
                AnimationAlphaScratchPointer2Offset + sizeof(void*) &&
            AnimationAlphaFrameOffset ==
                AnimationAlphaGenerationOffset + sizeof(void*) &&
            AnimationSsvOffset ==
                AnimationAlphaFrameOffset + sizeof(void*) &&
            AnimationInfoOffset == AnimationSsvOffset + sizeof(void*) &&
            StreamIndexEntryCountOffset > StreamIndexEntriesOffset;
    }

    bool ResolveMovieOffsets()
    {
        LONG mask = 0;
        auto pause = FindUniqueFunction(MoviePauseSignature,
            sizeof(MoviePauseSignature), 0);
        mask |= pause != nullptr ? 1 : 0;
        auto resume = FindUniqueFunction(MovieResumeSignature,
            sizeof(MovieResumeSignature), 1);
        mask |= resume != nullptr ? 2 : 0;
        auto setRate = FindUniqueFunction(MovieSetPlayRateSignature,
            sizeof(MovieSetPlayRateSignature), 2);
        mask |= setRate != nullptr ? 4 : 0;
        MovieVtableRva = FindVtableRva(".?AVMovie@@");
        mask |= MovieVtableRva > 0 ? 256 : 0;
        if (pause == nullptr || resume == nullptr || setRate == nullptr ||
            MovieVtableRva == 0)
        {
            InterlockedExchange(&g_movieResolverMask, mask);
            return false;
        }

        const unsigned char addMutex[] = { 0x48, 0x81, 0xC1 };
        const unsigned char loadState[] = { 0x8B, 0x43 };
        const unsigned char storeRate[] = { 0xF2, 0x0F, 0x11, 0x73 };
        const auto pauseMutex = FindSequence(pause, 48,
            addMutex, sizeof(addMutex));
        const auto pauseState = FindSequence(pause, 96,
            loadState, sizeof(loadState));
        const auto rateStore = FindSequence(setRate, 96,
            storeRate, sizeof(storeRate));
        if (pauseMutex == nullptr || pauseState == nullptr ||
            rateStore == nullptr)
        {
            InterlockedExchange(&g_movieResolverMask, mask);
            return false;
        }
        mask |= 32;

        MoviePauseRva = RvaFromAddress(pause);
        MovieResumeRva = RvaFromAddress(resume);
        MovieSetPlayRateRva = RvaFromAddress(setRate);
        MovieMutexOffset = *reinterpret_cast<const std::uint32_t*>(
            pauseMutex + 3);
        MovieStateOffset = pauseState[2];

        auto movieVtable = reinterpret_cast<void**>(
            ImageBase() + MovieVtableRva);
        auto advance = reinterpret_cast<unsigned char*>(movieVtable[11]);
        AnimationCallPath animationPath = {};
        if (!IsExecutableAddress(advance) ||
            !FindAnimationCallPath(advance, animationPath))
        {
            InterlockedExchange(&g_movieResolverMask, mask);
            return false;
        }
        MovieAdvanceRva = RvaFromAddress(advance);
        AnimationFrameRva = RvaFromAddress(animationPath.Wrapper);
        mask |= 8 | 16;

        if (!ResolveMovieMemberLayout(animationPath,
                MovieAnimationOffset, MovieCurrentFrameOffset))
        {
            InterlockedExchange(&g_movieResolverMask, mask);
            return false;
        }
        mask |= 64;
        mask |= 128;
        const bool resolved =
            MovieAnimationOffset > 0 && MovieCurrentFrameOffset > 0 &&
            MovieMutexOffset > 0 && MovieStateOffset > 0 &&
            rateStore[4] > 0 && MovieVtableRva > 0;
        mask |= resolved ? 512 : 0;
        InterlockedExchange(&g_movieResolverMask, mask);
        return resolved;
    }

    bool ResolveAudioOffsets(void** videoVtable)
    {
        LONG mask = 0;
        BpkSoundVtableRva = FindVtableRva(".?AVCBpkSound@@");
        mask |= IsRvaInImage(BpkSoundVtableRva,
            sizeof(void*) * 4) ? 1 : 0;
        if ((mask & 1) == 0 || videoVtable == nullptr)
        {
            InterlockedExchange(&g_audioResolverMask, mask);
            return false;
        }

        auto soundVtable = reinterpret_cast<void**>(
            ImageBase() + BpkSoundVtableRva);
        auto soundDestroy = reinterpret_cast<unsigned char*>(soundVtable[3]);
        auto videoDestroy = reinterpret_cast<unsigned char*>(videoVtable[3]);
        if (!IsExecutableAddress(soundDestroy) ||
            !IsExecutableAddress(videoDestroy))
        {
            InterlockedExchange(&g_audioResolverMask, mask);
            return false;
        }
        mask |= 2;

        unsigned char* soundClose = nullptr;
        const unsigned char* outputCheck = nullptr;
        std::size_t outputOffset = 0;
        for (std::size_t offset = 0; offset + 5 <= 96; offset++)
        {
            auto target = DirectCallTarget(soundDestroy + offset);
            if (target == nullptr)
            {
                continue;
            }

            for (std::size_t checkOffset = 0;
                checkOffset + 5 <= 96; checkOffset++)
            {
                const auto candidate = target + checkOffset;
                if (candidate[0] == 0x48 && candidate[1] == 0x83 &&
                    candidate[2] == 0x79 && candidate[4] == 0x00)
                {
                    soundClose = target;
                    outputCheck = candidate;
                    outputOffset = candidate[3];
                    break;
                }

                // Newer MSVC builds keep zero in a nonvolatile register and
                // compare the output pointer with that register instead of an
                // immediate zero. Decode the register and require a nearby
                // self-XOR before trusting the member displacement.
                if ((candidate[0] & 0xF8) == 0x48 &&
                    candidate[1] == 0x39)
                {
                    const unsigned char rex = candidate[0];
                    const unsigned char modrm = candidate[2];
                    const unsigned int mode = modrm >> 6;
                    const unsigned int base = (modrm & 7) +
                        ((rex & 1) != 0 ? 8 : 0);
                    const unsigned int comparedRegister =
                        ((modrm >> 3) & 7) +
                        ((rex & 4) != 0 ? 8 : 0);
                    const std::size_t instructionLength =
                        mode == 1 ? 4 : mode == 2 ? 7 : 0;
                    if (base != 1 || instructionLength == 0)
                    {
                        continue;
                    }

                    bool registerCleared = false;
                    const std::size_t searchStart = checkOffset > 32
                        ? checkOffset - 32 : 0;
                    for (std::size_t clearOffset = searchStart;
                        clearOffset + 3 <= checkOffset; clearOffset++)
                    {
                        const auto clear = target + clearOffset;
                        if ((clear[0] & 0xF0) != 0x40 ||
                            (clear[1] != 0x31 && clear[1] != 0x33))
                        {
                            continue;
                        }
                        const unsigned char clearRex = clear[0];
                        const unsigned char clearModrm = clear[2];
                        if ((clearModrm >> 6) != 3)
                        {
                            continue;
                        }
                        const unsigned int left =
                            ((clearModrm >> 3) & 7) +
                            ((clearRex & 4) != 0 ? 8 : 0);
                        const unsigned int right = (clearModrm & 7) +
                            ((clearRex & 1) != 0 ? 8 : 0);
                        if (left == comparedRegister && right == left)
                        {
                            registerCleared = true;
                            break;
                        }
                    }
                    if (!registerCleared)
                    {
                        continue;
                    }

                    outputOffset = mode == 1
                        ? candidate[3]
                        : *reinterpret_cast<const std::uint32_t*>(
                            candidate + 3);
                    if (outputOffset > 0 && outputOffset <= 0x100)
                    {
                        soundClose = target;
                        outputCheck = candidate;
                        break;
                    }
                }
            }
            if (soundClose != nullptr)
            {
                break;
            }
        }
        if (soundClose == nullptr || outputCheck == nullptr)
        {
            InterlockedExchange(&g_audioResolverMask, mask);
            return false;
        }
        BpkSoundCloseRva = RvaFromAddress(soundClose);
        BpkSoundOutputOffset = outputOffset;
        BpkSoundDeviceOffset = BpkSoundOutputOffset + sizeof(void*);
        BpkSoundPendingOffset = BpkSoundDeviceOffset + sizeof(void*);
        mask |= BpkSoundOutputOffset > 0 &&
            BpkSoundPendingOffset <= 0x100 ? 4 : 0;

        VideoSoundOffset = 0;
        for (std::size_t outer = 0; outer + 5 <= 128 &&
            VideoSoundOffset == 0; outer++)
        {
            auto cleanup = DirectCallTarget(videoDestroy + outer);
            if (cleanup == nullptr || !IsReadable(cleanup, 256))
            {
                continue;
            }

            for (std::size_t callOffset = 0;
                callOffset + 5 <= 256; callOffset++)
            {
                if (DirectCallTarget(cleanup + callOffset) != soundClose)
                {
                    continue;
                }

                const std::size_t firstLoad =
                    callOffset > 32 ? callOffset - 32 : 0;
                for (std::size_t loadOffset = firstLoad;
                    loadOffset + 4 <= callOffset; loadOffset++)
                {
                    const auto load = cleanup + loadOffset;
                    if ((load[0] == 0x48 || load[0] == 0x49) &&
                        load[1] == 0x8B &&
                        (load[2] & 0xC0) == 0x40 &&
                        (load[2] & 0x38) == 0x08)
                    {
                        VideoSoundOffset = load[3];
                    }
                }
                if (VideoSoundOffset != 0)
                {
                    break;
                }
            }
        }
        mask |= VideoSoundOffset > 0 &&
            VideoSoundOffset <= 0x200 ? 8 : 0;

        BpkSoundWriteRva = 0;
        AudioWriteCallRva = 0;
        auto decoderGroup = reinterpret_cast<unsigned char*>(videoVtable[13]);
        if (IsExecutableAddress(decoderGroup) &&
            IsReadable(decoderGroup, 0x1000))
        {
            for (std::size_t offset = 0; offset + 9 <= 0x1000; offset++)
            {
                const auto load = decoderGroup + offset;
                if ((load[0] != 0x48 && load[0] != 0x49) ||
                    load[1] != 0x8B ||
                    (load[2] & 0xC0) != 0x40 ||
                    (load[2] & 0x38) != 0x08 ||
                    load[3] != VideoSoundOffset ||
                    load[4] != 0xE8)
                {
                    continue;
                }

                auto write = DirectCallTarget(load + 4);
                if (write == nullptr || !IsReadable(write, 96))
                {
                    continue;
                }
                const unsigned char outputLoad[] = {
                    0x48, 0x8B, 0x49,
                    static_cast<unsigned char>(BpkSoundOutputOffset)
                };
                if (FindSequence(write, 96, outputLoad,
                        sizeof(outputLoad)) == nullptr)
                {
                    continue;
                }

                const std::uintptr_t writeRva = RvaFromAddress(write);
                const std::uintptr_t callRva = RvaFromAddress(load + 4);
                if ((BpkSoundWriteRva != 0 &&
                        BpkSoundWriteRva != writeRva) ||
                    (AudioWriteCallRva != 0 &&
                        AudioWriteCallRva != callRva))
                {
                    InterlockedExchange(&g_audioResolverMask, mask);
                    return false;
                }
                BpkSoundWriteRva = writeRva;
                AudioWriteCallRva = callRva;
            }
        }
        mask |= IsRvaInImage(BpkSoundWriteRva, 96) ? 16 : 0;
        mask |= IsRvaInImage(AudioWriteCallRva, 5) ? 32 : 0;

        BpkSoundRawWriteRva = 0;
        WmvAudioWriteCallRva = 0;
        auto readerVtable = reinterpret_cast<void**>(
            ImageBase() + SsvReaderVtableRva);
        auto onSample = reinterpret_cast<unsigned char*>(readerVtable[4]);
        if (IsExecutableAddress(onSample) && IsReadable(onSample, 512))
        {
            for (std::size_t offset = 14; offset + 5 <= 512; offset++)
            {
                auto call = onSample + offset;
                if (call[0] != 0xE8 ||
                    call[-4] != 0x48 || call[-3] != 0x8B ||
                    call[-2] != 0x48 ||
                    call[-1] != static_cast<unsigned char>(VideoSoundOffset))
                {
                    continue;
                }
                auto write = DirectCallTarget(call);
                if (write == nullptr ||
                    !IsCompatibleBpkSoundRawWrite(call, write))
                {
                    continue;
                }
                BpkSoundRawWriteRva = RvaFromAddress(write);
                WmvAudioWriteCallRva = RvaFromAddress(call);
                break;
            }
        }
        mask |= IsRvaInImage(BpkSoundRawWriteRva,
            sizeof(BpkSoundRawWriteSignature)) ? 64 : 0;
        mask |= IsRvaInImage(WmvAudioWriteCallRva, 5) ? 128 : 0;
        const bool wmvResolved = mask == 0xFF;
        mask |= wmvResolved ? 256 : 0;
        InterlockedExchange(&g_audioResolverMask, mask);
        return (mask & 0x3F) == 0x3F;
    }

    bool DecodeWorkerTargetLoads(const unsigned char* load,
        std::size_t& length, std::size_t& frameOffset,
        std::size_t& mutexOffset)
    {
        if (!IsReadable(load, 14))
        {
            return false;
        }

        std::size_t frameLoadLength = 0;
        if (load[0] == 0x41 && load[1] == 0x8B && load[2] == 0x6E)
        {
            frameOffset = load[3];
            frameLoadLength = 4;
        }
        else if (load[0] == 0x41 && load[1] == 0x8B && load[2] == 0xAE)
        {
            frameOffset = *reinterpret_cast<const std::uint32_t*>(load + 3);
            frameLoadLength = 7;
        }
        else
        {
            return false;
        }

        const auto queueLoad = load + frameLoadLength;
        if (queueLoad[0] == 0x49 && queueLoad[1] == 0x8B &&
            queueLoad[2] == 0x4E)
        {
            mutexOffset = queueLoad[3];
            length = frameLoadLength + 4;
            return true;
        }
        if (queueLoad[0] == 0x49 && queueLoad[1] == 0x8B &&
            queueLoad[2] == 0x8E)
        {
            mutexOffset =
                *reinterpret_cast<const std::uint32_t*>(queueLoad + 3);
            length = frameLoadLength + 7;
            return true;
        }
        return false;
    }

    bool ResolveVideoOffsets()
    {
        VideoFfmpegVtableRva = FindVtableRva(".?AVVideoFFmpeg@@");
        VideoWmvCoreVtableRva = FindVtableRva(".?AVVideoWmvCore@@");
        SsvReaderVtableRva = FindVtableRva(".?AVCSsvReader@@");
        if (!IsRvaInImage(VideoFfmpegVtableRva,
                sizeof(void*) * 15) ||
            !IsRvaInImage(VideoWmvCoreVtableRva,
                sizeof(void*) * 15) ||
            !IsRvaInImage(SsvReaderVtableRva,
                sizeof(void*) * 5))
        {
            return false;
        }

        auto vtable = reinterpret_cast<void**>(
            ImageBase() + VideoFfmpegVtableRva);
        auto worker = reinterpret_cast<unsigned char*>(vtable[11]);
        auto seek = reinterpret_cast<unsigned char*>(vtable[14]);
        if (!IsExecutableAddress(worker) || !IsExecutableAddress(seek))
        {
            return false;
        }
        if (!ResolveAudioOffsets(vtable))
        {
            return false;
        }
        VideoSeekRva = RvaFromAddress(seek);

        const unsigned char* targetLoad = nullptr;
        for (std::size_t offset = 0; offset + 14 <= 512; offset++)
        {
            const auto candidate = worker + offset;
            if (DecodeWorkerTargetLoads(candidate,
                    DecoderWorkerTargetLoadLength,
                    VideoCurrentFrameOffset,
                    VideoFrameQueueMutexOffset))
            {
                targetLoad = candidate;
                break;
            }
        }
        if (targetLoad == nullptr)
        {
            return false;
        }
        DecoderWorkerTargetLoadRva = RvaFromAddress(targetLoad);

        VideoFrameQueueOffset = 0;
        QueueBeginOffset = 0;
        QueueEndOffset = 0;
        const unsigned char queuePrefix[] = { 0x49, 0x8B, 0x4E };
        for (std::size_t offset = DecoderWorkerTargetLoadLength;
            offset + 10 <= 128; offset++)
        {
            if (std::memcmp(targetLoad + offset, queuePrefix,
                    sizeof(queuePrefix)) == 0 &&
                targetLoad[offset + 4] == 0x8B &&
                targetLoad[offset + 5] == 0x41 &&
                targetLoad[offset + 7] == 0x2B &&
                targetLoad[offset + 8] == 0x41)
            {
                VideoFrameQueueOffset = targetLoad[offset + 3];
                QueueEndOffset = targetLoad[offset + 6];
                QueueBeginOffset = targetLoad[offset + 9];
                break;
            }
        }

        QueueEntriesOffset = 0;
        VideoQueueEntryReadyOffset = 0;
        for (std::size_t offset = 0; offset + 5 <= 512; offset++)
        {
            const auto candidate = worker + offset;
            if (candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x44 && candidate[3] == 0xC1)
            {
                const std::size_t field = candidate[4];
                if (QueueEntriesOffset != 0 &&
                    QueueEntriesOffset != field)
                {
                    return false;
                }
                QueueEntriesOffset = field;
            }
            if (candidate[0] == 0xC6 && candidate[1] == 0x40 &&
                candidate[3] == 0x01)
            {
                const std::size_t field = candidate[2];
                if (VideoQueueEntryReadyOffset != 0 &&
                    VideoQueueEntryReadyOffset != field)
                {
                    return false;
                }
                VideoQueueEntryReadyOffset = field;
            }
        }

        const unsigned char seekSuffix[] = {
            0x41, 0xB9, 0x04, 0x00, 0x00, 0x00,
            0x45, 0x33, 0xC0, 0x41, 0x8D, 0x51, 0xFB,
            0x48, 0x8B, 0x4B
        };
        const auto suffix = FindSequence(seek, 512,
            seekSuffix, sizeof(seekSuffix));
        if (suffix == nullptr || suffix < seek + 7 ||
            suffix[-7] != 0x48 || suffix[-6] != 0x8B ||
            suffix[-5] != 0x05)
        {
            return false;
        }
        const auto slotInstruction = suffix - 7;
        const std::int32_t slotDisplacement =
            *reinterpret_cast<const std::int32_t*>(slotInstruction + 3);
        AvSeekFrameSlotRva = RvaFromAddress(
            slotInstruction + 7 + slotDisplacement);
        VideoFormatContextOffset = suffix[16];

        const unsigned char* currentClear = nullptr;
        std::size_t clearedFrameOffset = 0;
        for (std::size_t offset = 0; offset + 10 <= 96; offset++)
        {
            const auto candidate = suffix + offset;
            if (candidate[0] == 0xC7 && candidate[1] == 0x43 &&
                *reinterpret_cast<const std::uint32_t*>(candidate + 3) == 0)
            {
                currentClear = candidate;
                clearedFrameOffset = candidate[2];
                break;
            }
            if (candidate[0] == 0xC7 && candidate[1] == 0x83 &&
                *reinterpret_cast<const std::uint32_t*>(candidate + 6) == 0)
            {
                currentClear = candidate;
                clearedFrameOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 2);
                break;
            }
        }
        if (currentClear == nullptr)
        {
            return false;
        }
        if (VideoCurrentFrameOffset != clearedFrameOffset)
        {
            return false;
        }

        auto wmvVtable = reinterpret_cast<void**>(
            ImageBase() + VideoWmvCoreVtableRva);
        auto wmvDestroy = reinterpret_cast<unsigned char*>(wmvVtable[3]);
        auto wmvGetFrame = reinterpret_cast<unsigned char*>(wmvVtable[13]);
        auto ssvVtable = reinterpret_cast<void**>(
            ImageBase() + SsvReaderVtableRva);
        auto onStatus = reinterpret_cast<unsigned char*>(ssvVtable[3]);
        auto onSample = reinterpret_cast<unsigned char*>(ssvVtable[4]);
        if (!IsExecutableAddress(wmvDestroy) ||
            !IsExecutableAddress(wmvGetFrame) ||
            !IsExecutableAddress(onStatus) ||
            !IsExecutableAddress(onSample))
        {
            return false;
        }

        const unsigned char heldFramePrefix[] = { 0x80, 0x79 };
        const auto heldFrameLoad = FindSequence(wmvGetFrame, 32,
            heldFramePrefix, sizeof(heldFramePrefix));
        if (heldFrameLoad == nullptr || heldFrameLoad[3] != 0)
        {
            return false;
        }
        WmvFrameHeldOffset = heldFrameLoad[2];

        const unsigned char wmvReaderLoadPrefix[] = { 0x48, 0x8B, 0x49 };
        const auto wmvReaderLoad = FindSequence(wmvDestroy, 64,
            wmvReaderLoadPrefix, sizeof(wmvReaderLoadPrefix));
        if (wmvReaderLoad == nullptr)
        {
            return false;
        }
        WmvReaderObjectOffset = wmvReaderLoad[3];

        WmvReaderInterfaceOffset = 0;
        WmvSampleCounterOffset = 0;
        WmvReaderPausedOffset = 0;
        for (std::size_t offset = 0; offset + 10 <= 512; offset++)
        {
            const auto candidate = onSample + offset;
            if (candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x4E && candidate[4] == 0x48 &&
                candidate[5] == 0x8B && candidate[6] == 0x01 &&
                candidate[7] == 0xFF && candidate[8] == 0x50 &&
                candidate[9] == 0x60)
            {
                WmvReaderInterfaceOffset = candidate[3];
            }
            if (candidate[0] == 0x8B && candidate[1] == 0x4E &&
                candidate[3] == 0x89 && candidate[4] == 0x48 &&
                candidate[5] == 0x20)
            {
                WmvSampleCounterOffset = candidate[2];
            }
            if (candidate[0] == 0x80 && candidate[1] == 0x7E &&
                candidate[3] == 0x00)
            {
                WmvReaderPausedOffset = candidate[2];
            }
        }

        WmvQueueMutexOffset = 0;
        WmvColorQueueOffset = 0;
        for (std::size_t offset = 0; offset + 13 <= 512; offset++)
        {
            const auto candidate = onSample + offset;
            if (candidate[0] == 0x48 && candidate[1] == 0x83 &&
                candidate[2] == 0xC1 && candidate[3] >= 0x20 &&
                (candidate[3] & 7) == 0)
            {
                int matchingAdds = 0;
                for (std::size_t other = 0; other + 4 <= 512; other++)
                {
                    const auto otherCandidate = onSample + other;
                    if (otherCandidate[0] == 0x48 &&
                        otherCandidate[1] == 0x83 &&
                        otherCandidate[2] == 0xC1 &&
                        otherCandidate[3] == candidate[3])
                    {
                        matchingAdds++;
                    }
                }
                if (matchingAdds >= 2)
                {
                    WmvQueueMutexOffset = candidate[3];
                }
            }

            // The colour callback checks QListData::end - begin immediately
            // after loading its completed-frame queue.
            if (candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x86 &&
                candidate[7] == 0x8B && candidate[8] == 0x48 &&
                candidate[9] == 0x0C &&
                candidate[10] == 0x2B && candidate[11] == 0x48 &&
                candidate[12] == 0x08)
            {
                WmvColorQueueOffset =
                    *reinterpret_cast<const std::uint32_t*>(candidate + 3);
            }
        }

        WmvAdvancedInterfaceOffset = 0;
        WmvStatusEventOffset = 0;
        WmvLastResultOffset = 0;
        for (std::size_t offset = 0; offset + 6 <= 512; offset++)
        {
            const auto candidate = onStatus + offset;
            if (offset + 12 <= 512 &&
                candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x4B && candidate[4] == 0x48 &&
                candidate[5] == 0x8B && candidate[6] == 0x01 &&
                candidate[7] == 0x33 && candidate[8] == 0xD2 &&
                candidate[9] == 0xFF && candidate[10] == 0x50 &&
                candidate[11] == 0x28)
            {
                WmvAdvancedInterfaceOffset = candidate[3];
            }
            if (candidate[0] == 0x89 && candidate[1] == 0x7B)
            {
                if (WmvLastResultOffset != 0 &&
                    WmvLastResultOffset != candidate[2])
                {
                    return false;
                }
                WmvLastResultOffset = candidate[2];
            }
            if (candidate[0] == 0x48 && candidate[1] == 0x8B &&
                candidate[2] == 0x4B && candidate[4] == 0xFF &&
                candidate[5] == 0x15)
            {
                if (WmvStatusEventOffset != 0 &&
                    WmvStatusEventOffset != candidate[3])
                {
                    return false;
                }
                WmvStatusEventOffset = candidate[3];
            }
        }

        auto clearQueues = FindUniqueFunction(WmvClearQueuesSignature,
            sizeof(WmvClearQueuesSignature), -1);
        WmvClearQueuesRva = clearQueues == nullptr
            ? 0
            : RvaFromAddress(clearQueues);

        auto peekFrame = FindUniqueFunction(WmvPeekFrameSignature,
            sizeof(WmvPeekFrameSignature), 4);
        WmvPeekFrameRva = peekFrame == nullptr
            ? 0
            : RvaFromAddress(peekFrame);

        return VideoFrameQueueOffset > 0 &&
            VideoFrameQueueMutexOffset > 0 &&
            QueueBeginOffset > 0 &&
            QueueEndOffset > QueueBeginOffset &&
            QueueEntriesOffset > QueueEndOffset &&
            VideoQueueEntryReadyOffset > 0 &&
            VideoFormatContextOffset > 0 &&
            IsRvaInImage(AvSeekFrameSlotRva, sizeof(void*)) &&
            WmvReaderObjectOffset > 0 &&
            WmvFrameHeldOffset > WmvReaderObjectOffset &&
            WmvReaderInterfaceOffset > 0 &&
            WmvAdvancedInterfaceOffset > 0 &&
            WmvStatusEventOffset > 0 &&
            WmvLastResultOffset > 0 &&
            WmvSampleCounterOffset > 0 &&
            WmvReaderPausedOffset > 0 &&
            WmvQueueMutexOffset > 0 &&
            WmvColorQueueOffset > 0 &&
            IsRvaInImage(WmvClearQueuesRva,
                sizeof(WmvClearQueuesSignature)) &&
            IsRvaInImage(WmvPeekFrameRva, 64);
    }

    void ImageProfileSection(wchar_t (&section)[64])
    {
        const auto headers = ImageHeaders();
        wchar_t version[64] = {};
        if (headers == nullptr || !ImageVersion(version))
        {
            section[0] = L'\0';
            return;
        }
        swprintf_s(section, L"vghd_%s_%08X_%08X", version,
            headers->FileHeader.TimeDateStamp,
            headers->OptionalHeader.SizeOfImage);
    }

    void WriteResolvedValue(const wchar_t* path, const wchar_t* section,
        const wchar_t* key, std::uintptr_t value)
    {
        wchar_t text[32] = {};
        swprintf_s(text, L"0x%llX",
            static_cast<unsigned long long>(value));
        WritePrivateProfileStringW(section, key, text, path);
    }

    void SaveResolvedOffsets(const wchar_t* profilePath)
    {
        wchar_t section[64] = {};
        ImageProfileSection(section);
        if (section[0] == L'\0')
        {
            return;
        }
        wchar_t version[64] = {};
        if (ImageVersion(version))
        {
            WritePrivateProfileStringW(section,
                L"IStripperVersion", version, profilePath);
        }
        WritePrivateProfileStringW(section,
            L"Resolver", L"dynamic", profilePath);
        WriteResolvedValue(profilePath, section,
            L"OffsetResolverMask",
            static_cast<std::uintptr_t>(InterlockedCompareExchange(
                &g_offsetResolverMask, 0, 0)));
        WriteResolvedValue(profilePath, section,
            L"MovieResolverMask",
            static_cast<std::uintptr_t>(InterlockedCompareExchange(
                &g_movieResolverMask, 0, 0)));
        WriteResolvedValue(profilePath, section,
            L"AudioResolverMask",
            static_cast<std::uintptr_t>(InterlockedCompareExchange(
                &g_audioResolverMask, 0, 0)));
        WriteResolvedValue(profilePath, section,
            L"FastDecodeResolverMask",
            static_cast<std::uintptr_t>(InterlockedCompareExchange(
                &g_fastDecodeResolverMask, 0, 0)));
        WriteResolvedValue(profilePath, section,
            L"MoviePauseRva", MoviePauseRva);
        WriteResolvedValue(profilePath, section,
            L"MovieResumeRva", MovieResumeRva);
        WriteResolvedValue(profilePath, section,
            L"MovieSetPlayRateRva", MovieSetPlayRateRva);
        WriteResolvedValue(profilePath, section,
            L"MovieAdvanceRva", MovieAdvanceRva);
        WriteResolvedValue(profilePath, section,
            L"AnimationFrameRva", AnimationFrameRva);
        WriteResolvedValue(profilePath, section,
            L"MovieVtableRva", MovieVtableRva);
        WriteResolvedValue(profilePath, section,
            L"VideoFfmpegVtableRva", VideoFfmpegVtableRva);
        WriteResolvedValue(profilePath, section,
            L"BpkSoundVtableRva", BpkSoundVtableRva);
        WriteResolvedValue(profilePath, section,
            L"FsClipNodeVtableRva", FsClipNodeVtableRva);
        WriteResolvedValue(profilePath, section,
            L"FsClipNodeNextShowClipRva", FsClipNodeNextShowClipRva);
        WriteResolvedValue(profilePath, section,
            L"PlayableCardVtableRva", PlayableCardVtableRva);
        WriteResolvedValue(profilePath, section,
            L"PlayableCardExactConstructorRva",
            PlayableCardExactConstructorRva);
        WriteResolvedValue(profilePath, section,
            L"CardSequencerInsertNextRva",
            CardSequencerInsertNextRva);
        WriteResolvedValue(profilePath, section,
            L"CardSequencerTakeNextAtRva",
            CardSequencerTakeNextAtRva);
        WriteResolvedValue(profilePath, section,
            L"VghdOperatorNewRva", VghdOperatorNewRva);
        WriteResolvedValue(profilePath, section,
            L"CardSequencerVtableRva", g_cardSequencerVtableRva);
        WriteResolvedValue(profilePath, section,
            L"FsClipNodeSceneOffset", FsClipNodeSceneOffset);
        WriteResolvedValue(profilePath, section,
            L"SceneNodesOffset", SceneNodesOffset);
        WriteResolvedValue(profilePath, section,
            L"FsClipNodePlayableCardOffset",
            FsClipNodePlayableCardOffset);
        WriteResolvedValue(profilePath, section,
            L"PlayableCardTagOffset", PlayableCardTagOffset);
        WriteResolvedValue(profilePath, section,
            L"PlayableCardClipsOffset", PlayableCardClipsOffset);
        WriteResolvedValue(profilePath, section,
            L"PlayableCardSize", PlayableCardSize);
        WriteResolvedValue(profilePath, section,
            L"CardSequencerNextOffset", CardSequencerNextOffset);
        WriteResolvedValue(profilePath, section,
            L"NextCardPlayableCardOffset", NextCardPlayableCardOffset);
        WriteResolvedValue(profilePath, section,
            L"SceneExpectedPendingOffset", SceneExpectedPendingOffset);
        WriteResolvedValue(profilePath, section,
            L"SceneExpectedSelectionOffset", SceneExpectedSelectionOffset);
        WriteResolvedValue(profilePath, section,
            L"SceneExpectedCardOffset", SceneExpectedCardOffset);
        WriteResolvedValue(profilePath, section,
            L"SceneExpectedModeOffset", SceneExpectedModeOffset);
        WriteResolvedValue(profilePath, section,
            L"BpkSoundCloseRva", BpkSoundCloseRva);
        WriteResolvedValue(profilePath, section,
            L"BpkSoundWriteRva", BpkSoundWriteRva);
        WriteResolvedValue(profilePath, section,
            L"AudioWriteCallRva", AudioWriteCallRva);
        WriteResolvedValue(profilePath, section,
            L"BpkSoundRawWriteRva", BpkSoundRawWriteRva);
        WriteResolvedValue(profilePath, section,
            L"WmvAudioWriteCallRva", WmvAudioWriteCallRva);
        WriteResolvedValue(profilePath, section,
            L"VideoWmvCoreVtableRva", VideoWmvCoreVtableRva);
        WriteResolvedValue(profilePath, section,
            L"SsvReaderVtableRva", SsvReaderVtableRva);
        WriteResolvedValue(profilePath, section,
            L"SsvFileVtableRva", SsvFileVtableRva);
        WriteResolvedValue(profilePath, section,
            L"WmvClearQueuesRva", WmvClearQueuesRva);
        WriteResolvedValue(profilePath, section,
            L"WmvPeekFrameRva", WmvPeekFrameRva);
        WriteResolvedValue(profilePath, section,
            L"VideoSeekRva", VideoSeekRva);
        WriteResolvedValue(profilePath, section,
            L"DecoderWorkerTargetLoadRva",
            DecoderWorkerTargetLoadRva);
        WriteResolvedValue(profilePath, section,
            L"AvSeekFrameSlotRva", AvSeekFrameSlotRva);
        WriteResolvedValue(profilePath, section,
            L"MovieStateOffset", MovieStateOffset);
        WriteResolvedValue(profilePath, section,
            L"MovieAnimationOffset", MovieAnimationOffset);
        WriteResolvedValue(profilePath, section,
            L"MovieCurrentFrameOffset", MovieCurrentFrameOffset);
        WriteResolvedValue(profilePath, section,
            L"MovieMutexOffset", MovieMutexOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaOutputOffset", AnimationAlphaOutputOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaWidthOffset", AnimationAlphaWidthOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaHeightOffset", AnimationAlphaHeightOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaScratch1Offset", AnimationAlphaScratch1Offset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaScratch2Offset", AnimationAlphaScratch2Offset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaScratchPointer1Offset",
            AnimationAlphaScratchPointer1Offset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaScratchPointer2Offset",
            AnimationAlphaScratchPointer2Offset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaGenerationOffset",
            AnimationAlphaGenerationOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationAlphaFrameOffset", AnimationAlphaFrameOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationSsvOffset", AnimationSsvOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationInfoOffset", AnimationInfoOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationTotalFramesOffset", AnimationTotalFramesOffset);
        WriteResolvedValue(profilePath, section,
            L"AnimationFramesPerSecondOffset",
            AnimationFramesPerSecondOffset);
        WriteResolvedValue(profilePath, section,
            L"SsvVideoDecoderOffset", SsvVideoDecoderOffset);
        WriteResolvedValue(profilePath, section,
            L"StreamIndexEntriesOffset", StreamIndexEntriesOffset);
        WriteResolvedValue(profilePath, section,
            L"StreamIndexEntryCountOffset", StreamIndexEntryCountOffset);
        WriteResolvedValue(profilePath, section,
            L"FormatContextStreamCountOffset",
            FormatContextStreamCountOffset);
        WriteResolvedValue(profilePath, section,
            L"FormatContextStreamsOffset",
            FormatContextStreamsOffset);
        WriteResolvedValue(profilePath, section,
            L"StreamCodecContextOffset", StreamCodecContextOffset);
        WriteResolvedValue(profilePath, section,
            L"CodecContextMediaTypeOffset",
            CodecContextMediaTypeOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoFormatContextOffset", VideoFormatContextOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoFrameQueueOffset", VideoFrameQueueOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoFrameQueueMutexOffset", VideoFrameQueueMutexOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoCurrentFrameOffset", VideoCurrentFrameOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoSoundOffset", VideoSoundOffset);
        WriteResolvedValue(profilePath, section,
            L"BpkSoundOutputOffset", BpkSoundOutputOffset);
        WriteResolvedValue(profilePath, section,
            L"BpkSoundDeviceOffset", BpkSoundDeviceOffset);
        WriteResolvedValue(profilePath, section,
            L"BpkSoundPendingOffset", BpkSoundPendingOffset);
        WriteResolvedValue(profilePath, section,
            L"QueueBeginOffset", QueueBeginOffset);
        WriteResolvedValue(profilePath, section,
            L"QueueEndOffset", QueueEndOffset);
        WriteResolvedValue(profilePath, section,
            L"QueueEntriesOffset", QueueEntriesOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoQueueEntryReadyOffset",
            VideoQueueEntryReadyOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvReaderObjectOffset", WmvReaderObjectOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvFrameHeldOffset", WmvFrameHeldOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvReaderInterfaceOffset", WmvReaderInterfaceOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvAdvancedInterfaceOffset", WmvAdvancedInterfaceOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvStatusEventOffset", WmvStatusEventOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvLastResultOffset", WmvLastResultOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvSampleCounterOffset", WmvSampleCounterOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvReaderPausedOffset", WmvReaderPausedOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvQueueMutexOffset", WmvQueueMutexOffset);
        WriteResolvedValue(profilePath, section,
            L"WmvColorQueueOffset", WmvColorQueueOffset);
        WriteResolvedValue(profilePath, section,
            L"AvcodecOpenSlotRva", AvcodecOpenSlotRva);
        WriteResolvedValue(profilePath, section,
            L"DecodeScaleSlotRva", DecodeScaleSlotRva);
        WriteResolvedValue(profilePath, section,
            L"DecodeScaleCallRva", DecodeScaleCallRva);
    }

    bool EnsureEngineOffsets()
    {
        const LONG state = InterlockedCompareExchange(
            &g_offsetsResolved, 0, 0);
        if (state != 0)
        {
            return state > 0;
        }

        AcquireSRWLockExclusive(&g_offsetResolveLock);
        if (InterlockedCompareExchange(&g_offsetsResolved, 0, 0) == 0)
        {
            wchar_t profilePath[MAX_PATH] = {};
            LONG mask = 0;
            const bool hasPath = OffsetProfilePath(profilePath);
            mask |= hasPath ? 1 : 0;
            const bool hasMovie = ResolveMovieOffsets();
            mask |= hasMovie ? 4 : 0;
            const bool hasLayout = hasMovie && ResolveAnimationLayout();
            mask |= hasLayout ? 2 : 0;
            const bool hasVideo = hasLayout && ResolveVideoOffsets();
            mask |= hasVideo ? 8 : 0;
            const bool resolved = hasVideo;
            InterlockedExchange(&g_offsetResolverMask, mask);
            if (hasPath)
            {
                SaveResolvedOffsets(profilePath);
            }
            InterlockedExchange(&g_offsetsResolved, resolved ? 1 : -1);
        }
        const bool resolved =
            InterlockedCompareExchange(&g_offsetsResolved, 0, 0) > 0;
        ReleaseSRWLockExclusive(&g_offsetResolveLock);
        return resolved;
    }

    bool ResolveFastDecodeOffsets()
    {
        const auto finish = [](LONG mask, bool resolved)
        {
            InterlockedExchange(&g_fastDecodeResolverMask, mask);
            wchar_t profilePath[MAX_PATH] = {};
            if (OffsetProfilePath(profilePath))
            {
                SaveResolvedOffsets(profilePath);
            }
            return resolved;
        };

        LONG mask = 0;
        if (!EnsureEngineOffsets())
        {
            return finish(mask, false);
        }
        mask |= 1;
        if (IsRvaInImage(AvcodecOpenSlotRva, sizeof(void*)) &&
            IsRvaInImage(DecodeScaleSlotRva, sizeof(void*)) &&
            IsRvaInImage(DecodeScaleCallRva,
                DecodeScaleCallLength))
        {
            return finish(0x3F, true);
        }

        const HMODULE avcodec = GetModuleHandleW(L"avcodec-57.dll");
        const HMODULE swscale = GetModuleHandleW(L"swscale-4.dll");
        void* expectedOpen = avcodec == nullptr
            ? nullptr
            : reinterpret_cast<void*>(
                GetProcAddress(avcodec, "avcodec_open2"));
        void* expectedScale = swscale == nullptr
            ? nullptr
            : reinterpret_cast<void*>(
                GetProcAddress(swscale, "sws_scale"));
        const auto headers = ImageHeaders();
        auto base = ImageBase();
        if (expectedOpen == nullptr || expectedScale == nullptr ||
            headers == nullptr || base == nullptr)
        {
            return finish(mask, false);
        }
        mask |= 2;

        std::uintptr_t openSlot = 0;
        std::uintptr_t scaleSlot = 0;
        std::uintptr_t scaleCall = 0;
        if (!IsRvaInImage(VideoFfmpegVtableRva,
                sizeof(void*) * 15))
        {
            return finish(mask, false);
        }
        auto vtable = reinterpret_cast<unsigned char**>(
            base + VideoFfmpegVtableRva);
        auto scaleFunction = vtable[3];
        auto openFunction = vtable[12];
        if (!IsExecutableAddress(scaleFunction) ||
            !IsExecutableAddress(openFunction))
        {
            return finish(mask, false);
        }
        mask |= 4;

        const auto functionLength = [vtable](
            const unsigned char* function) -> std::size_t
        {
            const unsigned char* end = function + 0x2000;
            for (int index = 0; index < 17; index++)
            {
                auto candidate = vtable[index];
                if (candidate > function && candidate < end &&
                    IsExecutableAddress(candidate))
                {
                    end = candidate;
                }
            }
            return static_cast<std::size_t>(end - function);
        };
        const std::size_t openLength = functionLength(openFunction);
        const std::size_t scaleLength = functionLength(scaleFunction);
        if (!IsReadable(openFunction, openLength) ||
            !IsReadable(scaleFunction, scaleLength))
        {
            return finish(mask, false);
        }

        for (std::size_t offset = 0; offset + 32 < openLength; offset++)
        {
            auto instruction = openFunction + offset;
            if (instruction[0] != 0x48 || instruction[1] != 0x8B ||
                instruction[2] != 0x05)
            {
                continue;
            }
            const std::int32_t displacement =
                *reinterpret_cast<const std::int32_t*>(instruction + 3);
            auto slot = instruction + 7 + displacement;
            if (IsReadable(slot, sizeof(void*)) &&
                *reinterpret_cast<void**>(slot) == expectedOpen &&
                FindSequence(instruction + 7, 24,
                    reinterpret_cast<const unsigned char*>("\xFF\xD0"),
                    2) != nullptr)
            {
                const std::uintptr_t candidate = RvaFromAddress(slot);
                if (openSlot != 0 && openSlot != candidate)
                {
                    return finish(mask, false);
                }
                openSlot = candidate;
            }
        }
        mask |= openSlot != 0 ? 8 : 0;

        for (std::size_t offset = 0;
            offset + DecodeScaleCallLength <= scaleLength; offset++)
        {
            auto instruction = scaleFunction + offset;
            if (instruction[0] == 0xFF && instruction[1] == 0x15)
            {
                const std::int32_t displacement =
                    *reinterpret_cast<const std::int32_t*>(
                        instruction + 2);
                auto slot = instruction + DecodeScaleCallLength +
                    displacement;
                if (IsReadable(slot, sizeof(void*)) &&
                    *reinterpret_cast<void**>(slot) == expectedScale)
                {
                    const std::uintptr_t candidateSlot =
                        RvaFromAddress(slot);
                    const std::uintptr_t candidateCall =
                        RvaFromAddress(instruction);
                    if ((scaleSlot != 0 &&
                            scaleSlot != candidateSlot) ||
                        (scaleCall != 0 &&
                            scaleCall != candidateCall))
                    {
                        return finish(mask, false);
                    }
                    scaleSlot = candidateSlot;
                    scaleCall = candidateCall;
                }
            }
        }
        mask |= scaleSlot != 0 && scaleCall != 0 ? 16 : 0;
        if (openSlot == 0 || scaleSlot == 0 || scaleCall == 0)
        {
            return finish(mask, false);
        }
        AvcodecOpenSlotRva = openSlot;
        DecodeScaleSlotRva = scaleSlot;
        DecodeScaleCallRva = scaleCall;

        mask |= 32;
        return finish(mask, true);
    }

    bool HasSignature(std::uintptr_t rva, const unsigned char* signature, std::size_t length)
    {
        const auto base = ImageBase();
        return base != nullptr && IsRvaInImage(rva, length) &&
            std::memcmp(base + rva, signature, length) == 0;
    }

    bool IsIndirectCallToSlot(std::uintptr_t callRva,
        std::uintptr_t slotRva)
    {
        if (!IsRvaInImage(callRva, DecodeScaleCallLength) ||
            !IsRvaInImage(slotRva, sizeof(void*)))
        {
            return false;
        }
        const auto call = ImageBase() + callRva;
        if (call[0] != 0xFF || call[1] != 0x15)
        {
            return false;
        }
        const auto displacement =
            *reinterpret_cast<const std::int32_t*>(call + 2);
        return call + DecodeScaleCallLength + displacement ==
            ImageBase() + slotRva;
    }

    bool IsDecoderWorkerTargetLoad()
    {
        if (!IsRvaInImage(DecoderWorkerTargetLoadRva,
                DecoderWorkerTargetLoadLength) ||
            DecoderWorkerTargetLoadLength < 8)
        {
            return false;
        }
        const auto load = ImageBase() + DecoderWorkerTargetLoadRva;
        std::size_t length = 0;
        std::size_t frameOffset = 0;
        std::size_t mutexOffset = 0;
        return DecodeWorkerTargetLoads(load, length, frameOffset,
                mutexOffset) &&
            length == DecoderWorkerTargetLoadLength &&
            frameOffset == VideoCurrentFrameOffset &&
            mutexOffset == VideoFrameQueueMutexOffset;
    }

    int CompatibilityMask()
    {
        if (!EnsureEngineOffsets() &&
            (InterlockedCompareExchange(&g_offsetResolverMask, 0, 0) & 0x7) != 0x7)
        {
            return 0;
        }
        int mask = 0;
        mask |= HasSignature(MoviePauseRva, MoviePauseSignature,
            sizeof(MoviePauseSignature)) ? 1 : 0;
        mask |= HasSignature(MovieResumeRva, MovieResumeSignature,
            sizeof(MovieResumeSignature)) ? 2 : 0;
        mask |= HasSignature(MovieSetPlayRateRva, MovieSetPlayRateSignature,
            sizeof(MovieSetPlayRateSignature)) ? 4 : 0;
        mask |= IsRvaInImage(MovieVtableRva, sizeof(void*)) ? 8 : 0;
        mask |= IsRvaInImage(VideoFfmpegVtableRva, sizeof(void*)) ? 16 : 0;
        AnimationCallPath path = {};
        std::size_t animationOffset = 0;
        std::size_t currentFrameOffset = 0;
        mask |= IsRvaInImage(MovieAdvanceRva, 1024) &&
            FindAnimationCallPath(ImageBase() + MovieAdvanceRva, path) &&
            RvaFromAddress(path.Wrapper) == AnimationFrameRva &&
            ResolveMovieMemberLayout(path,
                animationOffset, currentFrameOffset) &&
            animationOffset == MovieAnimationOffset &&
            currentFrameOffset == MovieCurrentFrameOffset ? 32 : 0;
        return mask;
    }

    bool HasCompatibleEngine()
    {
        LONG cached = InterlockedCompareExchange(&g_compatibilityMask, -1, -1);
        if (cached < 0)
        {
            const LONG detected = CompatibilityMask();
            InterlockedCompareExchange(&g_compatibilityMask, detected, -1);
            cached = InterlockedCompareExchange(&g_compatibilityMask, -1, -1);
        }
        return cached == 0x3F;
    }

    bool HasFastForwardEngine()
    {
        if (!EnsureEngineOffsets())
        {
            return false;
        }
        LONG cached = InterlockedCompareExchange(
            &g_fastForwardCompatibility, -1, -1);
        if (cached < 0)
        {
            AnimationCallPath path = {};
            const LONG detected =
                IsRvaInImage(MovieAdvanceRva, 1024) &&
                FindAnimationCallPath(
                    ImageBase() + MovieAdvanceRva, path) &&
                RvaFromAddress(path.Wrapper) == AnimationFrameRva;
            InterlockedCompareExchange(
                &g_fastForwardCompatibility, detected, -1);
            cached = InterlockedCompareExchange(
                &g_fastForwardCompatibility, -1, -1);
        }
        return cached != 0;
    }

    bool HasFastDecodeEngine()
    {
        return ResolveFastDecodeOffsets() &&
            IsRvaInImage(AvcodecOpenSlotRva, sizeof(void*)) &&
            IsRvaInImage(AvSeekFrameSlotRva, sizeof(void*)) &&
            IsRvaInImage(DecodeScaleSlotRva, sizeof(void*)) &&
            (InterlockedCompareExchangePointer(&g_decodeScaleThunk, nullptr, nullptr) != nullptr ||
                IsIndirectCallToSlot(
                    DecodeScaleCallRva, DecodeScaleSlotRva)) &&
            (InterlockedCompareExchangePointer(&g_decoderWorkerTargetThunk,
                    nullptr, nullptr) != nullptr ||
                IsDecoderWorkerTargetLoad());
    }

    bool IsMovie(void* movie)
    {
        if (!IsReadable(movie, MovieMutexOffset + sizeof(void*)))
        {
            return false;
        }

        return *reinterpret_cast<void**>(movie) ==
            ImageBase() + MovieVtableRva;
    }

    void* ActiveMovie()
    {
        void* movie = InterlockedCompareExchangePointer(
            &g_activeMovie, nullptr, nullptr);
        return IsMovie(movie) ? movie : nullptr;
    }

    void StoreActiveMovie(void* movie)
    {
        InterlockedExchangePointer(&g_activeMovie, movie);
        InterlockedExchangePointer(&g_activeAnimation,
            *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(movie) +
                    MovieAnimationOffset));
    }

    void MarkMovieConsumed(void* movie)
    {
        void* animation = InterlockedCompareExchangePointer(
            &g_activeAnimation, nullptr, nullptr);
        InterlockedExchangePointer(&g_consumedMovie, movie);
        InterlockedExchangePointer(&g_consumedAnimation, animation);
        InterlockedExchangePointer(&g_consumedSsv,
            IsReadable(animation, AnimationSsvOffset + sizeof(void*))
                ? *reinterpret_cast<void**>(
                    reinterpret_cast<unsigned char*>(animation) +
                        AnimationSsvOffset)
                : nullptr);
        InterlockedExchangePointer(&g_consumedInfo,
            IsReadable(animation, AnimationInfoOffset + sizeof(void*))
                ? *reinterpret_cast<void**>(
                    reinterpret_cast<unsigned char*>(animation) +
                        AnimationInfoOffset)
                : nullptr);
    }

    void __fastcall CapturingMovieAdvance(void* movie)
    {
        if (IsMovie(movie) &&
            InterlockedCompareExchange(&g_movieCaptureArmed, 0, 0) != 0)
        {
            auto movieBytes = reinterpret_cast<unsigned char*>(movie);
            void* animation = *reinterpret_cast<void**>(
                movieBytes + MovieAnimationOffset);
            void* ssv = IsReadable(
                animation, AnimationSsvOffset + sizeof(void*))
                ? *reinterpret_cast<void**>(
                    reinterpret_cast<unsigned char*>(animation) +
                        AnimationSsvOffset)
                : nullptr;
            void* info = IsReadable(
                animation, AnimationInfoOffset + sizeof(void*))
                ? *reinterpret_cast<void**>(
                    reinterpret_cast<unsigned char*>(animation) +
                        AnimationInfoOffset)
                : nullptr;
            const int currentFrame = *reinterpret_cast<const int*>(
                movieBytes + MovieCurrentFrameOffset);
            if (movie != InterlockedCompareExchangePointer(
                    &g_consumedMovie, nullptr, nullptr) ||
                animation != InterlockedCompareExchangePointer(
                    &g_consumedAnimation, nullptr, nullptr) ||
                ssv != InterlockedCompareExchangePointer(
                    &g_consumedSsv, nullptr, nullptr) ||
                info != InterlockedCompareExchangePointer(
                    &g_consumedInfo, nullptr, nullptr) ||
                currentFrame < InterlockedCompareExchange(
                    &g_movieCaptureArmedFrame, -1, -1))
            {
                StoreActiveMovie(movie);
                InterlockedExchange(&g_movieCaptureReady, 1);
            }
        }

        auto original = reinterpret_cast<ManagerAction>(
            InterlockedCompareExchangePointer(
                &g_originalMovieAdvance, nullptr, nullptr));
        if (original != nullptr)
        {
            original(movie);
        }
    }

    bool TryLockMovieMutex(void* mutex, MutexTryLock tryLockMutex,
        int timeoutMilliseconds)
    {
        return tryLockMutex(mutex, timeoutMilliseconds);
    }

    void* AnimationVideoDecoder(void* animation)
    {
        if (!IsReadable(animation, AnimationSsvOffset + sizeof(void*)))
        {
            return nullptr;
        }

        void* ssv = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(animation) + AnimationSsvOffset);
        if (!IsReadable(ssv, SsvVideoDecoderOffset + sizeof(void*)))
        {
            return nullptr;
        }

        void* video = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(ssv) + SsvVideoDecoderOffset);
        if (!IsReadable(video, sizeof(void*)))
        {
            return nullptr;
        }

        return video;
    }

    void* VideoDecoder(void* animation)
    {
        void* video = AnimationVideoDecoder(animation);
        if (video == nullptr ||
            !IsReadable(video, VideoCurrentFrameOffset + sizeof(int)))
        {
            return nullptr;
        }

        void* vtable = *reinterpret_cast<void**>(video);
        return vtable == ImageBase() + VideoFfmpegVtableRva ? video : nullptr;
    }

    bool IsWmvDecoder(void* video)
    {
        return video != nullptr &&
            IsRvaInImage(VideoWmvCoreVtableRva, sizeof(void*)) &&
            *reinterpret_cast<void**>(video) ==
                ImageBase() + VideoWmvCoreVtableRva;
    }

    enum class AudioCommand
    {
        Suspend,
        Resume
    };

    void* VideoAudioSound(void* video)
    {
        if (video == nullptr ||
            !IsReadable(video, VideoSoundOffset + sizeof(void*)))
        {
            return nullptr;
        }

        void* vtable = *reinterpret_cast<void**>(video);
        if (vtable != ImageBase() + VideoFfmpegVtableRva &&
            vtable != ImageBase() + VideoWmvCoreVtableRva)
        {
            return nullptr;
        }

        void* sound = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(video) + VideoSoundOffset);
        if (!IsReadable(sound, BpkSoundOutputOffset + sizeof(void*)) ||
            *reinterpret_cast<void**>(sound) !=
                ImageBase() + BpkSoundVtableRva)
        {
            return nullptr;
        }
        return sound;
    }

    void* MovieAudioSound(void* movie)
    {
        if (movie == nullptr ||
            !IsReadable(movie, MovieAnimationOffset + sizeof(void*)))
        {
            return nullptr;
        }

        void* animation = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(movie) + MovieAnimationOffset);
        return VideoAudioSound(AnimationVideoDecoder(animation));
    }

    void* SoundAudioOutput(void* sound)
    {
        if (!IsReadable(sound, BpkSoundOutputOffset + sizeof(void*)) ||
            *reinterpret_cast<void**>(sound) !=
                ImageBase() + BpkSoundVtableRva)
        {
            return nullptr;
        }
        void* output = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(sound) +
                BpkSoundOutputOffset);
        return IsReadable(output, sizeof(void*)) ? output : nullptr;
    }

    bool ControlSoundAudio(void* sound, AudioCommand command)
    {
        void* output = SoundAudioOutput(sound);
        if (output == nullptr)
        {
            // Clips without an audio stream never open CBpkSound's output.
            return true;
        }

        const HMODULE multimedia = GetModuleHandleW(L"Qt5Multimedia.dll");
        if (multimedia == nullptr)
        {
            return false;
        }

        const char* exportName = nullptr;
        switch (command)
        {
        case AudioCommand::Suspend:
            exportName = "?suspend@QAudioOutput@@QEAAXXZ";
            break;
        case AudioCommand::Resume:
            exportName = "?resume@QAudioOutput@@QEAAXXZ";
            break;
        }

        const auto action = reinterpret_cast<QAudioAction>(
            GetProcAddress(multimedia, exportName));
        if (action == nullptr)
        {
            return false;
        }
        action(output);
        switch (command)
        {
        case AudioCommand::Suspend:
            InterlockedIncrement(&g_audioSuspendCount);
            break;
        case AudioCommand::Resume:
            InterlockedIncrement(&g_audioResumeCount);
            break;
        }
        return true;
    }

    bool ClearSoundPending(void* sound)
    {
        if (!IsReadable(sound, BpkSoundPendingOffset + sizeof(void*)))
        {
            return false;
        }
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto clear = core == nullptr
            ? nullptr
            : reinterpret_cast<QByteArrayAction>(GetProcAddress(
                core, "?clear@QByteArray@@QEAAXXZ"));
        if (clear == nullptr)
        {
            return false;
        }
        clear(reinterpret_cast<unsigned char*>(sound) +
            BpkSoundPendingOffset);
        return true;
    }

    bool RestartSoundAudio(void* sound)
    {
        void* output = SoundAudioOutput(sound);
        if (output == nullptr)
        {
            return true;
        }

        const HMODULE multimedia = GetModuleHandleW(L"Qt5Multimedia.dll");
        if (multimedia == nullptr)
        {
            return false;
        }
        const auto stop = reinterpret_cast<QAudioAction>(GetProcAddress(
            multimedia, "?stop@QAudioOutput@@QEAAXXZ"));
        const auto start = reinterpret_cast<QAudioStart>(GetProcAddress(
            multimedia, "?start@QAudioOutput@@QEAAPEAVQIODevice@@XZ"));
        if (stop == nullptr || start == nullptr ||
            !ClearSoundPending(sound))
        {
            return false;
        }

        stop(output);
        void* device = start(output);
        *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(sound) +
                BpkSoundDeviceOffset) = device;
        if (device == nullptr)
        {
            return false;
        }
        InterlockedIncrement(&g_audioResetCount);
        return true;
    }

    bool IsWmvMovie(void* movie)
    {
        void* animation = IsReadable(movie,
            MovieAnimationOffset + sizeof(void*))
            ? *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(movie) +
                    MovieAnimationOffset)
            : nullptr;
        return IsWmvDecoder(AnimationVideoDecoder(animation));
    }

    bool ControlMovieAudio(void* movie, AudioCommand command)
    {
        // QAudioOutput belongs to a Qt thread, while legacy playback controls
        // run on the injected call/WMF threads. WMV already stops producing
        // PCM when its Movie is paused, so do not touch the Qt device here.
        if (IsWmvMovie(movie))
        {
            return true;
        }
        void* sound = MovieAudioSound(movie);
        return sound == nullptr || ControlSoundAudio(sound, command);
    }

    HRESULT SetMovieVolume(int percent)
    {
        if (percent < 0 || percent > 100)
            return E_INVALIDARG;
        void* movie = ActiveMovie();
        if (movie == nullptr)
            return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        void* sound = MovieAudioSound(movie);
        if (sound == nullptr)
            return BridgeSuccess;
        void* output = SoundAudioOutput(sound);
        if (output == nullptr)
            return BridgeSuccess;

        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const auto invoke = core == nullptr ? nullptr :
            reinterpret_cast<QMetaInvoke>(GetProcAddress(core,
                "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
                "VQGenericArgument@@222222222@Z"));
        if (invoke == nullptr)
            return E_NOINTERFACE;

        const double volume = static_cast<double>(percent) / 100.0;
        const QtGenericArgument argument = { &volume, "qreal" };
        const QtGenericArgument empty = {};
        return invoke(output, "setVolume", argument, empty, empty, empty,
            empty, empty, empty, empty, empty, empty)
            ? BridgeSuccess : E_FAIL;
    }

    bool IsMoviePlaying(void* movie)
    {
        auto state = reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(movie) + MovieStateOffset);
        return IsReadable(state, sizeof(*state)) && *state == PlayingState;
    }

    bool IsMovieAudioHeld(void* movie)
    {
        return InterlockedCompareExchangePointer(
            &g_audioSeekMovie, nullptr, nullptr) == movie;
    }

    bool SuppressMovieAudioWrites(void* movie)
    {
        void* sound = MovieAudioSound(movie);
        if (sound == nullptr)
        {
            return true;
        }
        if (InterlockedCompareExchangePointer(
                &g_audioWriteThunk, nullptr, nullptr) == nullptr)
        {
            return false;
        }

        InterlockedExchangePointer(&g_audioSuppressedSound, sound);
        InterlockedExchange(&g_audioResumeAfterReset, 0);
        InterlockedExchangePointer(
            &g_audioReleaseAfterResetSound, nullptr);
        InterlockedExchangePointer(&g_audioResetPendingSound,
            IsWmvMovie(movie) ? nullptr : sound);
        return true;
    }

    bool WaitForAudioWrites()
    {
        const ULONGLONG deadline = GetTickCount64() + 1'000;
        while (InterlockedCompareExchange(
                &g_audioWritesInFlight, 0, 0) != 0)
        {
            if (GetTickCount64() >= deadline)
            {
                return false;
            }
            Sleep(0);
        }
        return true;
    }

    bool ReleaseMovieAudioWrites(void* movie, bool resume)
    {
        void* sound = MovieAudioSound(movie);
        if (sound == nullptr)
        {
            return true;
        }

        WaitForAudioWrites();
        InterlockedExchange(&g_audioResumeAfterReset, resume ? 1 : 0);
        InterlockedExchangePointer(
            &g_audioReleaseAfterResetSound, sound);
        if (InterlockedCompareExchangePointer(
                &g_audioResetPendingSound, nullptr, nullptr) == sound)
        {
            // The decoder thread will restart the output before its next PCM
            // write, then release this hold itself.
            return false;
        }

        InterlockedCompareExchangePointer(
            &g_audioReleaseAfterResetSound, nullptr, sound);
        InterlockedExchange(&g_audioResumeAfterReset, 0);
        InterlockedCompareExchangePointer(
            &g_audioSuppressedSound, nullptr, sound);
        return true;
    }

    void CancelMovieAudioWrites(void* movie)
    {
        void* sound = MovieAudioSound(movie);
        if (sound == nullptr)
        {
            return;
        }
        InterlockedCompareExchangePointer(
            &g_audioResetPendingSound, nullptr, sound);
        InterlockedCompareExchangePointer(
            &g_audioReleaseAfterResetSound, nullptr, sound);
        InterlockedExchange(&g_audioResumeAfterReset, 0);
        InterlockedCompareExchangePointer(
            &g_audioSuppressedSound, nullptr, sound);
    }

    void HoldAndFlushMovieAudio(void* movie)
    {
        // QAudioOutput::reset invalidates CBpkSound's QIODevice. Restart the
        // output at the next guarded PCM write so the device pointer is
        // refreshed on the decoder thread before writes resume.
        SuppressMovieAudioWrites(movie);
        ControlMovieAudio(movie, AudioCommand::Suspend);
    }

    void PauseMovieAudio(void* movie)
    {
        AcquireSRWLockExclusive(&g_audioControlLock);
        void* sound = MovieAudioSound(movie);
        if (InterlockedCompareExchangePointer(
                &g_audioReleaseAfterResetSound,
                nullptr, nullptr) == sound)
        {
            InterlockedExchange(&g_audioResumeAfterReset, 0);
        }
        ControlMovieAudio(movie, AudioCommand::Suspend);
        ReleaseSRWLockExclusive(&g_audioControlLock);
    }

    void ResumeMovieAudio(void* movie)
    {
        AcquireSRWLockExclusive(&g_audioControlLock);
        if (!IsMovieAudioHeld(movie))
        {
            if (ReleaseMovieAudioWrites(movie, true))
            {
                ControlMovieAudio(movie, AudioCommand::Resume);
            }
        }
        ReleaseSRWLockExclusive(&g_audioControlLock);
    }

    void BeginMovieAudioSeek(void* movie)
    {
        AcquireSRWLockExclusive(&g_audioControlLock);
        InterlockedExchangePointer(&g_audioSeekMovie, movie);
        HoldAndFlushMovieAudio(movie);
        ReleaseSRWLockExclusive(&g_audioControlLock);
    }

    void CancelMovieAudioSeek(void* movie)
    {
        AcquireSRWLockExclusive(&g_audioControlLock);
        InterlockedCompareExchangePointer(
            &g_audioSeekMovie, nullptr, movie);
        if (!IsMovieAudioHeld(movie))
        {
            CancelMovieAudioWrites(movie);
        }
        ReleaseSRWLockExclusive(&g_audioControlLock);
    }

    void CompleteMovieAudioSeek(void* movie)
    {
        AcquireSRWLockExclusive(&g_audioControlLock);
        if (InterlockedCompareExchangePointer(
                &g_audioSeekMovie, nullptr, nullptr) == movie)
        {
            InterlockedExchangePointer(&g_audioSeekMovie, nullptr);
            if (IsMoviePlaying(movie) && !IsMovieAudioHeld(movie))
            {
                if (ReleaseMovieAudioWrites(movie, true))
                {
                    ControlMovieAudio(movie, AudioCommand::Resume);
                }
            }
            else
            {
                if (!IsMovieAudioHeld(movie))
                {
                    ReleaseMovieAudioWrites(movie, false);
                }
                ControlMovieAudio(movie, AudioCommand::Suspend);
            }
        }
        ReleaseSRWLockExclusive(&g_audioControlLock);
    }

    void ResetPlaybackAudioSession()
    {
        AcquireSRWLockExclusive(&g_audioControlLock);
        WaitForAudioWrites();
        InterlockedExchangePointer(&g_audioSeekMovie, nullptr);
        InterlockedExchangePointer(&g_audioSuppressedSound, nullptr);
        InterlockedExchangePointer(&g_audioResetPendingSound, nullptr);
        InterlockedExchangePointer(
            &g_audioReleaseAfterResetSound, nullptr);
        InterlockedExchange(&g_audioResumeAfterReset, 0);
        InterlockedExchange64(&g_audioPlaybackRateBits,
            static_cast<LONGLONG>(0x3FF0000000000000ULL));
        ReleaseSRWLockExclusive(&g_audioControlLock);
    }

    bool IsNormalPlaybackRate(double playRate)
    {
        return playRate > 0.999 && playRate < 1.001;
    }

    void SetMovieAudioRate(void* movie, double playRate)
    {
        LONGLONG rateBits = 0;
        std::memcpy(&rateBits, &playRate, sizeof(playRate));
        if (InterlockedExchange64(
                &g_audioPlaybackRateBits, rateBits) == rateBits)
        {
            return;
        }

        AcquireSRWLockExclusive(&g_audioControlLock);
        if (!IsMovieAudioHeld(movie))
        {
            SuppressMovieAudioWrites(movie);
            if (!IsNormalPlaybackRate(playRate))
            {
                ControlMovieAudio(movie, AudioCommand::Suspend);
            }
            else if (IsWmvMovie(movie) && g_wmvUserClock)
            {
                // The WMF reader must return to its system clock before PCM
                // writes resume at normal speed.
            }
            else if (IsMoviePlaying(movie))
            {
                if (ReleaseMovieAudioWrites(movie, true))
                {
                    ControlMovieAudio(movie, AudioCommand::Resume);
                }
            }
            else
            {
                ReleaseMovieAudioWrites(movie, false);
                ControlMovieAudio(movie, AudioCommand::Suspend);
            }
        }
        ReleaseSRWLockExclusive(&g_audioControlLock);
    }

    bool IsActiveMovieCandidate(void* movie)
    {
        if (!IsMovie(movie))
        {
            return false;
        }

        auto bytes = reinterpret_cast<unsigned char*>(movie);
        const int state = *reinterpret_cast<const int*>(
            bytes + MovieStateOffset);
        const int currentFrame = *reinterpret_cast<const int*>(
            bytes + MovieCurrentFrameOffset);
        void* animation = *reinterpret_cast<void**>(
            bytes + MovieAnimationOffset);
        if ((state != PlayingState && state != PausedState) ||
            currentFrame < 0 ||
            !IsReadable(animation, AnimationInfoOffset + sizeof(void*)))
        {
            return false;
        }

        void* info = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(animation) + AnimationInfoOffset);
        if (!IsReadable(info, AnimationFramesPerSecondOffset + sizeof(int)))
        {
            return false;
        }

        const int totalFrames = *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(info) +
            AnimationTotalFramesOffset);
        const int framesPerSecond = *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(info) +
            AnimationFramesPerSecondOffset);
        return totalFrames > 0 && currentFrame <= totalFrames &&
            framesPerSecond > 0 && framesPerSecond <= 240;
    }

    void* DiscoverActiveMovie()
    {
        void* previousMovie = ActiveMovie();
        void* previousAnimation = InterlockedCompareExchangePointer(
            &g_activeAnimation, nullptr, nullptr);
        if (previousMovie != nullptr &&
            IsActiveMovieCandidate(previousMovie))
        {
            void* animation = *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(previousMovie) +
                    MovieAnimationOffset);
            if (animation != previousAnimation)
            {
                return previousMovie;
            }
        }

        void* reusedMovie = nullptr;
        SYSTEM_INFO systemInfo = {};
        GetSystemInfo(&systemInfo);
        const auto minimum = reinterpret_cast<std::uintptr_t>(
            systemInfo.lpMinimumApplicationAddress);
        const auto maximum = reinterpret_cast<std::uintptr_t>(
            systemInfo.lpMaximumApplicationAddress);
        const void* expectedVtable = ImageBase() + MovieVtableRva;

        for (std::uintptr_t address = minimum; address < maximum;)
        {
            MEMORY_BASIC_INFORMATION memory = {};
            if (VirtualQuery(reinterpret_cast<void*>(address), &memory,
                    sizeof(memory)) != sizeof(memory))
            {
                break;
            }

            const auto regionStart = reinterpret_cast<std::uintptr_t>(
                memory.BaseAddress);
            const auto regionEnd = regionStart + memory.RegionSize;
            if (regionEnd <= address)
            {
                break;
            }

            const DWORD protection = memory.Protect & 0xFF;
            if (memory.State == MEM_COMMIT && memory.Type == MEM_PRIVATE &&
                (protection == PAGE_READWRITE ||
                    protection == PAGE_WRITECOPY ||
                    protection == PAGE_EXECUTE_READWRITE ||
                    protection == PAGE_EXECUTE_WRITECOPY) &&
                (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0)
            {
                const std::uintptr_t first =
                    (regionStart + sizeof(void*) - 1) &
                    ~(sizeof(void*) - 1);
                __try
                {
                    for (std::uintptr_t candidate = first;
                        candidate + sizeof(void*) <= regionEnd;
                        candidate += sizeof(void*))
                    {
                        void* movie = reinterpret_cast<void*>(candidate);
                        if (*reinterpret_cast<void**>(candidate) ==
                                expectedVtable &&
                            IsActiveMovieCandidate(movie))
                        {
                            void* animation = *reinterpret_cast<void**>(
                                reinterpret_cast<unsigned char*>(movie) +
                                    MovieAnimationOffset);
                            if (previousMovie == nullptr ||
                                movie != previousMovie ||
                                animation != previousAnimation)
                            {
                                return movie;
                            }
                            reusedMovie = movie;
                        }
                    }
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    // A heap region can change while it is being inspected.
                }
            }
            address = regionEnd;
        }
        return reusedMovie;
    }

    void* VideoCodecContext(void* formatContext)
    {
        auto formatBytes = reinterpret_cast<unsigned char*>(formatContext);
        if (!IsReadable(formatContext,
                FormatContextStreamsOffset + sizeof(void*)))
        {
            return nullptr;
        }

        const int streamCount = *reinterpret_cast<const int*>(
            formatBytes + FormatContextStreamCountOffset);
        void** streams = *reinterpret_cast<void***>(
            formatBytes + FormatContextStreamsOffset);
        if (streamCount < 1 || streamCount > 64 ||
            !IsReadable(streams,
                static_cast<std::size_t>(streamCount) * sizeof(void*)))
        {
            return nullptr;
        }

        for (int index = 0; index < streamCount; index++)
        {
            void* stream = streams[index];
            if (!IsReadable(stream,
                    StreamCodecContextOffset + sizeof(void*)))
            {
                continue;
            }

            void* codecContext = *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(stream) +
                    StreamCodecContextOffset);
            if (IsReadable(codecContext,
                    CodecContextMediaTypeOffset + sizeof(int)) &&
                *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(codecContext) +
                        CodecContextMediaTypeOffset) == 0)
            {
                return codecContext;
            }
        }
        return nullptr;
    }

    bool FlushVideoCodec(void* formatContext)
    {
        void* codecContext = VideoCodecContext(formatContext);
        const HMODULE avcodec = GetModuleHandleW(L"avcodec-57.dll");
        const auto flush = avcodec == nullptr
            ? nullptr
            : reinterpret_cast<AvcodecFlushBuffers>(
                GetProcAddress(avcodec, "avcodec_flush_buffers"));
        if (codecContext == nullptr || flush == nullptr)
        {
            return false;
        }

        flush(codecContext);
        InterlockedIncrement(&g_codecFlushCount);
        return true;
    }

    struct AvIndexEntry57
    {
        std::int64_t position;
        std::int64_t timestamp;
        std::uint32_t flagsAndSize;
        int minimumDistance;
    };
    static_assert(sizeof(AvIndexEntry57) == 24,
        "Unexpected FFmpeg 3.1 index-entry layout");

    bool FindVideoKeyframe(void* formatContext, int targetFrame, int totalFrames,
        int& streamIndex, int& keyframeFrame, std::int64_t& timestamp)
    {
        const HMODULE avformat = GetModuleHandleW(L"avformat-57.dll");
        const auto version = avformat == nullptr
            ? nullptr
            : reinterpret_cast<AvformatVersion>(
                GetProcAddress(avformat, "avformat_version"));
        if (version == nullptr || version() != ExpectedAvformatVersion ||
            targetFrame < 0 || totalFrames <= 0)
        {
            return false;
        }

        auto formatBytes = reinterpret_cast<unsigned char*>(formatContext);
        if (!IsReadable(formatContext,
                FormatContextStreamsOffset + sizeof(void*)))
        {
            return false;
        }

        const int streamCount = *reinterpret_cast<const int*>(
            formatBytes + FormatContextStreamCountOffset);
        void** streams = *reinterpret_cast<void***>(
            formatBytes + FormatContextStreamsOffset);
        if (streamCount < 1 || streamCount > 64 ||
            !IsReadable(streams,
                static_cast<std::size_t>(streamCount) * sizeof(void*)))
        {
            return false;
        }

        for (int index = 0; index < streamCount; index++)
        {
            auto stream = reinterpret_cast<unsigned char*>(streams[index]);
            if (!IsReadable(stream, StreamIndexEntryCountOffset + sizeof(int)))
            {
                continue;
            }

            void* codecContext = *reinterpret_cast<void**>(
                stream + StreamCodecContextOffset);
            if (!IsReadable(codecContext,
                    CodecContextMediaTypeOffset + sizeof(int)) ||
                *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(codecContext) +
                        CodecContextMediaTypeOffset) != 0)
            {
                continue;
            }

            auto entries = *reinterpret_cast<AvIndexEntry57**>(
                stream + StreamIndexEntriesOffset);
            const int entryCount = *reinterpret_cast<const int*>(
                stream + StreamIndexEntryCountOffset);
            if (entryCount != totalFrames || targetFrame >= entryCount ||
                !IsReadable(entries,
                    static_cast<std::size_t>(entryCount) *
                        sizeof(AvIndexEntry57)) ||
                entries[0].timestamp != 0 ||
                (entries[0].flagsAndSize & 1u) == 0)
            {
                return false;
            }

            for (int frame = targetFrame; frame >= 0; frame--)
            {
                if ((entries[frame].flagsAndSize & 1u) != 0)
                {
                    streamIndex = *reinterpret_cast<const int*>(stream);
                    keyframeFrame = frame;
                    timestamp = entries[frame].timestamp;
                    return streamIndex >= 0;
                }
            }
            return false;
        }
        return false;
    }

    bool SeekVideoToKeyframe(void* video, int streamIndex, int keyframeFrame,
        std::int64_t timestamp)
    {
        const HMODULE qtCore = GetModuleHandleW(L"Qt5Core.dll");
        const auto requestInterruption = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<QThreadRequestInterruption>(GetProcAddress(
                qtCore, "?requestInterruption@QThread@@QEAAXXZ"));
        const auto wait = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<QThreadWait>(GetProcAddress(
                qtCore, "?wait@QThread@@QEAA_NK@Z"));
        const auto start = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<QThreadStart>(GetProcAddress(
                qtCore, "?start@QThread@@QEAAXW4Priority@1@@Z"));
        const auto seek = reinterpret_cast<AvSeekFrame>(
            InterlockedCompareExchangePointer(
                &g_originalAvSeekFrame, nullptr, nullptr));
        auto videoBytes = reinterpret_cast<unsigned char*>(video);
        void* formatContext = *reinterpret_cast<void**>(
            videoBytes + VideoFormatContextOffset);
        auto currentFrame = reinterpret_cast<int*>(
            videoBytes + VideoCurrentFrameOffset);
        if (requestInterruption == nullptr || wait == nullptr || start == nullptr ||
            seek == nullptr || !IsReadable(formatContext, 0x38) ||
            !IsWritable(currentFrame, sizeof(int)))
        {
            return false;
        }

        requestInterruption(video);
        if (!wait(video, INFINITE))
        {
            start(video, 7);
            return false;
        }

        const int result = seek(formatContext, streamIndex, timestamp, 1);
        if (result >= 0)
        {
            *currentFrame = keyframeFrame;
            FlushVideoCodec(formatContext);
            InterlockedExchange(&g_lastKeyframeSeekFrame, keyframeFrame);
            InterlockedIncrement(&g_keyframeSeekCount);
        }
        start(video, 7);
        return result >= 0;
    }

    bool CanResetAnimationAlpha(void* animation)
    {
        auto animationBytes = reinterpret_cast<unsigned char*>(animation);
        if (!IsWritable(animation,
                AnimationAlphaFrameOffset + sizeof(int)))
        {
            return false;
        }

        void* output = *reinterpret_cast<void**>(
            animationBytes + AnimationAlphaOutputOffset);
        const int width = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaWidthOffset);
        const int height = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaHeightOffset);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        const std::size_t outputSize =
            static_cast<std::size_t>(width) * static_cast<std::size_t>(height);
        if (AnimationAlphaScratch2Offset <= AnimationAlphaScratch1Offset ||
            AnimationSsvOffset <= AnimationAlphaScratch1Offset)
        {
            return false;
        }
        const std::size_t scratchSize =
            AnimationAlphaScratch2Offset - AnimationAlphaScratch1Offset;
        void* scratch1 = animationBytes + AnimationAlphaScratch1Offset;
        void* scratch2 = animationBytes + AnimationAlphaScratch2Offset;
        if (outputSize == 0 || outputSize > MaximumAlphaOutputSize ||
            !IsWritable(output, outputSize) ||
            !IsWritable(scratch1, scratchSize) ||
            !IsWritable(scratch2, scratchSize))
        {
            return false;
        }

        return true;
    }

    bool IsPointOverVisibleMoviePixelLocked(
        void* movie, HWND window, POINT point)
    {
        __try
        {
        void* animation = IsReadable(movie,
            MovieAnimationOffset + sizeof(void*))
            ? *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(movie) +
                    MovieAnimationOffset)
            : nullptr;
        if (!CanResetAnimationAlpha(animation))
        {
            return false;
        }

        RECT bounds = {};
        if (FAILED(DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS,
                &bounds, sizeof(bounds))))
        {
            return false;
        }
        const int windowWidth = bounds.right - bounds.left;
        const int windowHeight = bounds.bottom - bounds.top;
        if (windowWidth <= 0 || windowHeight <= 0)
        {
            return false;
        }

        const auto bytes = reinterpret_cast<unsigned char*>(animation);
        const auto alpha = reinterpret_cast<const unsigned char*>(
            *reinterpret_cast<void**>(bytes + AnimationAlphaOutputOffset));
        const int width = *reinterpret_cast<const int*>(
            bytes + AnimationAlphaWidthOffset);
        const int height = *reinterpret_cast<const int*>(
            bytes + AnimationAlphaHeightOffset);
        if (alpha == nullptr || width <= 0 || height <= 0)
        {
            return false;
        }

        const int x = width - 1 - std::clamp(static_cast<int>(
            static_cast<long long>(point.x - bounds.left) * width /
                windowWidth), 0, width - 1);
        const int y = std::clamp(static_cast<int>(
            static_cast<long long>(point.y - bounds.top) * height /
                windowHeight), 0, height - 1);
        // Include a tiny neighbourhood so scrolling remains easy on soft,
        // anti-aliased hair and costume edges without treating the layered
        // window's transparent rectangle as part of the dancer.
        constexpr int radius = 2;
        constexpr unsigned char visibleThreshold = 128;
        for (int row = std::max(0, y - radius);
            row <= std::min(height - 1, y + radius); row++)
        {
            for (int column = std::max(0, x - radius);
                column <= std::min(width - 1, x + radius); column++)
            {
                if (alpha[static_cast<std::size_t>(row) * width + column] >=
                    visibleThreshold)
                {
                    return true;
                }
            }
        }
            return false;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    bool IsPointOverVisibleMoviePixel(HWND window, POINT point)
    {
        void* movie = ActiveMovie();
        const HMODULE qtCore = GetModuleHandleW(L"Qt5Core.dll");
        const auto tryLockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexTryLock>(GetProcAddress(
                qtCore, "?tryLock@QMutex@@QEAA_NH@Z"));
        const auto unlockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexAction>(GetProcAddress(
                qtCore, "?unlock@QMutex@@QEAAXXZ"));
        if (movie == nullptr || tryLockMutex == nullptr ||
            unlockMutex == nullptr)
        {
            return false;
        }

        void* mutex = reinterpret_cast<unsigned char*>(movie) +
            MovieMutexOffset;
        bool mutexLocked = false;
        bool visible = false;
        __try
        {
            // This runs on the low-level mouse hook. Never wait for the
            // decoder: a busy alpha buffer is safer to treat as transparent
            // than to stall all mouse input.
            if (TryLockMovieMutex(mutex, tryLockMutex, 0))
            {
                mutexLocked = true;
                visible = IsPointOverVisibleMoviePixelLocked(
                    movie, window, point);
            }
        }
        __finally
        {
            if (mutexLocked)
            {
                unlockMutex(mutex);
            }
        }
        return visible;
    }

    bool ResetAnimationAlpha(void* animation)
    {
        if (!CanResetAnimationAlpha(animation))
        {
            return false;
        }

        auto animationBytes = reinterpret_cast<unsigned char*>(animation);
        void* output = *reinterpret_cast<void**>(
            animationBytes + AnimationAlphaOutputOffset);
        const int width = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaWidthOffset);
        const int height = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaHeightOffset);
        const std::size_t outputSize =
            static_cast<std::size_t>(width) * static_cast<std::size_t>(height);
        const std::size_t scratchSize =
            AnimationAlphaScratch2Offset - AnimationAlphaScratch1Offset;
        void* scratch1 = animationBytes + AnimationAlphaScratch1Offset;
        void* scratch2 = animationBytes + AnimationAlphaScratch2Offset;
        const int previousAlphaFrame = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaFrameOffset);
        std::memset(output, 0, outputSize);
        std::memset(scratch1, 0, scratchSize);
        std::memset(scratch2, 0, scratchSize);
        *reinterpret_cast<void**>(
            animationBytes + AnimationAlphaScratchPointer1Offset) = nullptr;
        *reinterpret_cast<void**>(
            animationBytes + AnimationAlphaScratchPointer2Offset) = nullptr;
        *reinterpret_cast<int*>(
            animationBytes + AnimationAlphaGenerationOffset) = 0;
        *reinterpret_cast<int*>(
            animationBytes + AnimationAlphaFrameOffset) = 0;
        InterlockedExchange(&g_lastAlphaFrameBeforeReset, previousAlphaFrame);
        InterlockedIncrement(&g_alphaResetCount);
        return true;
    }

    void FreeAlphaCheckpoint(AlphaCheckpoint& checkpoint)
    {
        if (checkpoint.data != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, checkpoint.data);
        }
        checkpoint = {};
    }

    void ClearAlphaCheckpointsLocked()
    {
        for (int index = 0; index < g_alphaCheckpointCount; index++)
        {
            FreeAlphaCheckpoint(g_alphaCheckpoints[index]);
        }
        g_alphaCheckpointCount = 0;
        g_alphaCheckpointBytes = 0;
        g_alphaCheckpointAnimation = nullptr;
        g_alphaCheckpointSsv = nullptr;
        g_alphaCheckpointInfo = nullptr;
        g_alphaCheckpointOutput = nullptr;
        g_alphaCheckpointWidth = 0;
        g_alphaCheckpointHeight = 0;
    }

    void ClearAlphaCheckpoints()
    {
        AcquireSRWLockExclusive(&g_alphaCheckpointLock);
        ClearAlphaCheckpointsLocked();
        ReleaseSRWLockExclusive(&g_alphaCheckpointLock);

        AcquireSRWLockExclusive(&g_seekReadinessLock);
        g_seekReadinessAnimation = nullptr;
        g_seekReadinessSsv = nullptr;
        g_seekReadinessInfo = nullptr;
        g_seekReadinessOutput = nullptr;
        g_seekReadinessMovieFrame = -1;
        g_seekReadinessAlphaFrame = -1;
        g_seekReadinessAlphaGeneration = -1;
        g_seekReadinessAlphaProgress = 0;
        g_seekReadinessWmvReader = nullptr;
        g_seekReadinessWmvFrame = -1;
        g_seekReadinessWmvProgress = 0;
        ReleaseSRWLockExclusive(&g_seekReadinessLock);
    }

    bool AlphaCacheDirectory(wchar_t (&path)[MAX_PATH])
    {
        wchar_t localAppData[MAX_PATH] = {};
        const DWORD length = GetEnvironmentVariableW(
            L"LOCALAPPDATA", localAppData, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
        {
            return false;
        }

        wchar_t applicationDirectory[MAX_PATH] = {};
        return swprintf_s(applicationDirectory,
                L"%s\\IStripperQuickPlayer", localAppData) > 0 &&
            (CreateDirectoryW(applicationDirectory, nullptr) ||
                GetLastError() == ERROR_ALREADY_EXISTS) &&
            swprintf_s(path, L"%s\\alpha-cache",
                applicationDirectory) > 0 &&
            (CreateDirectoryW(path, nullptr) ||
                GetLastError() == ERROR_ALREADY_EXISTS);
    }

    bool AlphaCacheFilePath(std::uint64_t clipKey, int frame,
        wchar_t (&path)[MAX_PATH])
    {
        wchar_t directory[MAX_PATH] = {};
        return clipKey != 0 && frame >= 0 &&
            AlphaCacheDirectory(directory) &&
            swprintf_s(path, L"%s\\%016llX_%08X.qpac", directory,
                static_cast<unsigned long long>(clipKey),
                static_cast<unsigned>(frame)) > 0;
    }

    std::uint64_t PersistentAlphaChecksum(
        const unsigned char* data, std::size_t size)
    {
        std::uint64_t hash = 1469598103934665603ULL;
        for (std::size_t index = 0; index < size; index++)
        {
            hash ^= data[index];
            hash *= 1099511628211ULL;
        }
        return hash;
    }

    bool EncodePersistentAlphaPointer(void* animation, void* output,
        std::size_t outputSize, void* pointer, std::int64_t& encoded)
    {
        encoded = 0;
        if (pointer == nullptr)
        {
            return true;
        }

        const auto address = reinterpret_cast<std::uintptr_t>(pointer);
        const auto animationAddress =
            reinterpret_cast<std::uintptr_t>(animation);
        const auto scratchBegin =
            animationAddress + AnimationAlphaScratch1Offset;
        const auto scratchEnd = animationAddress + AnimationSsvOffset;
        if (address >= scratchBegin && address < scratchEnd)
        {
            encoded = static_cast<std::int64_t>(
                address - animationAddress) + 1;
            return true;
        }

        const auto outputAddress =
            reinterpret_cast<std::uintptr_t>(output);
        if (address >= outputAddress &&
            address - outputAddress < outputSize)
        {
            encoded = -static_cast<std::int64_t>(
                address - outputAddress) - 1;
            return true;
        }
        return false;
    }

    void* DecodePersistentAlphaPointer(void* animation, void* output,
        std::size_t outputSize, std::int64_t encoded)
    {
        if (encoded == 0)
        {
            return nullptr;
        }
        if (encoded > 0)
        {
            const std::uint64_t offset =
                static_cast<std::uint64_t>(encoded - 1);
            return offset >= AnimationAlphaScratch1Offset &&
                offset < AnimationSsvOffset
                ? reinterpret_cast<unsigned char*>(animation) + offset
                : nullptr;
        }

        const std::uint64_t outputOffset =
            static_cast<std::uint64_t>(-(encoded + 1));
        return outputOffset < outputSize
            ? reinterpret_cast<unsigned char*>(output) + outputOffset
            : nullptr;
    }

    bool WriteFileFully(HANDLE file, const void* data, std::size_t size)
    {
        const auto bytes = static_cast<const unsigned char*>(data);
        std::size_t written = 0;
        while (written < size)
        {
            const DWORD chunk = static_cast<DWORD>(
                (size - written) < MAXDWORD
                    ? size - written : MAXDWORD);
            DWORD count = 0;
            if (!WriteFile(file, bytes + written, chunk, &count, nullptr) ||
                count != chunk)
            {
                return false;
            }
            written += count;
        }
        return true;
    }

    bool ReadFileFully(HANDLE file, void* data, std::size_t size)
    {
        auto bytes = static_cast<unsigned char*>(data);
        std::size_t read = 0;
        while (read < size)
        {
            const DWORD chunk = static_cast<DWORD>(
                (size - read) < MAXDWORD ? size - read : MAXDWORD);
            DWORD count = 0;
            if (!ReadFile(file, bytes + read, chunk, &count, nullptr) ||
                count != chunk)
            {
                return false;
            }
            read += count;
        }
        return true;
    }

    struct PersistentAlphaCacheFile
    {
        std::wstring path;
        std::uint64_t size = 0;
        std::uint64_t writeTime = 0;
    };

    void PrunePersistentAlphaCache(const wchar_t* directory)
    {
        const auto cacheLimit = static_cast<std::uint64_t>(
            InterlockedCompareExchange64(
                &g_persistentAlphaCacheLimit, 0, 0));
        wchar_t searchPath[MAX_PATH] = {};
        if (swprintf_s(searchPath, L"%s\\*.qpac", directory) <= 0)
        {
            return;
        }

        WIN32_FIND_DATAW data = {};
        HANDLE search = FindFirstFileW(searchPath, &data);
        if (search == INVALID_HANDLE_VALUE)
        {
            return;
        }

        std::uint64_t totalBytes = 0;
        std::vector<PersistentAlphaCacheFile> files;
        do
        {
            if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                continue;
            }
            wchar_t path[MAX_PATH] = {};
            if (swprintf_s(path, L"%s\\%s", directory,
                    data.cFileName) <= 0)
            {
                continue;
            }
            ULARGE_INTEGER size = {};
            size.HighPart = data.nFileSizeHigh;
            size.LowPart = data.nFileSizeLow;
            ULARGE_INTEGER time = {};
            time.HighPart = data.ftLastWriteTime.dwHighDateTime;
            time.LowPart = data.ftLastWriteTime.dwLowDateTime;
            files.push_back({ path, size.QuadPart, time.QuadPart });
            totalBytes += size.QuadPart;
        } while (FindNextFileW(search, &data));
        FindClose(search);

        if (totalBytes <= cacheLimit)
        {
            return;
        }
        std::sort(files.begin(), files.end(),
            [](const auto& left, const auto& right)
            {
                return left.writeTime < right.writeTime;
            });
        for (const auto& file : files)
        {
            if (totalBytes <= cacheLimit)
            {
                break;
            }
            if (DeleteFileW(file.path.c_str()))
            {
                totalBytes -= file.size;
            }
        }
    }

    void CALLBACK WritePersistentAlphaCheckpoint(
        PTP_CALLBACK_INSTANCE, void* context)
    {
        auto job = static_cast<PersistentAlphaWriteJob*>(context);
        unsigned char* raw = nullptr;
        unsigned char* compressed = nullptr;
        __try
        {
            const std::size_t outputSize =
                static_cast<std::size_t>(job->header.outputSize);
            const std::size_t stateSize =
                static_cast<std::size_t>(job->header.stateSize);
            const std::size_t rawSize = outputSize + stateSize;
            if (rawSize == 0 || rawSize > MaximumAlphaCheckpointBytes ||
                static_cast<std::uint64_t>(
                    InterlockedCompareExchange64(
                        &g_alphaCheckpointClipKey, 0, 0)) !=
                    job->header.clipKey)
            {
                __leave;
            }

            AcquireSRWLockShared(&g_alphaCheckpointLock);
            const AlphaCheckpoint* checkpoint = nullptr;
            if (g_alphaCheckpointAnimation == job->animation &&
                g_alphaCheckpointSsv == job->ssv &&
                g_alphaCheckpointInfo == job->info &&
                g_alphaCheckpointOutput == job->output &&
                g_alphaCheckpointWidth == job->header.width &&
                g_alphaCheckpointHeight == job->header.height)
            {
                for (int index = 0;
                    index < g_alphaCheckpointCount; index++)
                {
                    if (g_alphaCheckpoints[index].frame ==
                        job->header.frame)
                    {
                        checkpoint = &g_alphaCheckpoints[index];
                        break;
                    }
                }
            }
            if (checkpoint != nullptr &&
                checkpoint->outputSize == outputSize)
            {
                raw = static_cast<unsigned char*>(HeapAlloc(
                    GetProcessHeap(), 0, rawSize));
                if (raw != nullptr)
                {
                    std::memcpy(raw, checkpoint->data, rawSize);
                }
            }
            ReleaseSRWLockShared(&g_alphaCheckpointLock);
            if (raw == nullptr)
            {
                __leave;
            }

            const std::size_t pointer1Offset = outputSize +
                AnimationAlphaScratchPointer1Offset -
                AnimationAlphaScratch1Offset;
            const std::size_t pointer2Offset = outputSize +
                AnimationAlphaScratchPointer2Offset -
                AnimationAlphaScratch1Offset;
            if (pointer2Offset + sizeof(void*) > rawSize)
            {
                __leave;
            }
            void* pointer1 = nullptr;
            void* pointer2 = nullptr;
            std::memcpy(&pointer1, raw + pointer1Offset,
                sizeof(pointer1));
            std::memcpy(&pointer2, raw + pointer2Offset,
                sizeof(pointer2));
            if (!EncodePersistentAlphaPointer(job->animation,
                    job->output, outputSize, pointer1,
                    job->header.pointer1) ||
                !EncodePersistentAlphaPointer(job->animation,
                    job->output, outputSize, pointer2,
                    job->header.pointer2))
            {
                __leave;
            }
            std::memset(raw + pointer1Offset, 0, sizeof(void*));
            std::memset(raw + pointer2Offset, 0, sizeof(void*));
            job->header.checksum =
                PersistentAlphaChecksum(raw, rawSize);

            const unsigned char* stored = raw;
            std::size_t storedSize = rawSize;
            COMPRESSOR_HANDLE compressor = nullptr;
            if (CreateCompressor(COMPRESS_ALGORITHM_XPRESS_HUFF,
                    nullptr, &compressor))
            {
                SIZE_T required = 0;
                Compress(compressor, raw, rawSize,
                    nullptr, 0, &required);
                if (required > 0 &&
                    required < MaximumAlphaCheckpointBytes)
                {
                    compressed = static_cast<unsigned char*>(HeapAlloc(
                        GetProcessHeap(), 0, required));
                    SIZE_T actual = 0;
                    if (compressed != nullptr &&
                        Compress(compressor, raw, rawSize,
                            compressed, required, &actual) &&
                        actual < rawSize)
                    {
                        stored = compressed;
                        storedSize = actual;
                        job->header.compression = 1;
                    }
                }
                CloseCompressor(compressor);
            }
            job->header.storedSize = storedSize;

            wchar_t destination[MAX_PATH] = {};
            wchar_t directory[MAX_PATH] = {};
            wchar_t temporary[MAX_PATH] = {};
            if (!AlphaCacheFilePath(job->header.clipKey,
                    job->header.frame, destination) ||
                !AlphaCacheDirectory(directory) ||
                swprintf_s(temporary, L"%s\\%016llX_%08X_%08X.tmp",
                    directory,
                    static_cast<unsigned long long>(
                        job->header.clipKey),
                    static_cast<unsigned>(job->header.frame),
                    GetCurrentThreadId()) <= 0)
            {
                __leave;
            }

            HANDLE file = CreateFileW(temporary, GENERIC_WRITE, 0,
                nullptr, CREATE_ALWAYS,
                FILE_ATTRIBUTE_TEMPORARY, nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                __leave;
            }
            const bool written =
                WriteFileFully(file, &job->header,
                    sizeof(job->header)) &&
                WriteFileFully(file, stored, storedSize) &&
                FlushFileBuffers(file);
            CloseHandle(file);
            if (!written ||
                !MoveFileExW(temporary, destination,
                    MOVEFILE_REPLACE_EXISTING |
                    MOVEFILE_WRITE_THROUGH))
            {
                DeleteFileW(temporary);
                __leave;
            }
            PrunePersistentAlphaCache(directory);
        }
        __finally
        {
            if (compressed != nullptr)
            {
                HeapFree(GetProcessHeap(), 0, compressed);
            }
            if (raw != nullptr)
            {
                HeapFree(GetProcessHeap(), 0, raw);
            }
            HeapFree(GetProcessHeap(), 0, job);
        }
    }

    void QueuePersistentAlphaCheckpoint(void* animation, void* ssv,
        void* info, void* output, int width, int height,
        int framesPerSecond, int totalFrames, int frame, int bucket,
        std::size_t outputSize, std::size_t stateSize)
    {
        const auto clipKey = static_cast<std::uint64_t>(
            InterlockedCompareExchange64(
                &g_alphaCheckpointClipKey, 0, 0));
        if (clipKey == 0 || PinBridge() < 0)
        {
            return;
        }
        auto job = static_cast<PersistentAlphaWriteJob*>(HeapAlloc(
            GetProcessHeap(), HEAP_ZERO_MEMORY,
            sizeof(PersistentAlphaWriteJob)));
        if (job == nullptr)
        {
            return;
        }
        job->header.magic = PersistentAlphaMagic;
        job->header.version = PersistentAlphaVersion;
        job->header.clipKey = clipKey;
        job->header.frame = frame;
        job->header.bucket = bucket;
        job->header.width = width;
        job->header.height = height;
        job->header.framesPerSecond = framesPerSecond;
        job->header.totalFrames = totalFrames;
        job->header.outputSize = outputSize;
        job->header.stateSize = stateSize;
        job->animation = animation;
        job->ssv = ssv;
        job->info = info;
        job->output = output;
        if (!TrySubmitThreadpoolCallback(
                &WritePersistentAlphaCheckpoint, job, nullptr))
        {
            HeapFree(GetProcessHeap(), 0, job);
        }
    }

    bool ReadAlphaCheckpointIdentity(void* animation, void*& ssv, void*& info,
        void*& output, int& width, int& height, std::size_t& outputSize)
    {
        if (!CanResetAnimationAlpha(animation) ||
            !IsReadable(animation, AnimationInfoOffset + sizeof(void*)))
        {
            return false;
        }

        auto bytes = reinterpret_cast<unsigned char*>(animation);
        ssv = *reinterpret_cast<void**>(bytes + AnimationSsvOffset);
        info = *reinterpret_cast<void**>(bytes + AnimationInfoOffset);
        output = *reinterpret_cast<void**>(bytes + AnimationAlphaOutputOffset);
        width = *reinterpret_cast<const int*>(bytes + AnimationAlphaWidthOffset);
        height = *reinterpret_cast<const int*>(bytes + AnimationAlphaHeightOffset);
        outputSize =
            static_cast<std::size_t>(width) * static_cast<std::size_t>(height);
        return ssv != nullptr && info != nullptr && output != nullptr &&
            outputSize > 0 && outputSize <= MaximumAlphaOutputSize;
    }

    bool ObserveDecodedAlphaProgress(void* animation, int movieFrame)
    {
        void* ssv = nullptr;
        void* info = nullptr;
        void* output = nullptr;
        int width = 0;
        int height = 0;
        std::size_t outputSize = 0;
        if (!ReadAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height, outputSize))
        {
            return false;
        }

        auto animationBytes = static_cast<const unsigned char*>(animation);
        const int alphaFrame = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaFrameOffset);
        const int alphaGeneration = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaGenerationOffset);

        AcquireSRWLockExclusive(&g_seekReadinessLock);
        const bool sameIdentity =
            g_seekReadinessAnimation == animation &&
            g_seekReadinessSsv == ssv &&
            g_seekReadinessInfo == info &&
            g_seekReadinessOutput == output;
        if (sameIdentity &&
            g_seekReadinessAlphaProgress >=
                RequiredAlphaProgressObservations)
        {
            ReleaseSRWLockExclusive(&g_seekReadinessLock);
            return true;
        }
        if (!sameIdentity)
        {
            g_seekReadinessAnimation = animation;
            g_seekReadinessSsv = ssv;
            g_seekReadinessInfo = info;
            g_seekReadinessOutput = output;
            g_seekReadinessAlphaProgress = 0;
        }
        else if (
            movieFrame > g_seekReadinessMovieFrame &&
            AlphaDecoderProgressed(g_seekReadinessAlphaFrame, alphaFrame,
                g_seekReadinessAlphaGeneration, alphaGeneration))
        {
            g_seekReadinessAlphaProgress++;
        }
        g_seekReadinessMovieFrame = movieFrame;
        g_seekReadinessAlphaFrame = alphaFrame;
        g_seekReadinessAlphaGeneration = alphaGeneration;
        const bool ready =
            g_seekReadinessAlphaProgress >=
                RequiredAlphaProgressObservations;
        ReleaseSRWLockExclusive(&g_seekReadinessLock);
        return ready;
    }

    bool HasAlphaCheckpointIdentity(void* animation, void* ssv, void* info,
        void* output, int width, int height)
    {
        return g_alphaCheckpointAnimation == animation &&
            g_alphaCheckpointSsv == ssv &&
            g_alphaCheckpointInfo == info &&
            g_alphaCheckpointOutput == output &&
            g_alphaCheckpointWidth == width &&
            g_alphaCheckpointHeight == height;
    }

    bool InsertAlphaCheckpointLocked(int frame, int bucket,
        std::size_t outputSize, std::size_t stateSize,
        unsigned char* data)
    {
        for (int index = 0; index < g_alphaCheckpointCount; index++)
        {
            if (g_alphaCheckpoints[index].bucket != bucket)
            {
                continue;
            }
            if (g_alphaCheckpoints[index].frame >= frame)
            {
                HeapFree(GetProcessHeap(), 0, data);
                return false;
            }
            g_alphaCheckpointBytes -=
                g_alphaCheckpoints[index].outputSize + stateSize;
            FreeAlphaCheckpoint(g_alphaCheckpoints[index]);
            std::memmove(&g_alphaCheckpoints[index],
                &g_alphaCheckpoints[index + 1],
                static_cast<std::size_t>(
                    g_alphaCheckpointCount - index - 1) *
                    sizeof(AlphaCheckpoint));
            g_alphaCheckpoints[--g_alphaCheckpointCount] = {};
            break;
        }

        while (g_alphaCheckpointCount > 0 &&
            (g_alphaCheckpointCount >= MaximumAlphaCheckpoints ||
                outputSize + stateSize >
                    MaximumAlphaCheckpointBytes -
                        g_alphaCheckpointBytes))
        {
            g_alphaCheckpointBytes -=
                g_alphaCheckpoints[0].outputSize + stateSize;
            FreeAlphaCheckpoint(g_alphaCheckpoints[0]);
            std::memmove(&g_alphaCheckpoints[0],
                &g_alphaCheckpoints[1],
                static_cast<std::size_t>(
                    g_alphaCheckpointCount - 1) *
                    sizeof(AlphaCheckpoint));
            g_alphaCheckpoints[--g_alphaCheckpointCount] = {};
        }

        int insertAt = g_alphaCheckpointCount;
        while (insertAt > 0 &&
            g_alphaCheckpoints[insertAt - 1].frame > frame)
        {
            g_alphaCheckpoints[insertAt] =
                g_alphaCheckpoints[insertAt - 1];
            insertAt--;
        }
        g_alphaCheckpoints[insertAt].frame = frame;
        g_alphaCheckpoints[insertAt].bucket = bucket;
        g_alphaCheckpoints[insertAt].outputSize = outputSize;
        g_alphaCheckpoints[insertAt].data = data;
        g_alphaCheckpointCount++;
        g_alphaCheckpointBytes += outputSize + stateSize;
        return true;
    }

    bool LoadPersistentAlphaCheckpoint(void* animation,
        int targetFrame)
    {
        const auto clipKey = static_cast<std::uint64_t>(
            InterlockedCompareExchange64(
                &g_alphaCheckpointClipKey, 0, 0));
        if (clipKey == 0 || targetFrame <= 0)
        {
            return false;
        }

        void* ssv = nullptr;
        void* info = nullptr;
        void* output = nullptr;
        int width = 0;
        int height = 0;
        std::size_t outputSize = 0;
        if (!ReadAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height, outputSize) ||
            !IsReadable(reinterpret_cast<unsigned char*>(info) +
                    AnimationFramesPerSecondOffset, sizeof(int)) ||
            !IsReadable(reinterpret_cast<unsigned char*>(info) +
                    AnimationTotalFramesOffset, sizeof(int)))
        {
            return false;
        }
        const int framesPerSecond = *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(info) +
                AnimationFramesPerSecondOffset);
        const int totalFrames = *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(info) +
                AnimationTotalFramesOffset);
        const std::size_t stateSize =
            AnimationSsvOffset - AnimationAlphaScratch1Offset;
        const std::size_t rawSize = outputSize + stateSize;

        int bestFrame = -1;
        AcquireSRWLockShared(&g_alphaCheckpointLock);
        if (HasAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height))
        {
            for (int index = 0; index < g_alphaCheckpointCount; index++)
            {
                if (g_alphaCheckpoints[index].frame < targetFrame)
                {
                    bestFrame = g_alphaCheckpoints[index].frame;
                }
                else
                {
                    break;
                }
            }
        }
        ReleaseSRWLockShared(&g_alphaCheckpointLock);

        wchar_t directory[MAX_PATH] = {};
        wchar_t searchPath[MAX_PATH] = {};
        if (!AlphaCacheDirectory(directory) ||
            swprintf_s(searchPath, L"%s\\%016llX_*.qpac",
                directory,
                static_cast<unsigned long long>(clipKey)) <= 0)
        {
            return false;
        }

        wchar_t candidatePath[MAX_PATH] = {};
        WIN32_FIND_DATAW data = {};
        HANDLE search = FindFirstFileW(searchPath, &data);
        if (search == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        do
        {
            unsigned long long fileKey = 0;
            unsigned fileFrame = 0;
            if ((data.dwFileAttributes &
                    FILE_ATTRIBUTE_DIRECTORY) != 0 ||
                swscanf_s(data.cFileName, L"%llX_%X.qpac",
                    &fileKey, &fileFrame) != 2 ||
                fileKey != clipKey || fileFrame > MAXLONG ||
                static_cast<int>(fileFrame) <= bestFrame ||
                static_cast<int>(fileFrame) >= targetFrame)
            {
                continue;
            }
            bestFrame = static_cast<int>(fileFrame);
            if (swprintf_s(candidatePath, L"%s\\%s",
                    directory, data.cFileName) <= 0)
            {
                candidatePath[0] = L'\0';
            }
        } while (FindNextFileW(search, &data));
        FindClose(search);
        if (candidatePath[0] == L'\0')
        {
            return false;
        }

        HANDLE file = CreateFileW(candidatePath,
            GENERIC_READ | FILE_WRITE_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        LARGE_INTEGER fileSize = {};
        PersistentAlphaHeader header = {};
        bool valid = GetFileSizeEx(file, &fileSize) &&
            ReadFileFully(file, &header, sizeof(header)) &&
            header.magic == PersistentAlphaMagic &&
            header.version == PersistentAlphaVersion &&
            header.clipKey == clipKey &&
            header.frame == bestFrame &&
            header.frame < targetFrame &&
            header.bucket >= 0 &&
            header.width == width && header.height == height &&
            header.framesPerSecond == framesPerSecond &&
            header.totalFrames == totalFrames &&
            header.outputSize == outputSize &&
            header.stateSize == stateSize &&
            header.storedSize > 0 &&
            header.storedSize <= rawSize &&
            fileSize.QuadPart ==
                static_cast<LONGLONG>(
                    sizeof(header) + header.storedSize) &&
            (header.compression == 0 || header.compression == 1);

        unsigned char* stored = nullptr;
        unsigned char* raw = nullptr;
        if (valid)
        {
            stored = static_cast<unsigned char*>(HeapAlloc(
                GetProcessHeap(), 0,
                static_cast<std::size_t>(header.storedSize)));
            raw = static_cast<unsigned char*>(HeapAlloc(
                GetProcessHeap(), 0, rawSize));
            valid = stored != nullptr && raw != nullptr &&
                ReadFileFully(file, stored,
                    static_cast<std::size_t>(header.storedSize));
        }
        if (valid && header.compression == 0)
        {
            valid = header.storedSize == rawSize;
            if (valid)
            {
                std::memcpy(raw, stored, rawSize);
            }
        }
        else if (valid)
        {
            DECOMPRESSOR_HANDLE decompressor = nullptr;
            SIZE_T actual = 0;
            valid = CreateDecompressor(
                    COMPRESS_ALGORITHM_XPRESS_HUFF,
                    nullptr, &decompressor) &&
                Decompress(decompressor, stored,
                    static_cast<std::size_t>(header.storedSize),
                    raw, rawSize, &actual) &&
                actual == rawSize;
            if (decompressor != nullptr)
            {
                CloseDecompressor(decompressor);
            }
        }
        valid = valid &&
            PersistentAlphaChecksum(raw, rawSize) == header.checksum;

        const std::size_t pointer1Offset = outputSize +
            AnimationAlphaScratchPointer1Offset -
            AnimationAlphaScratch1Offset;
        const std::size_t pointer2Offset = outputSize +
            AnimationAlphaScratchPointer2Offset -
            AnimationAlphaScratch1Offset;
        if (valid &&
            pointer2Offset + sizeof(void*) <= rawSize)
        {
            void* pointer1 = DecodePersistentAlphaPointer(
                animation, output, outputSize, header.pointer1);
            void* pointer2 = DecodePersistentAlphaPointer(
                animation, output, outputSize, header.pointer2);
            valid = (header.pointer1 == 0 || pointer1 != nullptr) &&
                (header.pointer2 == 0 || pointer2 != nullptr);
            if (valid)
            {
                std::memcpy(raw + pointer1Offset,
                    &pointer1, sizeof(pointer1));
                std::memcpy(raw + pointer2Offset,
                    &pointer2, sizeof(pointer2));
            }
        }
        else
        {
            valid = false;
        }

        FILETIME now = {};
        GetSystemTimeAsFileTime(&now);
        if (valid)
        {
            SetFileTime(file, nullptr, nullptr, &now);
        }
        CloseHandle(file);
        if (stored != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, stored);
        }
        if (!valid)
        {
            if (raw != nullptr)
            {
                HeapFree(GetProcessHeap(), 0, raw);
            }
            return false;
        }

        AcquireSRWLockExclusive(&g_alphaCheckpointLock);
        const bool canInsert =
            static_cast<std::uint64_t>(
                InterlockedCompareExchange64(
                    &g_alphaCheckpointClipKey, 0, 0)) == clipKey &&
            HasAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height);
        const bool inserted = canInsert &&
            InsertAlphaCheckpointLocked(header.frame, header.bucket,
                outputSize, stateSize, raw);
        ReleaseSRWLockExclusive(&g_alphaCheckpointLock);
        if (!canInsert)
        {
            HeapFree(GetProcessHeap(), 0, raw);
        }
        return inserted;
    }

    int CaptureAlphaCheckpoint(void* animation, int frame, int framesPerSecond)
    {
        void* ssv = nullptr;
        void* info = nullptr;
        void* output = nullptr;
        int width = 0;
        int height = 0;
        std::size_t outputSize = 0;
        if (frame < 0 || framesPerSecond <= 0 ||
            !ReadAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height, outputSize))
        {
            return -1;
        }

        const int interval = framesPerSecond * AlphaCheckpointIntervalSeconds;
        const int bucket = interval > 0 ? frame / interval : frame;
        const std::size_t stateSize =
            AnimationSsvOffset - AnimationAlphaScratch1Offset;
        const std::size_t checkpointSize =
            outputSize + stateSize;
        if (checkpointSize > MaximumAlphaCheckpointBytes ||
            !IsReadable(reinterpret_cast<unsigned char*>(info) +
                AnimationTotalFramesOffset, sizeof(int)))
        {
            return -1;
        }
        const int totalFrames = *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(info) +
                AnimationTotalFramesOffset);
        if (totalFrames <= 0)
        {
            return -1;
        }

        AcquireSRWLockExclusive(&g_alphaCheckpointLock);
        if (!HasAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height))
        {
            ClearAlphaCheckpointsLocked();
            g_alphaCheckpointAnimation = animation;
            g_alphaCheckpointSsv = ssv;
            g_alphaCheckpointInfo = info;
            g_alphaCheckpointOutput = output;
            g_alphaCheckpointWidth = width;
            g_alphaCheckpointHeight = height;
        }

        for (int index = 0; index < g_alphaCheckpointCount; index++)
        {
            if (g_alphaCheckpoints[index].bucket == bucket)
            {
                const int count = g_alphaCheckpointCount;
                ReleaseSRWLockExclusive(&g_alphaCheckpointLock);
                return count;
            }
        }

        auto data = static_cast<unsigned char*>(HeapAlloc(
            GetProcessHeap(), 0, checkpointSize));
        if (data == nullptr)
        {
            ReleaseSRWLockExclusive(&g_alphaCheckpointLock);
            return -1;
        }

        std::memcpy(data, output, outputSize);
        std::memcpy(data + outputSize,
            reinterpret_cast<unsigned char*>(animation) +
                AnimationAlphaScratch1Offset,
            stateSize);

        const bool inserted = InsertAlphaCheckpointLocked(
            frame, bucket, outputSize, stateSize, data);
        const int count = g_alphaCheckpointCount;
        ReleaseSRWLockExclusive(&g_alphaCheckpointLock);
        if (inserted)
        {
            QueuePersistentAlphaCheckpoint(animation, ssv, info,
                output, width, height, framesPerSecond,
                totalFrames, frame, bucket, outputSize, stateSize);
        }
        return count;
    }

    bool RestoreAlphaCheckpoint(void* animation, int targetFrame,
        int& restoredFrame)
    {
        void* ssv = nullptr;
        void* info = nullptr;
        void* output = nullptr;
        int width = 0;
        int height = 0;
        std::size_t outputSize = 0;
        if (targetFrame <= 0 ||
            !ReadAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height, outputSize))
        {
            return false;
        }

        LoadPersistentAlphaCheckpoint(animation, targetFrame);

        AcquireSRWLockShared(&g_alphaCheckpointLock);
        if (!HasAlphaCheckpointIdentity(animation, ssv, info, output,
                width, height))
        {
            ReleaseSRWLockShared(&g_alphaCheckpointLock);
            return false;
        }

        const AlphaCheckpoint* checkpoint = nullptr;
        for (int index = 0; index < g_alphaCheckpointCount; index++)
        {
            if (g_alphaCheckpoints[index].frame < targetFrame)
            {
                checkpoint = &g_alphaCheckpoints[index];
            }
            else
            {
                break;
            }
        }
        if (checkpoint == nullptr || checkpoint->data == nullptr ||
            checkpoint->outputSize != outputSize)
        {
            ReleaseSRWLockShared(&g_alphaCheckpointLock);
            return false;
        }

        std::memcpy(output, checkpoint->data, outputSize);
        std::memcpy(
            reinterpret_cast<unsigned char*>(animation) +
                AnimationAlphaScratch1Offset,
            checkpoint->data + outputSize,
            AnimationSsvOffset - AnimationAlphaScratch1Offset);
        restoredFrame = checkpoint->frame;
        InterlockedExchange(&g_lastAlphaCheckpointFrame, restoredFrame);
        InterlockedIncrement(&g_alphaCheckpointRestoreCount);
        ReleaseSRWLockShared(&g_alphaCheckpointLock);
        return true;
    }

    bool ArmDecoderCatchup(void* video, int targetFrame,
        MutexAction lockMutex, MutexAction unlockMutex, int& droppedFrames)
    {
        auto videoBytes = reinterpret_cast<unsigned char*>(video);
        void* mutex = *reinterpret_cast<void**>(
            videoBytes + VideoFrameQueueMutexOffset);
        if (mutex == nullptr)
        {
            return false;
        }

        bool locked = false;
        bool valid = false;
        droppedFrames = 0;
        __try
        {
            lockMutex(mutex);
            locked = true;

            void* queue = *reinterpret_cast<void**>(
                videoBytes + VideoFrameQueueOffset);
            if (IsReadable(queue, QueueEntriesOffset))
            {
                const int begin = *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(queue) +
                        QueueBeginOffset);
                const int end = *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(queue) +
                        QueueEndOffset);
                if (begin >= 0 && end >= begin && end - begin <= 64)
                {
                    valid = true;
                    for (int index = begin; index < end; index++)
                    {
                        auto slotAddress = reinterpret_cast<unsigned char*>(queue) +
                            QueueEntriesOffset +
                            static_cast<std::size_t>(index) * sizeof(void*);
                        if (!IsReadable(slotAddress, sizeof(void*)))
                        {
                            valid = false;
                            break;
                        }

                        void* entry = *reinterpret_cast<void**>(slotAddress);
                        if (!IsReadable(entry,
                                VideoQueueEntryReadyOffset + 1))
                        {
                            valid = false;
                            break;
                        }

                        auto frame = reinterpret_cast<int*>(entry);
                        if (*frame >= 0)
                        {
                            droppedFrames++;
                        }
                        *frame = -1;
                        *(reinterpret_cast<unsigned char*>(entry) +
                            VideoQueueEntryReadyOffset) = 0;
                    }
                    if (valid)
                    {
                        InterlockedExchangePointer(&g_decoderCatchupVideo, video);
                        InterlockedExchange(&g_decoderCatchupTargetFrame,
                            targetFrame);
                    }
                }
            }
        }
        __finally
        {
            if (locked)
            {
                unlockMutex(mutex);
            }
        }
        return valid;
    }

    template<typename T>
    T FunctionAt(std::uintptr_t rva)
    {
        return reinterpret_cast<T>(ImageBase() + rva);
    }

    HRESULT ManagerError();

    int WmvQueueDepth(void* ssvReader, std::size_t queueOffset)
    {
        auto queueAddress = reinterpret_cast<unsigned char*>(ssvReader) +
            queueOffset;
        if (!IsReadable(queueAddress, sizeof(void*)))
        {
            return -1;
        }

        void* data = *reinterpret_cast<void**>(queueAddress);
        if (!IsReadable(data, QueueEntriesOffset))
        {
            return -1;
        }

        const int begin = *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(data) + QueueBeginOffset);
        const int end = *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(data) + QueueEndOffset);
        return begin >= 0 && end >= begin && end - begin <= 4096
            ? end - begin
            : -1;
    }

    bool WmvColorQueuePrimed(void* ssvReader)
    {
        const HMODULE qtCore = GetModuleHandleW(L"Qt5Core.dll");
        const auto lockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexAction>(GetProcAddress(
                qtCore, "?lock@QMutex@@QEAAXXZ"));
        const auto unlockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexAction>(GetProcAddress(
                qtCore, "?unlock@QMutex@@QEAAXXZ"));
        if (lockMutex == nullptr || unlockMutex == nullptr ||
            !IsReadable(ssvReader, WmvColorQueueOffset + sizeof(void*)))
        {
            return false;
        }

        void* mutex = reinterpret_cast<unsigned char*>(ssvReader) +
            WmvQueueMutexOffset;
        bool locked = false;
        bool primed = false;
        __try
        {
            lockMutex(mutex);
            locked = true;
            primed = WmvQueueDepth(ssvReader, WmvColorQueueOffset) > 0;
        }
        __finally
        {
            if (locked)
            {
                unlockMutex(mutex);
            }
        }
        return primed;
    }

    std::uint64_t WmvFrameTime(int frame, int framesPerSecond)
    {
        return (static_cast<std::uint64_t>(frame) * 10'000'000ULL +
            static_cast<std::uint64_t>(framesPerSecond / 2)) /
            static_cast<std::uint64_t>(framesPerSecond);
    }

    HRESULT DeliverWmvUntil(void* advanced, int untilFrame,
        int framesPerSecond, int totalFrames)
    {
        if (!IsReadable(advanced, sizeof(void*)) ||
            untilFrame <= 0 || framesPerSecond <= 0 || totalFrames <= 0)
        {
            return E_INVALIDARG;
        }
        auto vtable = *reinterpret_cast<void***>(advanced);
        if (!IsReadable(vtable, sizeof(void*) * 6) ||
            !IsExecutableMemory(vtable[5]))
        {
            return ManagerError();
        }
        const int maximumFrame = totalFrames <=
            MAXLONG - framesPerSecond
                ? totalFrames + framesPerSecond
                : totalFrames;
        const int boundedFrame = untilFrame < maximumFrame
            ? untilFrame : maximumFrame;
        return reinterpret_cast<WmvDeliverTime>(
            vtable[5])(advanced,
                WmvFrameTime(boundedFrame, framesPerSecond));
    }

    void ClearWmvClock()
    {
        InterlockedExchangePointer(&g_wmvClockMovie, nullptr);
        InterlockedExchangePointer(&g_wmvClockVideo, nullptr);
        InterlockedExchangePointer(&g_wmvClockAdvanced, nullptr);
        InterlockedExchange(&g_wmvClockFramesPerSecond, 0);
        InterlockedExchange(&g_wmvClockTotalFrames, 0);
        InterlockedExchange(&g_wmvClockDeliveredUntilFrame, 0);
        InterlockedExchange(&g_wmvClockReleaseFrame, -1);
        InterlockedExchange(&g_wmvClockStreaming, 0);
    }

    bool ReleaseWmvClockAudio(void* video)
    {
        void* sound = VideoAudioSound(video);
        if (sound == nullptr)
        {
            return true;
        }
        WaitForAudioWrites();
        if (!ClearSoundPending(sound))
        {
            return false;
        }
        InterlockedCompareExchangePointer(
            &g_audioSuppressedSound, nullptr, sound);
        return true;
    }

    HRESULT RestartWmvReader(void* video, int firstFrame,
        int deliveredUntilFrame, int framesPerSecond,
        int totalFrames, bool useUserClock);

    DWORD WINAPI WmvClockWorker(void*)
    {
        while (SleepEx(10, FALSE) == 0)
        {
            void* movie = InterlockedCompareExchangePointer(
                &g_wmvClockMovie, nullptr, nullptr);
            if (movie == nullptr ||
                !TryAcquireSRWLockShared(&g_wmvRestartLock))
            {
                continue;
            }

            __try
            {
                void* video = InterlockedCompareExchangePointer(
                    &g_wmvClockVideo, nullptr, nullptr);
                void* advanced = InterlockedCompareExchangePointer(
                    &g_wmvClockAdvanced, nullptr, nullptr);
                const int framesPerSecond = InterlockedCompareExchange(
                    &g_wmvClockFramesPerSecond, 0, 0);
                const int totalFrames = InterlockedCompareExchange(
                    &g_wmvClockTotalFrames, 0, 0);
                void* activeMovie = ActiveMovie();
                void* animation = activeMovie == movie &&
                    IsReadable(movie, MovieAnimationOffset + sizeof(void*))
                    ? *reinterpret_cast<void**>(
                        reinterpret_cast<unsigned char*>(movie) +
                            MovieAnimationOffset)
                    : nullptr;
                auto currentAddress = reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(movie) +
                        MovieCurrentFrameOffset);
                if (activeMovie != movie ||
                    AnimationVideoDecoder(animation) != video ||
                    !IsReadable(currentAddress, sizeof(*currentAddress)) ||
                    !IsReadable(advanced, sizeof(void*)) ||
                    framesPerSecond <= 0 || totalFrames <= 0)
                {
                    InterlockedExchange(&g_wmvClockResult,
                        HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
                    ClearWmvClock();
                    __leave;
                }

                auto videoBytes = reinterpret_cast<unsigned char*>(video);
                void* ssvReader = IsReadable(videoBytes +
                        WmvReaderObjectOffset, sizeof(void*))
                    ? *reinterpret_cast<void**>(
                        videoBytes + WmvReaderObjectOffset)
                    : nullptr;
                auto readerResult = IsReadable(ssvReader,
                        WmvLastResultOffset + sizeof(HRESULT))
                    ? reinterpret_cast<volatile HRESULT*>(
                        reinterpret_cast<unsigned char*>(ssvReader) +
                            WmvLastResultOffset)
                    : nullptr;
                if (readerResult == nullptr)
                {
                    InterlockedExchange(
                        &g_wmvClockResult, ManagerError());
                    __leave;
                }
                const HRESULT readerStatus = *readerResult;
                if (readerStatus != E_PENDING && FAILED(readerStatus))
                {
                    InterlockedExchange(&g_wmvClockResult, readerStatus);
                    __leave;
                }

                const int currentFrame = *currentAddress;
                if (!g_wmvUserClock &&
                    InterlockedCompareExchange(
                        &g_wmvClockResult, E_PENDING, E_PENDING) ==
                            BridgeSuccess)
                {
                    __leave;
                }
                const int releaseFrame = InterlockedCompareExchange(
                    &g_wmvClockReleaseFrame, -1, -1);
                bool justReleased = false;
                if (InterlockedCompareExchange(
                        &g_wmvClockStreaming, 0, 0) == 0)
                {
                    if (currentFrame < releaseFrame)
                    {
                        __leave;
                    }
                    if (IsNormalPlaybackRate(g_wmvRate))
                    {
                        if (!ReleaseWmvClockAudio(video))
                        {
                            InterlockedExchange(&g_wmvClockResult, E_FAIL);
                            __leave;
                        }
                        InterlockedExchange(&g_wmvClockStreaming, 1);
                        InterlockedExchange(
                            &g_wmvClockResult, BridgeSuccess);
                        __leave;
                    }
                    auto frameHeld =
                        videoBytes + WmvFrameHeldOffset;
                    if (!IsReadable(ssvReader, sizeof(void*)) ||
                        !IsWritable(frameHeld, sizeof(*frameHeld)))
                    {
                        InterlockedExchange(
                            &g_wmvClockResult, ManagerError());
                        __leave;
                    }
                    FunctionAt<WmvClearQueues>(
                        WmvClearQueuesRva)(ssvReader);
                    *frameHeld = 0;
                    InterlockedExchange(
                        &g_wmvClockDeliveredUntilFrame,
                        currentFrame + 1);
                    if (!ReleaseWmvClockAudio(video))
                    {
                        InterlockedExchange(&g_wmvClockResult, E_FAIL);
                        __leave;
                    }
                    InterlockedExchange(&g_wmvClockStreaming, 1);
                    justReleased = true;
                }

                const bool normalPlayback = IsNormalPlaybackRate(g_wmvRate);
                const double bufferedSeconds = std::max(
                    0.25, std::min(g_wmvRate, 4.0) / 4.0);
                const int bufferFrames = normalPlayback ? 1 : std::max(1,
                    static_cast<int>(
                        framesPerSecond * bufferedSeconds + 0.999));
                const int finalUntilFrame = totalFrames <=
                    MAXLONG - framesPerSecond
                        ? totalFrames + framesPerSecond
                        : totalFrames;
                const int desiredUntilFrame = currentFrame >=
                    totalFrames - bufferFrames - 1
                    ? finalUntilFrame
                    : currentFrame + bufferFrames + 1;
                const int deliveredUntilFrame = InterlockedCompareExchange(
                    &g_wmvClockDeliveredUntilFrame, 0, 0);
                const int deliveryStep = normalPlayback ? 1 :
                    (framesPerSecond > 4 ? framesPerSecond / 4 : 1);
                if ((desiredUntilFrame == finalUntilFrame &&
                        deliveredUntilFrame < finalUntilFrame) ||
                    desiredUntilFrame >=
                        deliveredUntilFrame + deliveryStep)
                {
                    const HRESULT result = DeliverWmvUntil(advanced,
                        desiredUntilFrame, framesPerSecond, totalFrames);
                    if (FAILED(result) && result != WmvInvalidRequest)
                    {
                        InterlockedExchange(&g_wmvClockResult, result);
                        InterlockedExchange(&g_wmvClockStreaming, 0);
                    }
                    else if (SUCCEEDED(result))
                    {
                        InterlockedExchange(
                            &g_wmvClockDeliveredUntilFrame,
                            desiredUntilFrame);
                        if (justReleased)
                        {
                            InterlockedExchange(
                                &g_wmvClockResult, BridgeSuccess);
                        }
                    }
                }
                else if (justReleased)
                {
                    InterlockedExchange(
                        &g_wmvClockResult, BridgeSuccess);
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                InterlockedExchange(&g_wmvClockResult, E_UNEXPECTED);
                ClearWmvClock();
            }
            ReleaseSRWLockShared(&g_wmvRestartLock);
        }
        return 0;
    }

    BOOL CALLBACK StartWmvClockWorker(PINIT_ONCE, PVOID, PVOID*)
    {
        const HRESULT pinResult = PinBridge();
        if (FAILED(pinResult))
        {
            InterlockedExchange(&g_wmvClockWorkerResult, pinResult);
            return TRUE;
        }
        const HANDLE thread = CreateThread(nullptr, 0,
            &WmvClockWorker, nullptr, 0, nullptr);
        if (thread == nullptr)
        {
            InterlockedExchange(&g_wmvClockWorkerResult,
                HRESULT_FROM_WIN32(GetLastError()));
            return TRUE;
        }
        CloseHandle(thread);
        InterlockedExchange(
            &g_wmvClockWorkerResult, BridgeSuccess);
        return TRUE;
    }

    HRESULT ArmWmvClock(void* movie, void* video,
        int framesPerSecond, int totalFrames,
        int deliveredUntilFrame, int releaseFrame)
    {
        if (!InitOnceExecuteOnce(&g_wmvClockWorkerOnce,
                &StartWmvClockWorker, nullptr, nullptr))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        const HRESULT workerResult = static_cast<HRESULT>(
            InterlockedCompareExchange(
                &g_wmvClockWorkerResult, E_PENDING, E_PENDING));
        if (FAILED(workerResult))
        {
            return workerResult;
        }
        auto videoBytes = reinterpret_cast<unsigned char*>(video);
        if (!IsReadable(videoBytes + WmvReaderObjectOffset,
                sizeof(void*)))
        {
            return ManagerError();
        }
        void* reader = *reinterpret_cast<void**>(
            videoBytes + WmvReaderObjectOffset);
        void* advanced = IsReadable(reader,
            WmvAdvancedInterfaceOffset + sizeof(void*))
            ? *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(reader) +
                    WmvAdvancedInterfaceOffset)
            : nullptr;
        if (!IsReadable(advanced, sizeof(void*)))
        {
            return ManagerError();
        }

        ClearWmvClock();
        InterlockedExchangePointer(&g_wmvClockVideo, video);
        InterlockedExchangePointer(&g_wmvClockAdvanced, advanced);
        InterlockedExchange(
            &g_wmvClockFramesPerSecond, framesPerSecond);
        InterlockedExchange(&g_wmvClockTotalFrames, totalFrames);
        InterlockedExchange(&g_wmvClockDeliveredUntilFrame,
            deliveredUntilFrame);
        InterlockedExchange(&g_wmvClockReleaseFrame, releaseFrame);
        InterlockedExchange(&g_wmvClockResult, E_PENDING);
        InterlockedExchangePointer(&g_wmvClockMovie, movie);
        return BridgeSuccess;
    }

    HRESULT WmvClockStatus(void* movie)
    {
        return InterlockedCompareExchangePointer(
                &g_wmvClockMovie, nullptr, nullptr) == movie
            ? static_cast<HRESULT>(InterlockedCompareExchange(
                &g_wmvClockResult, E_PENDING, E_PENDING))
            : HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
    }

    HRESULT RestartWmvReader(void* video, int firstFrame,
        int deliveredUntilFrame, int framesPerSecond,
        int totalFrames, bool useUserClock)
    {
        if (!IsWmvDecoder(video) || firstFrame < 0 ||
            deliveredUntilFrame <= firstFrame ||
            deliveredUntilFrame > totalFrames ||
            framesPerSecond <= 0 || totalFrames <= 0)
        {
            return E_INVALIDARG;
        }

        auto videoBytes = reinterpret_cast<unsigned char*>(video);
        auto frameHeld = videoBytes + WmvFrameHeldOffset;
        if (!IsReadable(videoBytes + WmvReaderObjectOffset, sizeof(void*)) ||
            !IsWritable(frameHeld, sizeof(*frameHeld)))
        {
            return ManagerError();
        }
        void* ssvReader = *reinterpret_cast<void**>(
            videoBytes + WmvReaderObjectOffset);
        if (!IsReadable(ssvReader, sizeof(void*)) ||
            *reinterpret_cast<void**>(ssvReader) !=
                ImageBase() + SsvReaderVtableRva)
        {
            return ManagerError();
        }

        auto readerBytes = reinterpret_cast<unsigned char*>(ssvReader);
        auto counter = reinterpret_cast<std::uint64_t*>(
            readerBytes + WmvSampleCounterOffset);
        auto paused = readerBytes + WmvReaderPausedOffset;
        auto lastResult = reinterpret_cast<volatile HRESULT*>(
            readerBytes + WmvLastResultOffset);
        if (!IsWritable(counter, sizeof(*counter)) ||
            !IsWritable(paused, sizeof(*paused)) ||
            !IsWritable(const_cast<HRESULT*>(lastResult),
                sizeof(*lastResult)) ||
            !IsReadable(readerBytes + WmvReaderInterfaceOffset,
                sizeof(void*)) ||
            !IsReadable(readerBytes + WmvAdvancedInterfaceOffset,
                sizeof(void*)) ||
            !IsReadable(readerBytes + WmvStatusEventOffset,
                sizeof(HANDLE)))
        {
            return ManagerError();
        }

        void* reader = *reinterpret_cast<void**>(
            readerBytes + WmvReaderInterfaceOffset);
        void* advanced = *reinterpret_cast<void**>(
            readerBytes + WmvAdvancedInterfaceOffset);
        HANDLE statusEvent = *reinterpret_cast<HANDLE*>(
            readerBytes + WmvStatusEventOffset);
        if (!IsReadable(reader, sizeof(void*)) ||
            !IsReadable(advanced, sizeof(void*)) ||
            statusEvent == nullptr || statusEvent == INVALID_HANDLE_VALUE)
        {
            return ManagerError();
        }
        auto vtable = *reinterpret_cast<void***>(reader);
        auto advancedVtable = *reinterpret_cast<void***>(advanced);
        if (!IsReadable(vtable, sizeof(void*) * 12) ||
            !IsExecutableMemory(vtable[10]) ||
            !IsExecutableMemory(vtable[11]) ||
            !IsReadable(advancedVtable, sizeof(void*) * 6) ||
            !IsExecutableMemory(advancedVtable[3]) ||
            !IsExecutableMemory(advancedVtable[5]))
        {
            return ManagerError();
        }

        // Stop the asynchronous reader before clearing its queues.
        const unsigned char previousPaused = *paused;
        *paused = 1;
        ResetEvent(statusEvent);
        HRESULT result = reinterpret_cast<WmvAction>(vtable[11])(reader);
        if (FAILED(result) && result != WmvInvalidRequest)
        {
            *paused = previousPaused;
            return result;
        }

        if (SUCCEEDED(result))
        {
            const DWORD wait = WaitForSingleObject(statusEvent, 10'000);
            if (wait != WAIT_OBJECT_0)
            {
                *paused = previousPaused;
                return wait == WAIT_FAILED
                    ? HRESULT_FROM_WIN32(GetLastError())
                    : HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            }
        }
        // WMV PCM arrives on the Windows Media callback, not the guarded
        // FFmpeg write site. Clear only CBpkSound's pending bytes here; calling
        // QAudioOutput::stop/start from this non-Qt worker corrupts its timers.
        void* sound = VideoAudioSound(video);
        if (sound != nullptr)
        {
            ClearSoundPending(sound);
        }

        FunctionAt<WmvClearQueues>(WmvClearQueuesRva)(ssvReader);
        // VideoWmvCore leaves the displayed colour frame at the front of the
        // completed queue until the next getFrame call. Queue clearing removes
        // it, so clear the matching held flag or getFrame will discard the
        // first new colour frame while alpha retains its first frame.
        *frameHeld = 0;
        *counter = static_cast<std::uint64_t>(firstFrame);
        *lastResult = E_PENDING;
        ResetEvent(statusEvent);

        result = reinterpret_cast<WmvSetUserClock>(
            advancedVtable[3])(advanced, useUserClock);
        if (FAILED(result))
        {
            *paused = previousPaused;
            return result;
        }

        const std::uint64_t startTime =
            WmvFrameTime(firstFrame, framesPerSecond);
        *paused = 0;
        result = reinterpret_cast<WmvStart>(vtable[10])(
            reader, startTime, 0, 1.0f, nullptr);
        if (FAILED(result))
        {
            return result;
        }

        if (useUserClock)
        {
            const ULONGLONG startedDeadline =
                GetTickCount64() + 10'000;
            while (*lastResult == E_PENDING)
            {
                if (GetTickCount64() >= startedDeadline)
                {
                    return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
                }
                Sleep(2);
            }
            if (FAILED(*lastResult))
            {
                return *lastResult;
            }

            // vghd's WMT_STARTED callback stores its result before it calls
            // DeliverTime(0). Let that callback finish so our real clock
            // horizon cannot be overwritten by its initial zero.
            Sleep(20);
            result = DeliverWmvUntil(advanced, deliveredUntilFrame,
                framesPerSecond, totalFrames);
            if (FAILED(result))
            {
                return result;
            }
        }

        const ULONGLONG deadline = GetTickCount64() + 10'000;
        // The mask is SSV frame data, not the optional WM audio stream.
        while (!WmvColorQueuePrimed(ssvReader))
        {
            if (GetTickCount64() >= deadline)
            {
                return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            }
            Sleep(2);
        }
        return S_OK;
    }

    HRESULT ResetWmvPlaybackSession(void* movie)
    {
        AcquireSRWLockExclusive(&g_wmvRestartLock);
        HRESULT result = BridgeSuccess;
        __try
        {
            void* animation = g_wmvRateAnimation;
            void* video = g_wmvRateVideo;
            void* currentAnimation = IsMovie(movie) &&
                IsReadable(movie,
                    MovieAnimationOffset + sizeof(void*))
                ? *reinterpret_cast<void**>(
                    reinterpret_cast<unsigned char*>(movie) +
                        MovieAnimationOffset)
                : nullptr;
            const bool restoreSystemClock = g_wmvUserClock &&
                animation != nullptr && currentAnimation == animation &&
                IsWmvDecoder(video) &&
                AnimationVideoDecoder(animation) == video;

            ClearWmvClock();
            if (restoreSystemClock)
            {
                void* info = IsReadable(animation,
                    AnimationInfoOffset + sizeof(void*))
                    ? *reinterpret_cast<void**>(
                        reinterpret_cast<unsigned char*>(animation) +
                            AnimationInfoOffset)
                    : nullptr;
                auto currentAddress = reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(movie) +
                        MovieCurrentFrameOffset);
                auto stateAddress = reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(movie) +
                        MovieStateOffset);
                if (!IsReadable(info,
                        AnimationFramesPerSecondOffset + sizeof(int)) ||
                    !IsReadable(currentAddress, sizeof(*currentAddress)) ||
                    !IsReadable(stateAddress, sizeof(*stateAddress)))
                {
                    result = ManagerError();
                }
                else
                {
                    const int totalFrames =
                        *reinterpret_cast<const int*>(
                            reinterpret_cast<unsigned char*>(info) +
                                AnimationTotalFramesOffset);
                    const int framesPerSecond =
                        *reinterpret_cast<const int*>(
                            reinterpret_cast<unsigned char*>(info) +
                                AnimationFramesPerSecondOffset);
                    const int currentFrame = *currentAddress;
                    if (totalFrames <= 0 || framesPerSecond <= 0 ||
                        currentFrame < 0)
                    {
                        result = E_INVALIDARG;
                    }
                    else
                    {
                        const int firstFrame =
                            currentFrame < totalFrames - 1
                                ? currentFrame + 1
                                : totalFrames - 1;
                        const bool wasPlaying =
                            *stateAddress == PlayingState;
                        if (wasPlaying)
                        {
                            FunctionAt<ManagerAction>(
                                MoviePauseRva)(movie);
                        }
                        __try
                        {
                            result = RestartWmvReader(video, firstFrame,
                                firstFrame + 1, framesPerSecond,
                                totalFrames, false);
                        }
                        __finally
                        {
                            if (wasPlaying)
                            {
                                FunctionAt<ManagerAction>(
                                    MovieResumeRva)(movie);
                            }
                        }
                    }
                }
            }
        }
        __finally
        {
            ClearWmvClock();
            g_wmvRateAnimation = nullptr;
            g_wmvRateVideo = nullptr;
            g_wmvRate = 1.0;
            g_wmvUserClock = false;
            ReleaseSRWLockExclusive(&g_wmvRestartLock);
        }
        return result;
    }

    bool ObserveWmvQueueProgress(void* reader, int movieFrame)
    {
        const bool queuesPrimed = WmvColorQueuePrimed(reader);
        AcquireSRWLockExclusive(&g_seekReadinessLock);
        if (g_seekReadinessWmvReader != reader)
        {
            g_seekReadinessWmvReader = reader;
            g_seekReadinessWmvFrame = movieFrame;
            g_seekReadinessWmvProgress = 0;
        }
        else if (queuesPrimed && movieFrame > g_seekReadinessWmvFrame)
        {
            g_seekReadinessWmvProgress++;
            g_seekReadinessWmvFrame = movieFrame;
        }
        const bool ready =
            g_seekReadinessWmvProgress >=
                RequiredAlphaProgressObservations;
        ReleaseSRWLockExclusive(&g_seekReadinessLock);
        return ready;
    }

    bool IsSeekReady(void* movie)
    {
        if (!HasFastForwardEngine() || !IsActiveMovieCandidate(movie))
        {
            return false;
        }

        auto movieBytes = reinterpret_cast<unsigned char*>(movie);
        void* animation = *reinterpret_cast<void**>(
            movieBytes + MovieAnimationOffset);
        void* video = AnimationVideoDecoder(animation);
        if (IsWmvDecoder(video))
        {
            auto videoBytes = reinterpret_cast<unsigned char*>(video);
            if (!IsReadable(
                    videoBytes + WmvReaderObjectOffset, sizeof(void*)))
            {
                return false;
            }
            void* reader = *reinterpret_cast<void**>(
                videoBytes + WmvReaderObjectOffset);
            if (!IsReadable(reader, sizeof(void*)) ||
                *reinterpret_cast<void**>(reader) !=
                    ImageBase() + SsvReaderVtableRva)
            {
                return false;
            }
            return ObserveWmvQueueProgress(reader,
                *reinterpret_cast<const int*>(
                    movieBytes + MovieCurrentFrameOffset));
        }

        video = VideoDecoder(animation);
        if (video == nullptr)
        {
            return false;
        }
        if (!CanResetAnimationAlpha(animation))
        {
            return false;
        }

        auto videoBytes = reinterpret_cast<unsigned char*>(video);
        void* formatContext = *reinterpret_cast<void**>(
            videoBytes + VideoFormatContextOffset);
        void* queue = *reinterpret_cast<void**>(
            videoBytes + VideoFrameQueueOffset);
        void* queueMutex = *reinterpret_cast<void**>(
            videoBytes + VideoFrameQueueMutexOffset);
        if (!IsReadable(formatContext, 0x38) ||
            !IsReadable(queue, QueueEntriesOffset) ||
            queueMutex == nullptr)
        {
            return false;
        }

        const int movieFrame = *reinterpret_cast<const int*>(
            movieBytes + MovieCurrentFrameOffset);
        const int decoderFrame = *reinterpret_cast<const int*>(
            videoBytes + VideoCurrentFrameOffset);
        const bool alphaReady =
            ObserveDecodedAlphaProgress(animation, movieFrame);
        return alphaReady &&
            DecoderFramesReady(movieFrame, decoderFrame);
    }

    HRESULT EngineError()
    {
        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
    }

    HRESULT ManagerError()
    {
        return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    }

    HRESULT TimelineResult(bool totalDuration)
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = ActiveMovie();
        if (movie == nullptr)
        {
            return ManagerError();
        }

        // The timer display tolerates a transient read. Taking Movie's QMutex
        // here stalled vghd window input on every QuickPlayer poll.
        auto animationAddress =
            reinterpret_cast<unsigned char*>(movie) + MovieAnimationOffset;
        if (!IsReadable(animationAddress, sizeof(void*)))
        {
            return ManagerError();
        }

        void* animation = *reinterpret_cast<void**>(animationAddress);
        if (!IsReadable(animation, 0x20))
        {
            return ManagerError();
        }

        auto infoAddress =
            reinterpret_cast<unsigned char*>(animation) + AnimationInfoOffset;
        if (!IsReadable(infoAddress, sizeof(void*)))
        {
            return ManagerError();
        }

        void* info = *reinterpret_cast<void**>(infoAddress);
        if (info == nullptr)
        {
            return ManagerError();
        }

        auto totalFramesAddress =
            reinterpret_cast<unsigned char*>(info) + AnimationTotalFramesOffset;
        auto framesPerSecondAddress =
            reinterpret_cast<unsigned char*>(info) +
                AnimationFramesPerSecondOffset;
        auto currentFrameAddress =
            reinterpret_cast<unsigned char*>(movie) + MovieCurrentFrameOffset;
        if (!IsReadable(totalFramesAddress, sizeof(int)) ||
            !IsReadable(framesPerSecondAddress, sizeof(int)) ||
            !IsReadable(currentFrameAddress, sizeof(int)))
        {
            return ManagerError();
        }

        const int framesPerSecond =
            *reinterpret_cast<const int*>(framesPerSecondAddress);
        const int frames = totalDuration
            ? *reinterpret_cast<const int*>(totalFramesAddress)
            : *reinterpret_cast<const int*>(currentFrameAddress);
        if (framesPerSecond <= 0 || (totalDuration && frames < 0))
        {
            return E_FAIL;
        }

        const int timelineFrames = frames < 0 ? 0 : frames;
        const std::int64_t milliseconds =
            static_cast<std::int64_t>(timelineFrames) * 1000 / framesPerSecond;
        return milliseconds > MAXLONG
            ? MAXLONG
            : static_cast<HRESULT>(milliseconds);
    }

    int __cdecl FastAvcodecOpen2(void* codecContext, const void* codec, void* options)
    {
        const LONG threadCount = InterlockedCompareExchange(&g_decoderThreadCount, 0, 0);
        LONG optionResult = HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        const HMODULE avutil = GetModuleHandleW(L"avutil-55.dll");
        if (avutil != nullptr)
        {
            const auto setOption = reinterpret_cast<AvOptSetInt>(
                GetProcAddress(avutil, "av_opt_set_int"));
            if (setOption != nullptr && threadCount > 0)
            {
                optionResult = setOption(codecContext, "threads", threadCount, 0);
            }
        }

        InterlockedExchange(&g_lastThreadOptionResult, optionResult);
        InterlockedIncrement(&g_decoderOpenCount);

        const auto original = reinterpret_cast<AvcodecOpen2>(
            InterlockedCompareExchangePointer(&g_originalAvcodecOpen2, nullptr, nullptr));
        return original != nullptr ? original(codecContext, codec, options) : -1;
    }

    int __cdecl FastAvSeekFrame(void* formatContext, int streamIndex,
        std::int64_t timestamp, int flags)
    {
        const auto original = reinterpret_cast<AvSeekFrame>(
            InterlockedCompareExchangePointer(
                &g_originalAvSeekFrame, nullptr, nullptr));
        const int result = original != nullptr
            ? original(formatContext, streamIndex, timestamp, flags)
            : -1;
        if (result >= 0)
        {
            FlushVideoCodec(formatContext);
        }
        return result;
    }

    double AudioPlaybackRate()
    {
        const LONGLONG rateBits = InterlockedCompareExchange64(
            &g_audioPlaybackRateBits, 0, 0);
        double rate = 1.0;
        std::memcpy(&rate, &rateBits, sizeof(rate));
        return _finite(rate) && rate >= 0.01 && rate <= 100.0
            ? rate : 1.0;
    }

    void WriteSoundPcmAtPlaybackRate(void* sound, const void* firstChannel,
        const void* secondChannel, int sampleCount)
    {
        const auto original = reinterpret_cast<BpkSoundWrite>(
            InterlockedCompareExchangePointer(
                &g_originalBpkSoundWrite, nullptr, nullptr));
        if (original == nullptr)
        {
            return;
        }

        const double rate = AudioPlaybackRate();
        if (IsNormalPlaybackRate(rate))
        {
            original(sound, firstChannel, secondChannel, sampleCount);
        }
    }

    void __fastcall GuardedBpkSoundWrite(void* sound, const void* begin,
        const void* end, int sampleCount)
    {
        InterlockedIncrement(&g_audioWritesInFlight);
        bool writePcm = true;
        if (InterlockedCompareExchangePointer(
                &g_audioResetPendingSound, nullptr, nullptr) == sound)
        {
            if (!RestartSoundAudio(sound))
            {
                writePcm = false;
            }
            else
            {
                InterlockedCompareExchangePointer(
                    &g_audioResetPendingSound, nullptr, sound);
                if (InterlockedCompareExchangePointer(
                        &g_audioReleaseAfterResetSound,
                        nullptr, sound) == sound)
                {
                    InterlockedCompareExchangePointer(
                        &g_audioSuppressedSound, nullptr, sound);
                    if (InterlockedExchange(
                            &g_audioResumeAfterReset, 0) == 0)
                    {
                        ControlSoundAudio(sound, AudioCommand::Suspend);
                    }
                }
                else
                {
                    ControlSoundAudio(sound, AudioCommand::Suspend);
                    writePcm = false;
                }
            }
        }
        else if (InterlockedCompareExchangePointer(
                &g_audioSuppressedSound, nullptr, nullptr) == sound)
        {
            writePcm = false;
        }

        if (writePcm)
        {
            WriteSoundPcmAtPlaybackRate(
                sound, begin, end, sampleCount);
        }
        else
        {
            InterlockedIncrement(&g_audioWritesSkipped);
        }
        InterlockedDecrement(&g_audioWritesInFlight);
    }

    void __fastcall GuardedBpkSoundRawWrite(void* sound, const void* bytes,
        int byteCount)
    {
        InterlockedIncrement(&g_audioWritesInFlight);
        bool writePcm = IsNormalPlaybackRate(AudioPlaybackRate()) &&
            InterlockedCompareExchangePointer(
                &g_audioSuppressedSound, nullptr, nullptr) != sound;
        if (writePcm &&
            InterlockedCompareExchangePointer(
                &g_audioResetPendingSound, nullptr, nullptr) == sound)
        {
            writePcm = RestartSoundAudio(sound);
            if (writePcm)
            {
                InterlockedCompareExchangePointer(
                    &g_audioResetPendingSound, nullptr, sound);
                InterlockedCompareExchangePointer(
                    &g_audioReleaseAfterResetSound, nullptr, sound);
                InterlockedExchange(&g_audioResumeAfterReset, 0);
            }
        }

        if (!writePcm)
        {
            InterlockedIncrement(&g_audioWritesSkipped);
        }
        else
        {
            const auto original = reinterpret_cast<BpkSoundRawWrite>(
                InterlockedCompareExchangePointer(
                    &g_originalBpkSoundRawWrite, nullptr, nullptr));
            if (original != nullptr)
            {
                original(sound, bytes, byteCount);
            }
        }
        InterlockedDecrement(&g_audioWritesInFlight);
    }

    struct SuspendedThreads
    {
        HANDLE handles[512] = {};
        std::size_t count = 0;
    };

    bool SuspendOtherThreads(SuspendedThreads& suspended)
    {
        const DWORD processId = GetCurrentProcessId();
        const DWORD currentThreadId = GetCurrentThreadId();
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        THREADENTRY32 entry = {};
        entry.dwSize = sizeof(entry);
        bool complete = Thread32First(snapshot, &entry) != FALSE;
        while (complete)
        {
            if (entry.th32OwnerProcessID == processId &&
                entry.th32ThreadID != currentThreadId)
            {
                if (suspended.count ==
                    sizeof(suspended.handles) / sizeof(suspended.handles[0]))
                {
                    CloseHandle(snapshot);
                    return false;
                }

                HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME, FALSE,
                    entry.th32ThreadID);
                if (thread != nullptr)
                {
                    if (SuspendThread(thread) != static_cast<DWORD>(-1))
                    {
                        suspended.handles[suspended.count++] = thread;
                    }
                    else
                    {
                        CloseHandle(thread);
                    }
                }
            }
            complete = Thread32Next(snapshot, &entry) != FALSE;
        }

        CloseHandle(snapshot);
        return true;
    }

    void ResumeThreads(SuspendedThreads& suspended)
    {
        while (suspended.count > 0)
        {
            HANDLE thread = suspended.handles[--suspended.count];
            ResumeThread(thread);
            CloseHandle(thread);
        }
    }

    unsigned char* AllocateNear(const void* address, std::size_t size)
    {
        const std::uintptr_t location =
            reinterpret_cast<std::uintptr_t>(address);
        SYSTEM_INFO systemInfo = {};
        GetSystemInfo(&systemInfo);
        const std::uintptr_t granularity =
            static_cast<std::uintptr_t>(systemInfo.dwAllocationGranularity);
        const std::uintptr_t alignmentMask = granularity - 1;
        MEM_ADDRESS_REQUIREMENTS requirements = {};
        requirements.LowestStartingAddress = reinterpret_cast<PVOID>(
            (location - static_cast<std::uintptr_t>(MAXLONG) +
                alignmentMask) & ~alignmentMask);
        requirements.HighestEndingAddress = reinterpret_cast<PVOID>(
            ((location + static_cast<std::uintptr_t>(MAXLONG) -
                granularity) & ~alignmentMask) + alignmentMask);

        MEM_EXTENDED_PARAMETER parameter = {};
        parameter.Type = MemExtendedParameterAddressRequirements;
        parameter.Pointer = &requirements;
        const HMODULE kernelBase = GetModuleHandleW(L"KernelBase.dll");
        const auto virtualAlloc2 = kernelBase == nullptr
            ? nullptr
            : reinterpret_cast<VirtualAlloc2Action>(
                GetProcAddress(kernelBase, "VirtualAlloc2"));
        if (virtualAlloc2 == nullptr)
        {
            SetLastError(ERROR_PROC_NOT_FOUND);
            return nullptr;
        }

        return static_cast<unsigned char*>(virtualAlloc2(
            GetCurrentProcess(), nullptr, size, MEM_RESERVE | MEM_COMMIT,
            PAGE_EXECUTE_READWRITE, &parameter, 1));
    }

    HRESULT InstallMovieCaptureHook()
    {
        AcquireSRWLockExclusive(&g_decodePatchLock);
        if (InterlockedCompareExchangePointer(
                &g_originalMovieAdvance, nullptr, nullptr) != nullptr)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return BridgeSuccess;
        }
        if (!HasCompatibleEngine())
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return EngineError();
        }

        void* target = ImageBase() + MovieAdvanceRva;
        auto slot = reinterpret_cast<PVOID*>(
            ImageBase() + MovieVtableRva) + 11;
        if (*slot != target)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return EngineError();
        }

        const HRESULT pinResult = PinBridge();
        if (pinResult < 0)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return pinResult;
        }

        HRESULT result = BridgeSuccess;
        DWORD oldProtection = 0;
        if (!VirtualProtect(const_cast<PVOID*>(slot), sizeof(void*),
                PAGE_READWRITE, &oldProtection))
        {
            result = HRESULT_FROM_WIN32(GetLastError());
        }
        else
        {
            InterlockedExchangePointer(&g_originalMovieAdvance, target);
            if (InterlockedCompareExchangePointer(
                    reinterpret_cast<PVOID volatile*>(slot),
                    reinterpret_cast<PVOID>(&CapturingMovieAdvance),
                    target) != target)
            {
                InterlockedExchangePointer(
                    &g_originalMovieAdvance, nullptr);
                result = EngineError();
            }
            DWORD ignoredProtection = 0;
            VirtualProtect(const_cast<PVOID*>(slot), sizeof(void*),
                oldProtection,
                &ignoredProtection);
        }

        if (result < 0)
        {
            InterlockedExchangePointer(
                &g_originalMovieAdvance, nullptr);
        }
        ReleaseSRWLockExclusive(&g_decodePatchLock);
        return result;
    }

    HRESULT InstallScaleSkipPatch()
    {
        AcquireSRWLockExclusive(&g_decodePatchLock);
        HRESULT result = BridgeSuccess;
        if (InterlockedCompareExchangePointer(&g_decodeScaleThunk,
                nullptr, nullptr) != nullptr)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return result;
        }

        unsigned char* call = ImageBase() + DecodeScaleCallRva;
        if (!IsIndirectCallToSlot(
                DecodeScaleCallRva, DecodeScaleSlotRva))
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return EngineError();
        }

        void* originalScale = *reinterpret_cast<void**>(
            ImageBase() + DecodeScaleSlotRva);
        if (!IsReadable(originalScale, 1))
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return EngineError();
        }

        unsigned char* thunk = AllocateNear(call + 5, 64);
        if (thunk == nullptr)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(GetLastError());
        }

        // Hook sws_scale inside the decode helper, after avcodec_decode_video2.
        // Every compressed packet still rebuilds the VP9 reference state. Only
        // RGB conversion for disposable intermediate frames is skipped; the
        // requested frame and ordinary playback still call the original scaler.
        const unsigned char code[] = {
            0x48, 0xB8,                               // mov rax,video address
            0, 0, 0, 0, 0, 0, 0, 0,
            0x48, 0x3B, 0x30,                         // cmp rsi,[rax]
            0x75, 0x25,                               // jne original
            0x48, 0xB8,                               // mov rax,target address
            0, 0, 0, 0, 0, 0, 0, 0,
            0x8B, 0x00,                               // mov eax,[rax]
            0x85, 0xC0,                               // test eax,eax
            0x78, 0x15,                               // js original
            0x39, 0x46, 0x78,                         // cmp [rsi+78h],eax
            0x7D, 0x10,                               // jge original
            0x48, 0xB8,                               // mov rax,counter address
            0, 0, 0, 0, 0, 0, 0, 0,
            0xF0, 0xFF, 0x00,                         // lock inc dword ptr [rax]
            0x33, 0xC0,                               // xor eax,eax
            0xC3,                                     // ret
            0x48, 0xB8,                               // original: mov rax,scale
            0, 0, 0, 0, 0, 0, 0, 0,
            0xFF, 0xE0                                // jmp rax
        };
        std::memcpy(thunk, code, sizeof(code));
        const std::uintptr_t videoAddress =
            reinterpret_cast<std::uintptr_t>(&g_decoderCatchupVideo);
        const std::uintptr_t targetAddress =
            reinterpret_cast<std::uintptr_t>(&g_fastForwardTargetFrame);
        const std::uintptr_t skippedCounter =
            reinterpret_cast<std::uintptr_t>(&g_skippedScaleCount);
        const std::uintptr_t originalScaleAddress =
            reinterpret_cast<std::uintptr_t>(originalScale);
        std::memcpy(thunk + 2, &videoAddress, sizeof(videoAddress));
        std::memcpy(thunk + 17, &targetAddress, sizeof(targetAddress));
        std::memcpy(thunk + 38, &skippedCounter, sizeof(skippedCounter));
        std::memcpy(thunk + 54, &originalScaleAddress,
            sizeof(originalScaleAddress));
        thunk[33] = static_cast<unsigned char>(VideoCurrentFrameOffset);
        FlushInstructionCache(GetCurrentProcess(), thunk, sizeof(code));

        const std::intptr_t relative =
            reinterpret_cast<std::intptr_t>(thunk) -
            reinterpret_cast<std::intptr_t>(call + 5);
        if (relative < INT32_MIN || relative > INT32_MAX)
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        }

        SuspendedThreads suspended;
        if (!SuspendOtherThreads(suspended))
        {
            ResumeThreads(suspended);
            VirtualFree(thunk, 0, MEM_RELEASE);
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(ERROR_TOO_MANY_TCBS);
        }

        DWORD oldProtection = 0;
        if (!VirtualProtect(call, DecodeScaleCallLength,
                PAGE_EXECUTE_READWRITE, &oldProtection))
        {
            result = HRESULT_FROM_WIN32(GetLastError());
        }
        else
        {
            const std::int32_t displacement = static_cast<std::int32_t>(relative);
            call[0] = 0xE8;
            std::memcpy(call + 1, &displacement, sizeof(displacement));
            call[5] = 0x90;
            FlushInstructionCache(GetCurrentProcess(), call,
                DecodeScaleCallLength);
            DWORD ignoredProtection = 0;
            VirtualProtect(call, DecodeScaleCallLength,
                oldProtection, &ignoredProtection);
            InterlockedExchangePointer(&g_decodeScaleThunk, thunk);
        }

        ResumeThreads(suspended);
        if (result < 0)
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
        }
        ReleaseSRWLockExclusive(&g_decodePatchLock);
        return result;
    }

    HRESULT InstallDecoderWorkerTargetPatch()
    {
        AcquireSRWLockExclusive(&g_decodePatchLock);
        HRESULT result = BridgeSuccess;
        if (InterlockedCompareExchangePointer(&g_decoderWorkerTargetThunk,
                nullptr, nullptr) != nullptr)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return result;
        }

        unsigned char* load = ImageBase() + DecoderWorkerTargetLoadRva;
        if (!IsDecoderWorkerTargetLoad())
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return EngineError();
        }

        // Reproduce "mov ebp,[r14+78h]" and, for one worker iteration,
        // substitute the requested catch-up target. The worker consequently
        // labels the queue entry with the same target that decodeVideo reaches.
        const unsigned char code[] = {
            0x9C,                                     // pushfq
            0x50,                                     // push rax
            0x41, 0x8B, 0xAE, 0, 0, 0, 0,             // mov ebp,[r14+frame]
            0x48, 0xB8,                               // mov rax,video address
            0, 0, 0, 0, 0, 0, 0, 0,
            0x4C, 0x3B, 0x30,                         // cmp r14,[rax]
            0x75, 0x1B,                               // jne done
            0x48, 0xB8,                               // mov rax,target address
            0, 0, 0, 0, 0, 0, 0, 0,
            0xB9, 0xFF, 0xFF, 0xFF, 0xFF,             // mov ecx,-1
            0x87, 0x08,                               // xchg [rax],ecx
            0x85, 0xC9,                               // test ecx,ecx
            0x78, 0x06,                               // js done
            0x3B, 0xE9,                               // cmp ebp,ecx
            0x7D, 0x02,                               // jge done
            0x8B, 0xE9,                               // mov ebp,ecx
            0x49, 0x8B, 0x8E, 0, 0, 0, 0,             // mov rcx,[r14+mutex]
            0x58,                                     // pop rax
            0x9D,                                     // popfq
            0xC3                                      // done: ret
        };

        unsigned char* thunk = AllocateNear(
            load + DecoderWorkerTargetLoadLength, 64);
        if (thunk == nullptr)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(GetLastError());
        }

        std::memcpy(thunk, code, sizeof(code));
        const std::uintptr_t videoAddress =
            reinterpret_cast<std::uintptr_t>(&g_decoderCatchupVideo);
        const std::uintptr_t targetAddress =
            reinterpret_cast<std::uintptr_t>(&g_decoderCatchupTargetFrame);
        std::memcpy(thunk + 11, &videoAddress, sizeof(videoAddress));
        std::memcpy(thunk + 26, &targetAddress, sizeof(targetAddress));
        const auto frameOffset = static_cast<std::uint32_t>(
            VideoCurrentFrameOffset);
        const auto mutexOffset = static_cast<std::uint32_t>(
            VideoFrameQueueMutexOffset);
        std::memcpy(thunk + 5, &frameOffset, sizeof(frameOffset));
        std::memcpy(thunk + 54, &mutexOffset, sizeof(mutexOffset));
        FlushInstructionCache(GetCurrentProcess(), thunk, sizeof(code));

        const std::intptr_t relative =
            reinterpret_cast<std::intptr_t>(thunk) -
            reinterpret_cast<std::intptr_t>(load + 5);
        if (relative < INT32_MIN || relative > INT32_MAX)
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        }

        SuspendedThreads suspended;
        if (!SuspendOtherThreads(suspended))
        {
            ResumeThreads(suspended);
            VirtualFree(thunk, 0, MEM_RELEASE);
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(ERROR_TOO_MANY_TCBS);
        }

        DWORD oldProtection = 0;
        if (!VirtualProtect(load, DecoderWorkerTargetLoadLength,
                PAGE_EXECUTE_READWRITE, &oldProtection))
        {
            result = HRESULT_FROM_WIN32(GetLastError());
        }
        else
        {
            const std::int32_t displacement = static_cast<std::int32_t>(relative);
            load[0] = 0xE8;
            std::memcpy(load + 1, &displacement, sizeof(displacement));
            std::memset(load + 5, 0x90,
                DecoderWorkerTargetLoadLength - 5);
            FlushInstructionCache(GetCurrentProcess(), load,
                DecoderWorkerTargetLoadLength);
            DWORD ignoredProtection = 0;
            VirtualProtect(load, DecoderWorkerTargetLoadLength,
                oldProtection, &ignoredProtection);
            InterlockedExchangePointer(&g_decoderWorkerTargetThunk, thunk);
        }

        ResumeThreads(suspended);
        if (result < 0)
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
        }
        ReleaseSRWLockExclusive(&g_decodePatchLock);
        return result;
    }

    HRESULT InstallDirectCallPatch(std::uintptr_t callRva,
        std::uintptr_t targetRva, void* hook, PVOID volatile* thunkSlot,
        PVOID volatile* originalSlot)
    {
        AcquireSRWLockExclusive(&g_decodePatchLock);
        HRESULT result = BridgeSuccess;
        if (InterlockedCompareExchangePointer(
                thunkSlot, nullptr, nullptr) != nullptr)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return result;
        }

        unsigned char* call = ImageBase() + callRva;
        unsigned char* originalWrite = ImageBase() + targetRva;
        if (!IsRvaInImage(callRva, 5) ||
            !IsRvaInImage(targetRva, 16) ||
            DirectCallTarget(call) != originalWrite)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return EngineError();
        }

        unsigned char* thunk = AllocateNear(call + 5, 16);
        if (thunk == nullptr)
        {
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(GetLastError());
        }
        const unsigned char code[] = {
            0x48, 0xB8,                               // mov rax,hook
            0, 0, 0, 0, 0, 0, 0, 0,
            0xFF, 0xE0                                // jmp rax
        };
        std::memcpy(thunk, code, sizeof(code));
        const std::uintptr_t hookAddress =
            reinterpret_cast<std::uintptr_t>(hook);
        std::memcpy(thunk + 2, &hookAddress, sizeof(hookAddress));
        FlushInstructionCache(GetCurrentProcess(), thunk, sizeof(code));

        const std::intptr_t relative =
            reinterpret_cast<std::intptr_t>(thunk) -
            reinterpret_cast<std::intptr_t>(call + 5);
        if (relative < INT32_MIN || relative > INT32_MAX)
        {
            VirtualFree(thunk, 0, MEM_RELEASE);
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        }

        SuspendedThreads suspended;
        if (!SuspendOtherThreads(suspended))
        {
            ResumeThreads(suspended);
            VirtualFree(thunk, 0, MEM_RELEASE);
            ReleaseSRWLockExclusive(&g_decodePatchLock);
            return HRESULT_FROM_WIN32(ERROR_TOO_MANY_TCBS);
        }

        InterlockedExchangePointer(
            originalSlot, originalWrite);
        DWORD oldProtection = 0;
        if (!VirtualProtect(call, 5, PAGE_EXECUTE_READWRITE,
                &oldProtection))
        {
            result = HRESULT_FROM_WIN32(GetLastError());
        }
        else
        {
            const std::int32_t displacement =
                static_cast<std::int32_t>(relative);
            std::memcpy(call + 1, &displacement, sizeof(displacement));
            FlushInstructionCache(GetCurrentProcess(), call, 5);
            DWORD ignoredProtection = 0;
            VirtualProtect(call, 5, oldProtection, &ignoredProtection);
            InterlockedExchangePointer(thunkSlot, thunk);
        }

        ResumeThreads(suspended);
        if (result < 0)
        {
            InterlockedExchangePointer(
                originalSlot, nullptr);
            VirtualFree(thunk, 0, MEM_RELEASE);
        }
        ReleaseSRWLockExclusive(&g_decodePatchLock);
        return result;
    }

    HRESULT InstallAudioWritePatch()
    {
        return InstallDirectCallPatch(AudioWriteCallRva, BpkSoundWriteRva,
            reinterpret_cast<void*>(&GuardedBpkSoundWrite),
            &g_audioWriteThunk, &g_originalBpkSoundWrite);
    }

    HRESULT InstallWmvAudioWritePatch()
    {
        if ((InterlockedCompareExchange(&g_audioResolverMask, 0, 0) & 256) == 0)
        {
            return BridgeSuccess;
        }
        return InstallDirectCallPatch(
            WmvAudioWriteCallRva, BpkSoundRawWriteRva,
            reinterpret_cast<void*>(&GuardedBpkSoundRawWrite),
            &g_wmvAudioWriteThunk, &g_originalBpkSoundRawWrite);
    }

    HRESULT InstallFastDecodeHook(LONG threadCount)
    {
        InterlockedExchange(&g_fastDecodeInstallStage, 0);
        if (!HasCompatibleEngine() || !HasFastDecodeEngine())
        {
            return EngineError();
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 1);

        if (threadCount < 1 || threadCount > 64)
        {
            return E_INVALIDARG;
        }

        const HMODULE avcodec = GetModuleHandleW(L"avcodec-57.dll");
        const HMODULE avformat = GetModuleHandleW(L"avformat-57.dll");
        const HMODULE avutil = GetModuleHandleW(L"avutil-55.dll");
        if (avcodec == nullptr || avformat == nullptr || avutil == nullptr)
        {
            return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 2);

        void* expectedOpen = reinterpret_cast<void*>(
            GetProcAddress(avcodec, "avcodec_open2"));
        void* expectedSeek = reinterpret_cast<void*>(
            GetProcAddress(avformat, "av_seek_frame"));
        if (expectedOpen == nullptr ||
            expectedSeek == nullptr ||
            GetProcAddress(avcodec, "avcodec_flush_buffers") == nullptr ||
            GetProcAddress(avutil, "av_opt_set_int") == nullptr)
        {
            return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 4);

        auto slot = reinterpret_cast<PVOID volatile*>(ImageBase() + AvcodecOpenSlotRva);
        auto seekSlot = reinterpret_cast<PVOID volatile*>(
            ImageBase() + AvSeekFrameSlotRva);
        if (!IsReadable(const_cast<PVOID*>(slot), sizeof(void*)) ||
            !IsReadable(const_cast<PVOID*>(seekSlot), sizeof(void*)))
        {
            return EngineError();
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 8);

        void* current = InterlockedCompareExchangePointer(slot, nullptr, nullptr);
        void* currentSeek = InterlockedCompareExchangePointer(
            seekSlot, nullptr, nullptr);
        void* hook = reinterpret_cast<void*>(&FastAvcodecOpen2);
        void* seekHook = reinterpret_cast<void*>(&FastAvSeekFrame);
        if ((current != expectedOpen && current != hook) ||
            (currentSeek != expectedSeek && currentSeek != seekHook))
        {
            return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 16);

        // The process-local function-pointer hooks must not outlive this DLL.
        const HRESULT pinResult = PinBridge();
        if (pinResult < 0)
        {
            return pinResult;
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 32);

        const HRESULT workerPatch = InstallDecoderWorkerTargetPatch();
        if (workerPatch < 0)
        {
            return workerPatch;
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 64);

        const HRESULT scalePatch = InstallScaleSkipPatch();
        if (scalePatch < 0)
        {
            return scalePatch;
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 128);

        const HRESULT audioPatch = InstallAudioWritePatch();
        if (audioPatch < 0)
        {
            return audioPatch;
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 256);

        const HRESULT wmvAudioPatch = InstallWmvAudioWritePatch();
        if (wmvAudioPatch < 0)
        {
            return wmvAudioPatch;
        }
        InterlockedExchange(&g_fastDecodeInstallStage, 512);

        InterlockedExchange(&g_decoderThreadCount, threadCount);
        InterlockedCompareExchangePointer(&g_originalAvcodecOpen2, expectedOpen, nullptr);
        InterlockedCompareExchangePointer(
            &g_originalAvSeekFrame, expectedSeek, nullptr);
        InterlockedExchange(&g_fastDecodeInstallStage, 1024);

        if (current != hook)
        {
            DWORD oldProtection = 0;
            if (!VirtualProtect(const_cast<PVOID*>(slot), sizeof(void*),
                    PAGE_READWRITE, &oldProtection))
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }

            void* previous = InterlockedCompareExchangePointer(slot, hook, expectedOpen);
            DWORD ignoredProtection = 0;
            VirtualProtect(const_cast<PVOID*>(slot), sizeof(void*),
                oldProtection, &ignoredProtection);
            if (previous != expectedOpen && previous != hook)
            {
                return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
            }
        }

        if (currentSeek != seekHook)
        {
            DWORD oldProtection = 0;
            if (!VirtualProtect(const_cast<PVOID*>(seekSlot), sizeof(void*),
                    PAGE_READWRITE, &oldProtection))
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }

            void* previous = InterlockedCompareExchangePointer(
                seekSlot, seekHook, expectedSeek);
            DWORD ignoredProtection = 0;
            VirtualProtect(const_cast<PVOID*>(seekSlot), sizeof(void*),
                oldProtection, &ignoredProtection);
            if (previous != expectedSeek && previous != seekHook)
            {
                return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
            }
        }

        return BridgeSuccess;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayerLocked(
    SIZE_T locked)
{
    __try
    {
        return SetPlayerLocked(locked != 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayerClickThrough(
    SIZE_T enabled)
{
    __try
    {
        return SetPlayerClickThrough(enabled != 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayerWheelResize(
    SIZE_T options)
{
    __try
    {
        return SetPlayerWheelResize(options);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayerMode(
    SIZE_T mode)
{
    return SetPlayerMode(mode);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayerVolume(
    SIZE_T percent)
{
    __try
    {
        return percent <= 100
            ? SetMovieVolume(static_cast<int>(percent)) : E_INVALIDARG;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayerSizePercent(
    SIZE_T packed)
{
    __try
    {
        return SetPlayerSizePercent(packed);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayerLarge(
    SIZE_T large)
{
    __try
    {
        return SetPlayerLarge(large != 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperStartFullscreenHook()
{
    const HRESULT hook = InstallFullScreenHook();
    if (hook >= 0)
        PublishFullScreenState(true);
    return hook;
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperSetFullscreenShaderData(SIZE_T packetAddress)
{
    __try
    {
        auto packet = reinterpret_cast<FullScreenShaderDataPacket*>(
            packetAddress);
        if (!IsWritable(packet, sizeof(*packet)))
            return E_INVALIDARG;
        if (InterlockedCompareExchangePointer(
                &g_originalQOpenGLShaderProgramBind,
                nullptr, nullptr) == nullptr)
            return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        FullScreenShaderDataPacket incoming = *packet;
        for (float value : incoming.values)
            if (!_finite(value))
                return E_INVALIDARG;

        AcquireSRWLockExclusive(&g_fullScreenShaderDataLock);
        incoming.sequence = g_fullScreenShaderData.sequence >= 0x00FFFFFF
            ? 0 : g_fullScreenShaderData.sequence + 1;
        g_fullScreenShaderData = incoming;
        ReleaseSRWLockExclusive(&g_fullScreenShaderDataLock);
        *packet = incoming;
        InterlockedExchange(&g_fullScreenShaderDataSet, 1);
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperGetFullscreenShaderData(SIZE_T packetAddress)
{
    __try
    {
        auto packet = reinterpret_cast<FullScreenShaderDataPacket*>(
            packetAddress);
        if (!IsWritable(packet, sizeof(*packet)))
            return E_INVALIDARG;
        FullScreenShaderDataPacket current;
        AcquireSRWLockShared(&g_fullScreenShaderDataLock);
        current = g_fullScreenShaderData;
        ReleaseSRWLockShared(&g_fullScreenShaderDataLock);
        *packet = current;
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperSetFullscreenShaderTexture(SIZE_T packetAddress)
{
    __try
    {
        return SetFullScreenShaderTexture(packetAddress);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperGetFullscreenShaderTexture(SIZE_T packetAddress)
{
    __try
    {
        return GetFullScreenShaderTexture(packetAddress);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperFullscreenNext()
{
    return InvokeQtSingleton(g_fullScreenVtableRva, &g_fullScreenObject,
        ".?AVFullScreen@@", "FullScreen", "goToNextCard");
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperDesktopNext()
{
    __try
    {
        return InvokeDesktopNext();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperFullscreenNextSlot(SIZE_T slotId)
{
    __try
    {
        if (slotId == 0 || slotId > MAXDWORD)
            return E_INVALIDARG;
        return AdvanceFullScreenSlot(static_cast<DWORD>(slotId));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperFullscreenPlaySlot(SIZE_T requestAddress)
{
    char request[1024] = {};
    if (!CopyFullScreenRequest(requestAddress, request, sizeof(request)))
        return E_INVALIDARG;
    char* first = std::strchr(request, '\n');
    char* second = first == nullptr ? nullptr : std::strchr(first + 1, '\n');
    if (first == nullptr || second == nullptr)
        return E_INVALIDARG;
    *first = '\0';
    *second = '\0';
    char* end = nullptr;
    const unsigned long parsed = std::strtoul(request, &end, 10);
    const std::string card(first + 1);
    const std::string clip(second + 1);
    if (end == request || *end != '\0' || parsed == 0 ||
        parsed > MAXDWORD || !IsFullScreenCardRequest(card, clip))
        return E_INVALIDARG;
    return ReplaceFullScreenSlot(static_cast<DWORD>(parsed), card, clip);
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperFullscreenClearQueue()
{
    return InvokeQtSingleton(g_cardSequencerVtableRva,
        &g_cardSequencerObject, ".?AVCardSequencer@Model@@",
        "Model::CardSequencer", "clearNext");
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperFullscreenQueueInsert(SIZE_T requestAddress)
{
    std::string card;
    std::string clip;
    return ParseFullScreenQueueRequest(requestAddress, card, clip)
        ? RunFullScreenQueueOperation(FullScreenQueueOperationKind::Insert,
            card, clip) : E_INVALIDARG;
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperFullscreenQueueAdd(SIZE_T requestAddress)
{
    std::string card;
    std::string clip;
    return ParseFullScreenQueueRequest(requestAddress, card, clip)
        ? RunFullScreenQueueOperation(FullScreenQueueOperationKind::Add,
            card, clip) : E_INVALIDARG;
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperFullscreenQueueRemove(SIZE_T index)
{
    return RunFullScreenQueueOperation(
        FullScreenQueueOperationKind::Remove, {}, {}, index);
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperRefreshFullscreenState()
{
    PublishFullScreenState(true);
    return BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperDumpFullscreenTree()
{
    __try
    {
        return DumpFullScreenObjectTree();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperPrepareDressingRoomUrls()
{
    __try
    {
        DressingRoomLog("Background Dressing Room URL preparation started");
        const HRESULT requested = RequestDressingRoomUrl(1);
        if (FAILED(requested))
            return requested;
        CacheDressingRoomUrlRegion();
        return requested;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperWarmDressingRoomCache()
{
    __try
    {
        const HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
        const QtObjectFunctions qt = {
            reinterpret_cast<QObjectInherits>(core == nullptr ? nullptr :
                GetProcAddress(core, "?inherits@QObject@@QEBA_NPEBD@Z")),
            reinterpret_cast<QMetaInvoke>(core == nullptr ? nullptr :
                GetProcAddress(core,
                    "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
                    "VQGenericArgument@@222222222@Z"))
        };
        if (qt.inherits == nullptr || qt.invoke == nullptr)
            return E_NOINTERFACE;
        DressingRoomLog("Background DressingRoomData cache warm-up started");
        const int refreshed = RefreshDressingRoomDataUrls(qt);
        DressingRoomLog("Background DressingRoomData cache warm-up completed invoked=%d",
            refreshed);
        return refreshed > 0 ? BridgeSuccess :
            HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperRequestDressingRoomUrl(SIZE_T packetAddress)
{
    __try
    {
        auto packet = reinterpret_cast<const int*>(packetAddress);
        if (!IsReadable(packet, sizeof(*packet)) || *packet <= 0)
            return E_INVALIDARG;
        const char* resourceName = reinterpret_cast<const char*>(packet) + 4;
        if (!IsReadable(resourceName, 1) ||
            strnlen_s(resourceName, 8188) == 8188)
            return E_INVALIDARG;
        DressingRoomLog("Request drcId=%d resource=%s", *packet,
            resourceName);
        AcquireSRWLockExclusive(&g_dressingRoomUrlLock);
        g_selectedDressingRoomResource.assign(resourceName);
        ReleaseSRWLockExclusive(&g_dressingRoomUrlLock);
        return RequestDressingRoomUrl(*packet);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperConsumeDressingRoomUrl(SIZE_T packetAddress)
{
    __try
    {
        constexpr std::size_t capacity = 8192;
        auto packet = reinterpret_cast<char*>(packetAddress);
        if (!IsWritable(packet, capacity))
            return E_INVALIDARG;
        AcquireSRWLockExclusive(&g_dressingRoomUrlLock);
        if (g_dressingRoomUrl.empty())
        {
            char selected[256] = {};
            strncpy_s(selected, g_selectedDressingRoomResource.c_str(),
                _TRUNCATE);
            ReleaseSRWLockExclusive(&g_dressingRoomUrlLock);
            if (*selected == '\0' ||
                InterlockedCompareExchange(
                    &g_dressingRoomCacheScanPending, 0, 0) == 0)
                return HRESULT_FROM_WIN32(ERROR_NOT_READY);
            const ULONGLONG now = GetTickCount64();
            const ULONGLONG lastScanStarted = static_cast<ULONGLONG>(
                InterlockedCompareExchange64(
                    &g_dressingRoomLastScanStarted, 0, 0));
            if (lastScanStarted != 0 && now - lastScanStarted < 500)
                return HRESULT_FROM_WIN32(ERROR_NOT_READY);
            InterlockedExchange64(&g_dressingRoomLastScanStarted,
                static_cast<LONG64>(now));
            DressingRoomLog("URL scan triggered immediately");
            if (!FindCachedDressingRoomUrl(selected))
                return HRESULT_FROM_WIN32(ERROR_NOT_READY);
            InterlockedExchange(&g_dressingRoomCacheScanPending, 0);
            AcquireSRWLockExclusive(&g_dressingRoomUrlLock);
        }
        const std::size_t count = (std::min)(capacity - 1,
            g_dressingRoomUrl.size());
        std::memcpy(packet, g_dressingRoomUrl.data(), count);
        packet[count] = '\0';
        g_dressingRoomUrl.clear();
        ReleaseSRWLockExclusive(&g_dressingRoomUrlLock);
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperPlaybackBridgeVersion()
{
    HasCompatibleEngine();
    HasFastForwardEngine();
    return 94;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetCompatibilityMask()
{
    __try
    {
        HasCompatibleEngine();
        return InterlockedCompareExchange(&g_compatibilityMask, -1, -1);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetOffsetResolverMask()
{
    EnsureEngineOffsets();
    return InterlockedCompareExchange(&g_offsetResolverMask, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetMovieResolverMask()
{
    EnsureEngineOffsets();
    return InterlockedCompareExchange(&g_movieResolverMask, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetAudioResolverMask()
{
    EnsureEngineOffsets();
    return InterlockedCompareExchange(&g_audioResolverMask, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetAudioResetCount()
{
    return InterlockedCompareExchange(&g_audioResetCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetAudioSuspendCount()
{
    return InterlockedCompareExchange(&g_audioSuspendCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetAudioResumeCount()
{
    return InterlockedCompareExchange(&g_audioResumeCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetAudioWritesSkipped()
{
    return InterlockedCompareExchange(&g_audioWritesSkipped, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeResolverMask()
{
    ResolveFastDecodeOffsets();
    return InterlockedCompareExchange(&g_fastDecodeResolverMask, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeCompatibilityMask()
{
    int mask = 0;
    mask |= ResolveFastDecodeOffsets() ? 1 : 0;
    mask |= IsRvaInImage(AvcodecOpenSlotRva, sizeof(void*)) ? 2 : 0;
    mask |= IsRvaInImage(AvSeekFrameSlotRva, sizeof(void*)) ? 4 : 0;
    mask |= IsRvaInImage(DecodeScaleSlotRva, sizeof(void*)) ? 8 : 0;
    mask |= IsIndirectCallToSlot(
        DecodeScaleCallRva, DecodeScaleSlotRva) ? 16 : 0;
    mask |= IsDecoderWorkerTargetLoad() ? 32 : 0;
    return mask;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeInstallStage()
{
    return InterlockedCompareExchange(&g_fastDecodeInstallStage, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetMovieManager(SIZE_T managerAddress)
{
    UNREFERENCED_PARAMETER(managerAddress);
    return E_NOTIMPL;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperInstallMovieCaptureHook()
{
    HRESULT result = InstallMovieCaptureHook();
    if (FAILED(result))
    {
        return result;
    }
    result = InstallAudioWritePatch();
    return FAILED(result) ? result : InstallWmvAudioWritePatch();
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperResetPlaybackSession()
{
    InterlockedExchange(&g_decoderCatchupTargetFrame, -1);
    InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
    InterlockedExchange(&g_fastForwardTargetFrame, -1);
    InterlockedExchangePointer(&g_fastForwardMovie, nullptr);

    void* movie = ActiveMovie();
    HRESULT result = E_UNEXPECTED;
    __try
    {
        result = ResetWmvPlaybackSession(movie);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        result = E_UNEXPECTED;
    }
    ResetPlaybackAudioSession();

    InterlockedExchange(&g_movieCaptureArmed, 0);
    InterlockedExchange(&g_movieCaptureReady, 0);
    InterlockedExchangePointer(&g_activeMovie, nullptr);
    InterlockedExchangePointer(&g_activeAnimation, nullptr);
    InterlockedExchangePointer(&g_consumedMovie, nullptr);
    InterlockedExchangePointer(&g_consumedAnimation, nullptr);
    InterlockedExchangePointer(&g_consumedSsv, nullptr);
    InterlockedExchangePointer(&g_consumedInfo, nullptr);
    ClearAlphaCheckpoints();
    return FAILED(result) ? result : BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperArmMovieCapture()
{
    if (InterlockedCompareExchangePointer(
            &g_originalMovieAdvance, nullptr, nullptr) == nullptr)
    {
        return EngineError();
    }
    void* movie = ActiveMovie();
    const int frame = movie != nullptr
        ? *reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(movie) +
                MovieCurrentFrameOffset)
        : -1;
    InterlockedExchange(&g_movieCaptureArmedFrame, frame);
    InterlockedExchange(&g_movieCaptureArmed, 1);
    return BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperConsumeCapturedMovie()
{
    if (InterlockedExchange(&g_movieCaptureReady, 0) == 0)
    {
        return ManagerError();
    }
    InterlockedExchange(&g_movieCaptureArmed, 0);
    void* movie = ActiveMovie();
    if (!IsActiveMovieCandidate(movie))
    {
        return ManagerError();
    }
    MarkMovieConsumed(movie);
    return BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperCancelMovieCapture()
{
    InterlockedExchange(&g_movieCaptureArmed, 0);
    InterlockedExchange(&g_movieCaptureReady, 0);
    return BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetMovie(SIZE_T movieAddress)
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = reinterpret_cast<void*>(movieAddress);
        if (!IsActiveMovieCandidate(movie))
        {
            return E_INVALIDARG;
        }
        StoreActiveMovie(movie);
        MarkMovieConsumed(movie);
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperDiscoverMovie()
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = DiscoverActiveMovie();
        if (movie == nullptr)
        {
            return ManagerError();
        }

        StoreActiveMovie(movie);
        MarkMovieConsumed(movie);
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperPause()
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = ActiveMovie();
        if (movie == nullptr ||
            !HasSignature(MoviePauseRva, MoviePauseSignature,
                sizeof(MoviePauseSignature)))
        {
            return ManagerError();
        }
        // Stop the visible frame first. QAudioOutput::suspend can block while
        // its device drains, which previously made pause look sluggish.
        FunctionAt<ManagerAction>(MoviePauseRva)(movie);
        PauseMovieAudio(movie);

        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperResume()
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = ActiveMovie();
        if (movie == nullptr ||
            !HasSignature(MovieResumeRva, MovieResumeSignature,
                sizeof(MovieResumeSignature)))
        {
            return ManagerError();
        }
        FunctionAt<ManagerAction>(MovieResumeRva)(movie);
        ResumeMovieAudio(movie);

        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetPlayRate(SIZE_T rateBits)
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = ActiveMovie();
        if (movie == nullptr ||
            !HasSignature(MovieSetPlayRateRva,
                MovieSetPlayRateSignature,
                sizeof(MovieSetPlayRateSignature)))
        {
            return ManagerError();
        }

        static_assert(sizeof(double) == sizeof(rateBits), "Unexpected SIZE_T width");
        double playRate = 1.0;
        std::memcpy(&playRate, &rateBits, sizeof(playRate));
        if (!_finite(playRate) || playRate < 0.01 || playRate > 100.0)
        {
            return E_INVALIDARG;
        }

        void* animation = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(movie) + MovieAnimationOffset);
        void* video = AnimationVideoDecoder(animation);
        if (IsWmvDecoder(video))
        {
            if (playRate > 3.001)
            {
                return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            }
            AcquireSRWLockExclusive(&g_wmvRestartLock);
            HRESULT result = E_FAIL;
            __try
            {
                const bool sameDecoder =
                    animation == g_wmvRateAnimation &&
                    video == g_wmvRateVideo;
                const double previousRate =
                    sameDecoder ? g_wmvRate : 1.0;
                const bool previousUserClock =
                    sameDecoder && g_wmvUserClock;
                if (playRate == previousRate || previousUserClock)
                {
                    FunctionAt<ManagerSetPlayRate>(
                        MovieSetPlayRateRva)(movie, playRate);
                    g_wmvRateAnimation = animation;
                    g_wmvRateVideo = video;
                    g_wmvRate = playRate;
                    g_wmvUserClock = previousUserClock;
                    if (previousUserClock &&
                        IsNormalPlaybackRate(playRate) &&
                        !IsNormalPlaybackRate(previousRate))
                    {
                        auto currentAddress = reinterpret_cast<const int*>(
                            reinterpret_cast<unsigned char*>(movie) +
                                MovieCurrentFrameOffset);
                        if (!IsReadable(currentAddress,
                                sizeof(*currentAddress)) ||
                            InterlockedCompareExchangePointer(
                                &g_wmvClockMovie, nullptr, nullptr) != movie)
                        {
                            result = ManagerError();
                        }
                        else
                        {
                            InterlockedExchange(
                                &g_wmvClockReleaseFrame, *currentAddress);
                            InterlockedExchange(
                                &g_wmvClockStreaming, 0);
                            InterlockedExchange(
                                &g_wmvClockResult, E_PENDING);
                            result = BridgeSuccess;
                        }
                    }
                    else
                    {
                        result = BridgeSuccess;
                    }
                }
                else
                {
                    void* info = IsReadable(animation,
                        AnimationInfoOffset + sizeof(void*))
                        ? *reinterpret_cast<void**>(
                            reinterpret_cast<unsigned char*>(animation) +
                                AnimationInfoOffset)
                        : nullptr;
                    auto currentAddress = reinterpret_cast<int*>(
                        reinterpret_cast<unsigned char*>(movie) +
                            MovieCurrentFrameOffset);
                    auto stateAddress = reinterpret_cast<int*>(
                        reinterpret_cast<unsigned char*>(movie) +
                            MovieStateOffset);
                    if (!IsReadable(info,
                            AnimationFramesPerSecondOffset + sizeof(int)) ||
                        !IsWritable(currentAddress, sizeof(int)) ||
                        !IsReadable(stateAddress, sizeof(int)))
                    {
                        result = ManagerError();
                    }
                    else
                    {
                        const int totalFrames = *reinterpret_cast<const int*>(
                            reinterpret_cast<unsigned char*>(info) +
                                AnimationTotalFramesOffset);
                        const int framesPerSecond =
                            *reinterpret_cast<const int*>(
                                reinterpret_cast<unsigned char*>(info) +
                                    AnimationFramesPerSecondOffset);
                        const int currentFrame = *currentAddress;
                        if (totalFrames <= 0 || framesPerSecond <= 0 ||
                            currentFrame < 0)
                        {
                            result = E_INVALIDARG;
                        }
                        else
                        {
                            const bool wasPlaying =
                                *stateAddress == PlayingState;
                            if (wasPlaying)
                            {
                                FunctionAt<ManagerAction>(
                                    MoviePauseRva)(movie);
                                ControlMovieAudio(
                                    movie, AudioCommand::Suspend);
                            }

                            const int firstFrame = currentFrame + 1 < totalFrames
                                ? currentFrame + 1
                                : totalFrames - 1;
                            const int releaseFrame =
                                currentFrame + 2 < totalFrames
                                    ? currentFrame + 2
                                    : totalFrames - 1;
                            const int deliveredUntilFrame =
                                releaseFrame + 1;
                            ClearWmvClock();
                            result = RestartWmvReader(video, 0,
                                deliveredUntilFrame, framesPerSecond,
                                totalFrames, true);
                            if (SUCCEEDED(result))
                            {
                                result = ArmWmvClock(movie, video,
                                    framesPerSecond, totalFrames,
                                    deliveredUntilFrame, releaseFrame);
                            }
                            if (SUCCEEDED(result))
                            {
                                FunctionAt<ManagerSetPlayRate>(
                                    MovieSetPlayRateRva)(movie, playRate);
                                g_wmvRateAnimation = animation;
                                g_wmvRateVideo = video;
                                g_wmvRate = playRate;
                                g_wmvUserClock = true;
                                result = BridgeSuccess;
                            }
                            else if (SUCCEEDED(RestartWmvReader(video,
                                    firstFrame, firstFrame + 1,
                                    framesPerSecond, totalFrames, false)))
                            {
                                ClearWmvClock();
                                FunctionAt<ManagerSetPlayRate>(
                                    MovieSetPlayRateRva)(movie, previousRate);
                                g_wmvRateAnimation = animation;
                                g_wmvRateVideo = video;
                                g_wmvRate = previousRate;
                                g_wmvUserClock = false;
                            }

                            if (wasPlaying)
                            {
                                FunctionAt<ManagerAction>(
                                    MovieResumeRva)(movie);
                            }
                        }
                    }
                }
                if (SUCCEEDED(result))
                {
                    SetMovieAudioRate(movie, playRate);
                }
            }
            __finally
            {
                ReleaseSRWLockExclusive(&g_wmvRestartLock);
            }
            return result;
        }

        FunctionAt<ManagerSetPlayRate>(MovieSetPlayRateRva)(movie, playRate);
        SetMovieAudioRate(movie, playRate);
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetElapsedMilliseconds()
{
    __try
    {
        return TimelineResult(false);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetTotalMilliseconds()
{
    __try
    {
        return TimelineResult(true);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetState()
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = ActiveMovie();
        if (movie == nullptr)
        {
            return ManagerError();
        }

        const auto stateAddress = reinterpret_cast<unsigned char*>(movie) + MovieStateOffset;
        if (!IsReadable(stateAddress, sizeof(int)))
        {
            return ManagerError();
        }
        return *reinterpret_cast<const int*>(stateAddress);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperEnableFastDecode(SIZE_T threadCount)
{
    __try
    {
        if (threadCount > 64)
        {
            return E_INVALIDARG;
        }

        return InstallFastDecodeHook(static_cast<LONG>(threadCount));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeOpenCount()
{
    return InterlockedCompareExchange(&g_decoderOpenCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeOptionResult()
{
    return InterlockedCompareExchange(&g_lastThreadOptionResult, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeSkippedScaleCount()
{
    return InterlockedCompareExchange(&g_skippedScaleCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeCatchupDistance()
{
    return InterlockedCompareExchange(&g_lastDecoderCatchupDistance, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastDecodeDroppedFrameCount()
{
    return InterlockedCompareExchange(&g_lastDroppedVideoFrames, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetCodecFlushCount()
{
    return InterlockedCompareExchange(&g_codecFlushCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetAlphaResetCount()
{
    return InterlockedCompareExchange(&g_alphaResetCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetLastAlphaFrameBeforeReset()
{
    return InterlockedCompareExchange(&g_lastAlphaFrameBeforeReset, -1, -1);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetAlphaCheckpointRestoreCount()
{
    return InterlockedCompareExchange(&g_alphaCheckpointRestoreCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetLastAlphaCheckpointFrame()
{
    return InterlockedCompareExchange(&g_lastAlphaCheckpointFrame, -1, -1);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperClearAlphaCheckpoints()
{
    ClearAlphaCheckpoints();
    return BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperSetAlphaCheckpointCacheKey(SIZE_T clipKey)
{
    InterlockedExchange64(&g_alphaCheckpointClipKey,
        static_cast<LONGLONG>(clipKey));
    return BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperSetAlphaCheckpointCacheLimitBytes(SIZE_T cacheBytes)
{
    constexpr SIZE_T minimum = 64ULL * 1024 * 1024;
    constexpr SIZE_T maximum = 64ULL * 1024 * 1024 * 1024;
    if (cacheBytes < minimum || cacheBytes > maximum)
    {
        return E_INVALIDARG;
    }

    InterlockedExchange64(&g_persistentAlphaCacheLimit,
        static_cast<LONGLONG>(cacheBytes));
    wchar_t directory[MAX_PATH] = {};
    if (AlphaCacheDirectory(directory))
    {
        PrunePersistentAlphaCache(directory);
    }
    return BridgeSuccess;
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperCaptureAlphaCheckpoint()
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* movie = ActiveMovie();
        const HMODULE qtCore = GetModuleHandleW(L"Qt5Core.dll");
        const auto tryLockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexTryLock>(GetProcAddress(
                qtCore, "?tryLock@QMutex@@QEAA_NH@Z"));
        const auto unlockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexAction>(GetProcAddress(
                qtCore, "?unlock@QMutex@@QEAAXXZ"));
        if (movie == nullptr || tryLockMutex == nullptr ||
            unlockMutex == nullptr)
        {
            return ManagerError();
        }
        if (!IsSeekReady(movie))
        {
            return ManagerError();
        }

        void* mutex = reinterpret_cast<unsigned char*>(movie) + MovieMutexOffset;
        bool mutexLocked = false;
        HRESULT result = E_FAIL;
        __try
        {
            if (!TryLockMovieMutex(mutex, tryLockMutex, 250))
            {
                result = HRESULT_FROM_WIN32(ERROR_BUSY);
                __leave;
            }
            mutexLocked = true;
            void* animation = *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(movie) +
                    MovieAnimationOffset);
            void* info = IsReadable(animation,
                AnimationInfoOffset + sizeof(void*))
                ? *reinterpret_cast<void**>(
                    reinterpret_cast<unsigned char*>(animation) +
                        AnimationInfoOffset)
                : nullptr;
            if (!IsReadable(info,
                    AnimationFramesPerSecondOffset + sizeof(int)))
            {
                result = ManagerError();
            }
            else
            {
                const int frame = *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(movie) +
                        MovieCurrentFrameOffset);
                const int framesPerSecond = *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(info) +
                        AnimationFramesPerSecondOffset);
                const int count = CaptureAlphaCheckpoint(
                    animation, frame, framesPerSecond);
                result = count < 0 ? E_FAIL : count;
            }
        }
        __finally
        {
            if (mutexLocked)
            {
                unlockMutex(mutex);
            }
        }
        return result;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetKeyframeSeekCount()
{
    return InterlockedCompareExchange(&g_keyframeSeekCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetLastKeyframeSeekFrame()
{
    return InterlockedCompareExchange(&g_lastKeyframeSeekFrame, -1, -1);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetDecoderKind()
{
    __try
    {
        void* movie = ActiveMovie();
        if (movie == nullptr)
        {
            return 0;
        }

        void* animation = *reinterpret_cast<void**>(
            reinterpret_cast<unsigned char*>(movie) + MovieAnimationOffset);
        void* video = AnimationVideoDecoder(animation);
        if (video == nullptr)
        {
            return 0;
        }

        void* vtable = *reinterpret_cast<void**>(video);
        if (vtable == ImageBase() + VideoFfmpegVtableRva)
        {
            return 1;
        }
        return IsWmvDecoder(video) ? 2 : 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperIsSeekReady()
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }
        return IsSeekReady(ActiveMovie()) ? BridgeSuccess : S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetSeekReadinessMask()
{
    __try
    {
        void* movie = ActiveMovie();
        if (!HasFastForwardEngine() || !IsActiveMovieCandidate(movie))
        {
            return 0;
        }

        int mask = 1;
        auto movieBytes = reinterpret_cast<unsigned char*>(movie);
        void* animation = *reinterpret_cast<void**>(
            movieBytes + MovieAnimationOffset);
        if (!IsReadable(animation, AnimationSsvOffset + sizeof(void*)))
        {
            return mask;
        }
        mask |= 2;

        void* video = VideoDecoder(animation);
        if (video == nullptr)
        {
            return mask;
        }
        mask |= 4;
        if (!CanResetAnimationAlpha(animation))
        {
            return mask;
        }
        mask |= 8;

        auto videoBytes = reinterpret_cast<unsigned char*>(video);
        void* formatContext = *reinterpret_cast<void**>(
            videoBytes + VideoFormatContextOffset);
        void* queue = *reinterpret_cast<void**>(
            videoBytes + VideoFrameQueueOffset);
        void* queueMutex = *reinterpret_cast<void**>(
            videoBytes + VideoFrameQueueMutexOffset);
        if (IsReadable(formatContext, 0x38))
        {
            mask |= 0x10;
        }
        if (IsReadable(queue, QueueEntriesOffset))
        {
            mask |= 0x20;
        }
        if (queueMutex != nullptr)
        {
            mask |= 0x40;
        }

        auto animationBytes =
            reinterpret_cast<unsigned char*>(animation);
        void* ssv = *reinterpret_cast<void**>(
            animationBytes + AnimationSsvOffset);
        void* info = *reinterpret_cast<void**>(
            animationBytes + AnimationInfoOffset);
        void* output = *reinterpret_cast<void**>(
            animationBytes + AnimationAlphaOutputOffset);
        AcquireSRWLockShared(&g_seekReadinessLock);
        const bool alphaReady =
            g_seekReadinessAnimation == animation &&
            g_seekReadinessSsv == ssv &&
            g_seekReadinessInfo == info &&
            g_seekReadinessOutput == output &&
            g_seekReadinessAlphaProgress >=
                RequiredAlphaProgressObservations;
        ReleaseSRWLockShared(&g_seekReadinessLock);
        if (alphaReady)
        {
            mask |= 0x80;
        }

        const int movieFrame = *reinterpret_cast<const int*>(
            movieBytes + MovieCurrentFrameOffset);
        const int decoderFrame = *reinterpret_cast<const int*>(
            videoBytes + VideoCurrentFrameOffset);
        if (DecoderFramesReady(movieFrame, decoderFrame))
        {
            mask |= 0x100;
        }
        return mask;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperPrepareFastForwardMilliseconds(
    SIZE_T targetMilliseconds)
{
    __try
    {
        if (!HasCompatibleEngine() || !HasFastForwardEngine())
        {
            return EngineError();
        }

        void* movie = ActiveMovie();
        if (movie == nullptr)
        {
            return ManagerError();
        }

        const auto stateAddress = reinterpret_cast<unsigned char*>(movie) + MovieStateOffset;
        if (!IsReadable(stateAddress, sizeof(int)) ||
            *reinterpret_cast<const int*>(stateAddress) != PausedState)
        {
            return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        }

        const HMODULE qtCore = GetModuleHandleW(L"Qt5Core.dll");
        if (qtCore == nullptr)
        {
            return EngineError();
        }

        const auto lockMutex = reinterpret_cast<MutexAction>(GetProcAddress(
            qtCore, "?lock@QMutex@@QEAAXXZ"));
        const auto tryLockMutex = reinterpret_cast<MutexTryLock>(GetProcAddress(
            qtCore, "?tryLock@QMutex@@QEAA_NH@Z"));
        const auto unlockMutex = reinterpret_cast<MutexAction>(GetProcAddress(
            qtCore, "?unlock@QMutex@@QEAAXXZ"));
        if (lockMutex == nullptr || tryLockMutex == nullptr ||
            unlockMutex == nullptr)
        {
            return EngineError();
        }

        void* mutex = reinterpret_cast<unsigned char*>(movie) + MovieMutexOffset;
        bool mutexLocked = false;
        HRESULT result = E_FAIL;
        __try
        {
            if (!TryLockMovieMutex(mutex, tryLockMutex, 10'000))
            {
                result = HRESULT_FROM_WIN32(ERROR_BUSY);
                __leave;
            }
            mutexLocked = true;
            InterlockedExchange(&g_decoderCatchupTargetFrame, -1);
            InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
            InterlockedExchange(&g_fastForwardTargetFrame, -1);
            InterlockedExchangePointer(&g_fastForwardMovie, nullptr);

            void* animation = *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(movie) + MovieAnimationOffset);
            if (!IsReadable(animation, AnimationInfoOffset + sizeof(void*)))
            {
                result = ManagerError();
            }
            else
            {
                void* info = *reinterpret_cast<void**>(
                    reinterpret_cast<unsigned char*>(animation) + AnimationInfoOffset);
                const auto currentAddress = reinterpret_cast<int*>(
                    reinterpret_cast<unsigned char*>(movie) + MovieCurrentFrameOffset);
                if (!IsReadable(info, AnimationFramesPerSecondOffset + sizeof(int)) ||
                    !IsReadable(currentAddress, sizeof(int)))
                {
                    result = ManagerError();
                }
                else
                {
                    const int totalFrames = *reinterpret_cast<const int*>(
                        reinterpret_cast<unsigned char*>(info) + AnimationTotalFramesOffset);
                    const int framesPerSecond = *reinterpret_cast<const int*>(
                        reinterpret_cast<unsigned char*>(info) +
                        AnimationFramesPerSecondOffset);
                    const int currentFrame = *currentAddress;
                    if (totalFrames <= 0 || framesPerSecond <= 0 ||
                        targetMilliseconds > static_cast<SIZE_T>(MAXLONG) * 1000)
                    {
                        result = E_INVALIDARG;
                    }
                    else
                    {
                        const std::uint64_t requestedFrame =
                            (static_cast<std::uint64_t>(targetMilliseconds) *
                                static_cast<std::uint64_t>(framesPerSecond) + 999) / 1000;
                        const int lastSeekableFrame = totalFrames > framesPerSecond
                            ? totalFrames - framesPerSecond
                            : totalFrames - 1;
                        const int targetFrame = static_cast<int>(
                            requestedFrame >= static_cast<std::uint64_t>(
                                lastSeekableFrame)
                                ? lastSeekableFrame
                                : requestedFrame);
                        if (targetFrame == currentFrame)
                        {
                            result = E_INVALIDARG;
                        }
                        else
                        {
                            void* anyVideo = AnimationVideoDecoder(animation);
                            const bool isWmv = IsWmvDecoder(anyVideo);
                            const bool hasFfmpegAcceleration =
                                InterlockedCompareExchangePointer(
                                    &g_decodeScaleThunk,
                                    nullptr, nullptr) != nullptr &&
                                InterlockedCompareExchangePointer(
                                    &g_decoderWorkerTargetThunk,
                                    nullptr, nullptr) != nullptr;
                            if (!isWmv && !hasFfmpegAcceleration)
                            {
                                result = EngineError();
                            }
                            else if (isWmv)
                            {
                                const int releaseFrame =
                                    targetFrame + 2 < totalFrames
                                        ? targetFrame + 2
                                        : totalFrames - 1;
                                const int deliveredUntilFrame =
                                    releaseFrame + 1;
                                BeginMovieAudioSeek(movie);
                                AcquireSRWLockExclusive(
                                    &g_wmvRestartLock);
                                __try
                                {
                                    ClearWmvClock();
                                    result = RestartWmvReader(anyVideo,
                                        0, deliveredUntilFrame,
                                        framesPerSecond, totalFrames, true);
                                    if (SUCCEEDED(result))
                                    {
                                        *currentAddress = targetFrame - 1;
                                        result = ArmWmvClock(movie, anyVideo,
                                            framesPerSecond, totalFrames,
                                            deliveredUntilFrame,
                                            releaseFrame);
                                    }
                                }
                                __finally
                                {
                                    ReleaseSRWLockExclusive(
                                        &g_wmvRestartLock);
                                }
                                if (SUCCEEDED(result))
                                {
                                    g_wmvRateAnimation = animation;
                                    g_wmvRateVideo = anyVideo;
                                    g_wmvRate = 1.0;
                                    g_wmvUserClock = true;
                                    InterlockedExchange(
                                        &g_fastForwardTargetFrame,
                                        targetFrame);
                                    InterlockedExchangePointer(
                                        &g_fastForwardMovie, movie);
                                    result = BridgeSuccess;
                                }
                                else
                                {
                                    *currentAddress = currentFrame;
                                    CancelMovieAudioSeek(movie);
                                }
                            }
                            else if (!CanResetAnimationAlpha(animation))
                            {
                                result = E_FAIL;
                            }
                            else
                            {
                                void* video = VideoDecoder(animation);
                                if (video == nullptr)
                                {
                                    result = EngineError();
                                }
                                else
                                {
                                    BeginMovieAudioSeek(movie);
                                    auto decoderFrameAddress = reinterpret_cast<int*>(
                                        reinterpret_cast<unsigned char*>(video) +
                                        VideoCurrentFrameOffset);
                                    int decoderFrame = *decoderFrameAddress;
                                    bool decoderReady = true;
                                    int streamIndex = -1;
                                    int keyframeFrame = -1;
                                    std::int64_t keyframeTimestamp = 0;
                                    const bool hasKeyframe = FindVideoKeyframe(
                                        *reinterpret_cast<void**>(
                                            reinterpret_cast<unsigned char*>(video) +
                                            VideoFormatContextOffset),
                                        targetFrame, totalFrames, streamIndex,
                                        keyframeFrame, keyframeTimestamp);
                                    const bool useKeyframe = hasKeyframe &&
                                        (targetFrame < decoderFrame ||
                                            keyframeFrame > decoderFrame + 30);
                                    if (useKeyframe)
                                    {
                                        decoderReady = SeekVideoToKeyframe(video,
                                            streamIndex, keyframeFrame,
                                            keyframeTimestamp);
                                        if (decoderReady)
                                        {
                                            decoderFrame = *decoderFrameAddress;
                                        }
                                    }

                                    if (targetFrame < decoderFrame)
                                    {
                                        const auto seek = FunctionAt<VideoSeek>(VideoSeekRva);
                                        if (!seek(video, 0))
                                        {
                                            result = E_FAIL;
                                            decoderReady = false;
                                        }
                                        else
                                        {
                                            decoderFrame = *decoderFrameAddress;
                                            decoderReady = true;
                                        }
                                    }
                                    else if (!decoderReady)
                                    {
                                        // A failed forward keyframe probe leaves the
                                        // existing sequential decoder state usable.
                                        decoderReady = true;
                                    }

                                    if (decoderReady)
                                    {
                                        int alphaStartFrame = -1;
                                        const bool restoredCheckpoint =
                                            RestoreAlphaCheckpoint(animation,
                                                targetFrame, alphaStartFrame);
                                        if (!restoredCheckpoint &&
                                            !ResetAnimationAlpha(animation))
                                        {
                                            result = E_FAIL;
                                        }
                                        else
                                        {
                                            int droppedFrames = 0;
                                            // Publish the persistent conversion target
                                            // before waking the independent decoder
                                            // worker through ArmDecoderCatchup.
                                            InterlockedExchange(
                                                &g_fastForwardTargetFrame, targetFrame);
                                            if (targetFrame < decoderFrame ||
                                                !ArmDecoderCatchup(video, targetFrame,
                                                    lockMutex, unlockMutex, droppedFrames))
                                            {
                                                InterlockedExchange(
                                                    &g_fastForwardTargetFrame, -1);
                                                result = E_FAIL;
                                            }
                                            else
                                            {
                                                // Movie::advance asks CAnim for
                                                // currentFrame + 1. CAnim continues
                                                // from the restored checkpoint, or
                                                // frame zero when none was available,
                                                // before Movie publishes the target.
                                                *currentAddress = targetFrame - 1;
                                                InterlockedExchange(
                                                    &g_lastDecoderCatchupDistance,
                                                    targetFrame - decoderFrame);
                                                InterlockedExchange(
                                                    &g_lastDroppedVideoFrames,
                                                    droppedFrames);
                                                InterlockedExchangePointer(
                                                    &g_fastForwardMovie, movie);
                                                result = BridgeSuccess;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        __finally
        {
            if (mutexLocked)
            {
                unlockMutex(mutex);
            }
        }
        if (result != BridgeSuccess &&
            InterlockedCompareExchangePointer(
                &g_audioSeekMovie, nullptr, nullptr) == movie)
        {
            CancelMovieAudioSeek(movie);
        }
        return result;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        InterlockedExchange(&g_decoderCatchupTargetFrame, -1);
        InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
        InterlockedExchange(&g_fastForwardTargetFrame, -1);
        InterlockedExchangePointer(&g_fastForwardMovie, nullptr);
        InterlockedExchangePointer(&g_audioSeekMovie, nullptr);
        InterlockedExchangePointer(&g_audioSuppressedSound, nullptr);
        InterlockedExchangePointer(&g_audioResetPendingSound, nullptr);
        InterlockedExchangePointer(
            &g_audioReleaseAfterResetSound, nullptr);
        InterlockedExchange(&g_audioResumeAfterReset, 0);
        return E_UNEXPECTED;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetFastForwardStatus()
{
    __try
    {
        const LONG targetFrame = InterlockedCompareExchange(
            &g_fastForwardTargetFrame, -1, -1);
        void* pendingMovie = InterlockedCompareExchangePointer(
            &g_fastForwardMovie, nullptr, nullptr);
        if (targetFrame < 0 || pendingMovie == nullptr)
        {
            return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        }

        void* movie = ActiveMovie();
        if (movie == nullptr || movie != pendingMovie)
        {
            InterlockedExchange(&g_decoderCatchupTargetFrame, -1);
            InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
            InterlockedExchange(&g_fastForwardTargetFrame, -1);
            InterlockedExchangePointer(&g_fastForwardMovie, nullptr);
            CancelMovieAudioSeek(pendingMovie);
            return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        }

        const auto currentAddress = reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(movie) + MovieCurrentFrameOffset);
        if (!IsReadable(currentAddress, sizeof(int)))
        {
            return ManagerError();
        }

        int completionFrame = targetFrame;
        void* animation = IsReadable(movie,
            MovieAnimationOffset + sizeof(void*))
            ? *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(movie) +
                    MovieAnimationOffset)
            : nullptr;
        const bool isWmv =
            IsWmvDecoder(AnimationVideoDecoder(animation));
        if (isWmv)
        {
            const HRESULT clockStatus = WmvClockStatus(movie);
            if (clockStatus != E_PENDING && FAILED(clockStatus))
            {
                CancelMovieAudioSeek(movie);
                return clockStatus;
            }
            if (clockStatus != BridgeSuccess)
            {
                return S_OK;
            }
        }
        if (isWmv && targetFrame <= MAXLONG - 2)
        {
            // Reaching the relabelled target only proves the first queued
            // colour frame was displayed. Two subsequent Movie advances prove
            // that the restarted reader and alpha consumer are both moving
            // before WinForms enables the seek controls again.
            completionFrame += 2;
        }

        if (*currentAddress < completionFrame)
        {
            return S_OK;
        }

        InterlockedExchange(&g_fastForwardTargetFrame, -1);
        InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
        InterlockedExchangePointer(&g_fastForwardMovie, nullptr);
        CompleteMovieAudioSeek(movie);
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_bridgeModule = instance;
        DisableThreadLibraryCalls(instance);
        HANDLE commandThread = CreateThread(
            nullptr, 0, &StartBridgeCommandServer, instance, 0, nullptr);
        if (commandThread)
            CloseHandle(commandThread);
    }
    return TRUE;
}
