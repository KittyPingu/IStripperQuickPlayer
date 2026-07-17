#include <Windows.h>
#include <TlHelp32.h>
#include <cstdint>
#include <cstring>
#include <float.h>

// This bridge is intentionally tied to vghd.exe 2.4.0.0.  These are private
// functions and fields discovered in that build; every callable RVA is checked
// before it is used so a future iStripper update fails closed instead of jumping
// into an unknown instruction stream.
namespace
{
    constexpr std::uintptr_t PlayRva = 0x280CF0;
    constexpr std::uintptr_t PauseRva = 0x280D10;
    constexpr std::uintptr_t ResumeRva = 0x280D30;
    constexpr std::uintptr_t ElapsedRva = 0x280E00;
    constexpr std::uintptr_t DurationRva = 0x280E20;
    constexpr std::uintptr_t SetPlayRateRva = 0x280F90;
    constexpr std::uintptr_t AnimationFrameRva = 0x272780;
    constexpr std::uintptr_t MovieAdvanceRva = 0x27DA20;
    constexpr std::uintptr_t MoviePauseRva = 0x27C7D0;
    constexpr std::uintptr_t MovieResumeRva = 0x27C840;
    constexpr std::uintptr_t MovieSetPlayRateRva = 0x27D720;
    constexpr std::uintptr_t AvcodecOpenCallRva = 0x286805;
    constexpr std::uintptr_t AvcodecOpenSlotRva = 0x726770;
    constexpr std::uintptr_t AvSeekFrameSlotRva = 0x726758;
    constexpr std::uintptr_t DecodeScaleCallRva = 0x28651A;
    constexpr std::uintptr_t DecodeScaleSlotRva = 0x726750;
    constexpr std::uintptr_t DecoderWorkerTargetLoadRva = 0x286D60;
    constexpr std::uintptr_t VideoSeekRva = 0x286F70;
    constexpr std::uintptr_t MovieVtableRva = 0x55AF50;
    constexpr std::uintptr_t VideoFfmpegVtableRva = 0x55CD30;

    constexpr std::size_t ManagerMovieOffset = 0x08;
    constexpr std::size_t MovieStateOffset = 0x4C;
    constexpr std::size_t MovieAnimationOffset = 0x88;
    constexpr std::size_t MovieCurrentFrameOffset = 0x98;
    constexpr std::size_t MovieMutexOffset = 0xE0;
    constexpr std::size_t AnimationAlphaOutputOffset = 0x08;
    constexpr std::size_t AnimationAlphaWidthOffset = 0x10;
    constexpr std::size_t AnimationAlphaHeightOffset = 0x14;
    constexpr std::size_t AnimationAlphaScratch1Offset = 0x58;
    constexpr std::size_t AnimationAlphaScratch2Offset = 0x232D8;
    constexpr std::size_t AnimationAlphaScratchPointer1Offset = 0x46558;
    constexpr std::size_t AnimationAlphaScratchPointer2Offset = 0x46560;
    constexpr std::size_t AnimationAlphaGenerationOffset = 0x46568;
    constexpr std::size_t AnimationAlphaFrameOffset = 0x46570;
    constexpr std::size_t AnimationSsvOffset = 0x46578;
    constexpr std::size_t AnimationInfoOffset = 0x46580;
    constexpr std::size_t AnimationTotalFramesOffset = 0x108;
    constexpr std::size_t AnimationFramesPerSecondOffset = 0x10C;
    constexpr std::size_t AnimationAlphaScratchSize =
        AnimationAlphaScratch2Offset - AnimationAlphaScratch1Offset;
    // Current high-resolution cards can carry a 6016x3172 alpha plane
    // (19,082,752 bytes). Keep a conservative upper bound while allowing
    // those native-resolution masks.
    constexpr std::size_t MaximumAlphaOutputSize = 64 * 1024 * 1024;
    constexpr std::size_t SsvVideoDecoderOffset = 0x40;
    constexpr std::size_t VideoFormatContextOffset = 0x38;
    constexpr std::size_t VideoFrameQueueOffset = 0x58;
    constexpr std::size_t VideoFrameQueueMutexOffset = 0x60;
    constexpr std::size_t VideoCurrentFrameOffset = 0x78;
    constexpr std::size_t StreamIndexEntriesOffset = 0x1C8;
    constexpr std::size_t StreamIndexEntryCountOffset = 0x1D0;

    constexpr int PlayingState = 3;
    constexpr int PausedState = 4;
    constexpr HRESULT BridgeSuccess = 1;
    constexpr unsigned ExpectedAvformatVersion =
        (57u << 16) | (47u << 8) | 101u;

    // Common prologue used by the MovieManager wrappers in vghd.exe 2.4.0.0.
    constexpr unsigned char ManagerWrapperSignature[] = {
        0x48, 0x89, 0x4C, 0x24, 0x08, 0x48, 0x83, 0xEC,
        0x48, 0x48, 0x8B, 0x49, 0x08
    };

    constexpr unsigned char ElapsedWrapperSignature[] = {
        0x48, 0x83, 0xEC, 0x48, 0x48, 0x8B, 0x49, 0x08,
        0x48, 0x85, 0xC9, 0x74
    };

    constexpr unsigned char DurationWrapperSignature[] = {
        0x40, 0x53, 0x48, 0x83, 0xEC, 0x40, 0x48, 0x8B,
        0x59, 0x08, 0x48, 0x85, 0xDB, 0x74
    };

    constexpr unsigned char AnimationFrameSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74,
        0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x40, 0x41
    };

    constexpr unsigned char MovieAdvanceSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x20, 0x55, 0x56, 0x57,
        0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57
    };

    constexpr unsigned char MoviePauseSignature[] = {
        0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B
    };

    constexpr unsigned char MovieResumeSignature[] = {
        0x48, 0x89, 0x5C, 0x24, 0x18, 0x57, 0x48, 0x83
    };

    constexpr unsigned char MovieSetPlayRateSignature[] = {
        0x40, 0x53, 0x48, 0x83, 0xEC, 0x30, 0x0F, 0x29
    };

    constexpr unsigned char AvcodecOpenCallSignature[] = {
        0x48, 0x8B, 0x05, 0x64, 0xFF, 0x49, 0x00, 0x45,
        0x33, 0xC0, 0x49, 0x8B, 0xCE, 0xFF, 0xD0, 0x85
    };

    constexpr unsigned char DecodeScaleCallSignature[] = {
        0xFF, 0x15, 0x30, 0x02, 0x4A, 0x00
    };

    constexpr unsigned char DecoderWorkerTargetLoadSignature[] = {
        0x41, 0x8B, 0x6E, 0x78, 0x49, 0x8B, 0x4E, 0x60
    };

    using ManagerAction = void(__fastcall*)(void* manager);
    using ManagerSetPlayRate = void(__fastcall*)(void* manager, double playRate);
    using MutexAction = void(__fastcall*)(void* mutex);
    using AvcodecOpen2 = int(__cdecl*)(void* codecContext, const void* codec, void* options);
    using AvcodecFlushBuffers = void(__cdecl*)(void* codecContext);
    using AvSeekFrame = int(__cdecl*)(void* formatContext, int streamIndex,
        std::int64_t timestamp, int flags);
    using AvOptSetInt = int(__cdecl*)(void* target, const char* name,
        std::int64_t value, int searchFlags);
    using AvformatVersion = unsigned(__cdecl*)();
    using VideoSeek = bool(__fastcall*)(void* video, int frame);
    using QThreadRequestInterruption = void(__fastcall*)(void* thread);
    using QThreadWait = bool(__fastcall*)(void* thread, unsigned long milliseconds);
    using QThreadStart = void(__fastcall*)(void* thread, int priority);
    using VirtualAlloc2Action = PVOID(WINAPI*)(HANDLE process, PVOID baseAddress,
        SIZE_T size, ULONG allocationType, ULONG pageProtection,
        MEM_EXTENDED_PARAMETER* parameters, ULONG parameterCount);

    PVOID volatile g_movieManager = nullptr;
    PVOID volatile g_activeMovie = nullptr;
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
    PVOID volatile g_decoderCatchupVideo = nullptr;
    LONG volatile g_decoderCatchupTargetFrame = -1;
    LONG volatile g_lastDecoderCatchupDistance = 0;
    LONG volatile g_lastDroppedVideoFrames = 0;
    LONG volatile g_codecFlushCount = 0;
    LONG volatile g_alphaResetCount = 0;
    LONG volatile g_lastAlphaFrameBeforeReset = -1;
    LONG volatile g_keyframeSeekCount = 0;
    LONG volatile g_lastKeyframeSeekFrame = -1;
    SRWLOCK g_decodePatchLock = SRWLOCK_INIT;
    PVOID volatile g_fastForwardMovie = nullptr;
    LONG volatile g_fastForwardTargetFrame = -1;

    bool IsReadable(const void* address, std::size_t length)
    {
        if (address == nullptr || length == 0)
        {
            return false;
        }

        MEMORY_BASIC_INFORMATION memory = {};
        if (VirtualQuery(address, &memory, sizeof(memory)) != sizeof(memory) ||
            memory.State != MEM_COMMIT || (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
        {
            return false;
        }

        const auto start = reinterpret_cast<std::uintptr_t>(address);
        const auto regionStart = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
        const auto regionEnd = regionStart + memory.RegionSize;
        return start >= regionStart && length <= regionEnd - start;
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
        return rva < imageSize && length <= imageSize - rva;
    }

    bool HasSignature(std::uintptr_t rva, const unsigned char* signature, std::size_t length)
    {
        const auto base = ImageBase();
        return base != nullptr && IsRvaInImage(rva, length) &&
            std::memcmp(base + rva, signature, length) == 0;
    }

    int CompatibilityMask()
    {
        int mask = 0;
        mask |= HasSignature(PauseRva, ManagerWrapperSignature, sizeof(ManagerWrapperSignature)) ? 1 : 0;
        mask |= HasSignature(ResumeRva, ManagerWrapperSignature, sizeof(ManagerWrapperSignature)) ? 2 : 0;
        mask |= HasSignature(ElapsedRva, ElapsedWrapperSignature, sizeof(ElapsedWrapperSignature)) ? 4 : 0;
        mask |= HasSignature(DurationRva, DurationWrapperSignature, sizeof(DurationWrapperSignature)) ? 8 : 0;
        mask |= HasSignature(SetPlayRateRva, ManagerWrapperSignature, sizeof(ManagerWrapperSignature)) ? 16 : 0;
        mask |= HasSignature(PlayRva, ManagerWrapperSignature, sizeof(ManagerWrapperSignature)) ? 32 : 0;
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
        LONG cached = InterlockedCompareExchange(
            &g_fastForwardCompatibility, -1, -1);
        if (cached < 0)
        {
            const LONG detected =
                HasSignature(AnimationFrameRva, AnimationFrameSignature,
                    sizeof(AnimationFrameSignature)) &&
                HasSignature(MovieAdvanceRva, MovieAdvanceSignature,
                    sizeof(MovieAdvanceSignature));
            InterlockedCompareExchange(
                &g_fastForwardCompatibility, detected, -1);
            cached = InterlockedCompareExchange(
                &g_fastForwardCompatibility, -1, -1);
        }
        return cached != 0;
    }

    bool HasFastDecodeEngine()
    {
        return HasSignature(AvcodecOpenCallRva, AvcodecOpenCallSignature,
                sizeof(AvcodecOpenCallSignature)) &&
            IsRvaInImage(AvcodecOpenSlotRva, sizeof(void*)) &&
            IsRvaInImage(AvSeekFrameSlotRva, sizeof(void*)) &&
            IsRvaInImage(DecodeScaleSlotRva, sizeof(void*)) &&
            (InterlockedCompareExchangePointer(&g_decodeScaleThunk, nullptr, nullptr) != nullptr ||
                HasSignature(DecodeScaleCallRva, DecodeScaleCallSignature,
                    sizeof(DecodeScaleCallSignature))) &&
            (InterlockedCompareExchangePointer(&g_decoderWorkerTargetThunk,
                    nullptr, nullptr) != nullptr ||
                HasSignature(DecoderWorkerTargetLoadRva,
                    DecoderWorkerTargetLoadSignature,
                    sizeof(DecoderWorkerTargetLoadSignature)));
    }

    void* MovieManager()
    {
        return InterlockedCompareExchangePointer(&g_movieManager, nullptr, nullptr);
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

    void* Movie(void* manager)
    {
        if (!IsReadable(manager, ManagerMovieOffset + sizeof(void*)))
        {
            return nullptr;
        }

        void* movie = *reinterpret_cast<void**>(reinterpret_cast<unsigned char*>(manager) + ManagerMovieOffset);
        return IsMovie(movie) ? movie : nullptr;
    }

    void* ActiveMovie()
    {
        void* movie = Movie(MovieManager());
        if (movie != nullptr)
        {
            return movie;
        }

        movie = InterlockedCompareExchangePointer(
            &g_activeMovie, nullptr, nullptr);
        return IsMovie(movie) ? movie : nullptr;
    }

    void* VideoDecoder(void* animation)
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
        if (!IsReadable(video, VideoCurrentFrameOffset + sizeof(int)))
        {
            return nullptr;
        }

        void* vtable = *reinterpret_cast<void**>(video);
        return vtable == ImageBase() + VideoFfmpegVtableRva ? video : nullptr;
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
            framesPerSecond > 0 && framesPerSecond <= 240 &&
            VideoDecoder(animation) != nullptr;
    }

    void* DiscoverActiveMovie()
    {
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
                        if (*reinterpret_cast<void**>(candidate) ==
                                expectedVtable &&
                            IsActiveMovieCandidate(
                                reinterpret_cast<void*>(candidate)))
                        {
                            return reinterpret_cast<void*>(candidate);
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
        return nullptr;
    }

    void* VideoCodecContext(void* formatContext)
    {
        auto formatBytes = reinterpret_cast<unsigned char*>(formatContext);
        if (!IsReadable(formatContext, 0x38))
        {
            return nullptr;
        }

        const int streamCount = *reinterpret_cast<const int*>(formatBytes + 0x2C);
        void** streams = *reinterpret_cast<void***>(formatBytes + 0x30);
        if (streamCount < 1 || streamCount > 64 ||
            !IsReadable(streams,
                static_cast<std::size_t>(streamCount) * sizeof(void*)))
        {
            return nullptr;
        }

        for (int index = 0; index < streamCount; index++)
        {
            void* stream = streams[index];
            if (!IsReadable(stream, 0x10))
            {
                continue;
            }

            void* codecContext = *reinterpret_cast<void**>(
                reinterpret_cast<unsigned char*>(stream) + 0x08);
            if (IsReadable(codecContext, 0x10) &&
                *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(codecContext) + 0x0C) == 0)
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
        if (!IsReadable(formatContext, 0x38))
        {
            return false;
        }

        const int streamCount = *reinterpret_cast<const int*>(formatBytes + 0x2C);
        void** streams = *reinterpret_cast<void***>(formatBytes + 0x30);
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

            void* codecContext = *reinterpret_cast<void**>(stream + 0x08);
            if (!IsReadable(codecContext, 0x10) ||
                *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(codecContext) + 0x0C) != 0)
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
        void* scratch1 = animationBytes + AnimationAlphaScratch1Offset;
        void* scratch2 = animationBytes + AnimationAlphaScratch2Offset;
        if (outputSize == 0 || outputSize > MaximumAlphaOutputSize ||
            !IsWritable(output, outputSize) ||
            !IsWritable(scratch1, AnimationAlphaScratchSize) ||
            !IsWritable(scratch2, AnimationAlphaScratchSize))
        {
            return false;
        }

        return true;
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
        void* scratch1 = animationBytes + AnimationAlphaScratch1Offset;
        void* scratch2 = animationBytes + AnimationAlphaScratch2Offset;
        const int previousAlphaFrame = *reinterpret_cast<const int*>(
            animationBytes + AnimationAlphaFrameOffset);
        std::memset(output, 0, outputSize);
        std::memset(scratch1, 0, AnimationAlphaScratchSize);
        std::memset(scratch2, 0, AnimationAlphaScratchSize);
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
            if (IsReadable(queue, 16))
            {
                const int begin = *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(queue) + 8);
                const int end = *reinterpret_cast<const int*>(
                    reinterpret_cast<unsigned char*>(queue) + 12);
                if (begin >= 0 && end >= begin && end - begin <= 64)
                {
                    valid = true;
                    for (int index = begin; index < end; index++)
                    {
                        auto slotAddress = reinterpret_cast<unsigned char*>(queue) +
                            16 + static_cast<std::size_t>(index) * sizeof(void*);
                        if (!IsReadable(slotAddress, sizeof(void*)))
                        {
                            valid = false;
                            break;
                        }

                        void* entry = *reinterpret_cast<void**>(slotAddress);
                        if (!IsReadable(entry, 17))
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
                        *(reinterpret_cast<unsigned char*>(entry) + 16) = 0;
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

        const HMODULE qtCore = GetModuleHandleW(L"Qt5Core.dll");
        if (qtCore == nullptr)
        {
            return EngineError();
        }

        const auto lockMutex = reinterpret_cast<MutexAction>(GetProcAddress(
            qtCore, "?lock@QMutex@@QEAAXXZ"));
        const auto unlockMutex = reinterpret_cast<MutexAction>(GetProcAddress(
            qtCore, "?unlock@QMutex@@QEAAXXZ"));
        if (lockMutex == nullptr || unlockMutex == nullptr)
        {
            return EngineError();
        }

        void* mutex = reinterpret_cast<unsigned char*>(movie) + MovieMutexOffset;
        bool mutexLocked = false;
        HRESULT result = E_FAIL;
        __try
        {
            lockMutex(mutex);
            mutexLocked = true;

            auto animationAddress = reinterpret_cast<unsigned char*>(movie) + MovieAnimationOffset;
            if (!IsReadable(animationAddress, sizeof(void*)))
            {
                result = ManagerError();
            }
            else
            {
                void* animation = *reinterpret_cast<void**>(animationAddress);
                if (!IsReadable(animation, 0x20))
                {
                    result = ManagerError();
                }
                else
                {
                    auto infoAddress = reinterpret_cast<unsigned char*>(animation) + AnimationInfoOffset;
                    if (!IsReadable(infoAddress, sizeof(void*)))
                    {
                        result = ManagerError();
                    }
                    else
                    {
                        void* info = *reinterpret_cast<void**>(infoAddress);
                        if (info == nullptr)
                        {
                            result = ManagerError();
                        }
                        else
                        {
                            auto totalFramesAddress = reinterpret_cast<unsigned char*>(info) + AnimationTotalFramesOffset;
                            auto framesPerSecondAddress = reinterpret_cast<unsigned char*>(info) + AnimationFramesPerSecondOffset;
                            auto currentFrameAddress = reinterpret_cast<unsigned char*>(movie) + MovieCurrentFrameOffset;
                            if (!IsReadable(totalFramesAddress, sizeof(int)) ||
                                !IsReadable(framesPerSecondAddress, sizeof(int)) ||
                                !IsReadable(currentFrameAddress, sizeof(int)))
                            {
                                result = ManagerError();
                            }
                            else
                            {
                                const int framesPerSecond = *reinterpret_cast<const int*>(framesPerSecondAddress);
                                const int frames = totalDuration
                                    ? *reinterpret_cast<const int*>(totalFramesAddress)
                                    : *reinterpret_cast<const int*>(currentFrameAddress);
                                if (framesPerSecond <= 0 || (totalDuration && frames < 0))
                                {
                                    result = E_FAIL;
                                }
                                else
                                {
                                    const int timelineFrames = frames < 0 ? 0 : frames;
                                    const std::int64_t milliseconds =
                                        static_cast<std::int64_t>(timelineFrames) * 1000 / framesPerSecond;
                                    result = milliseconds > MAXLONG
                                        ? MAXLONG
                                        : static_cast<HRESULT>(milliseconds);
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
        return result;
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
        if (!HasSignature(DecodeScaleCallRva, DecodeScaleCallSignature,
                sizeof(DecodeScaleCallSignature)))
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
        if (!VirtualProtect(call, sizeof(DecodeScaleCallSignature),
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
                sizeof(DecodeScaleCallSignature));
            DWORD ignoredProtection = 0;
            VirtualProtect(call, sizeof(DecodeScaleCallSignature),
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
        if (!HasSignature(DecoderWorkerTargetLoadRva,
                DecoderWorkerTargetLoadSignature,
                sizeof(DecoderWorkerTargetLoadSignature)))
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
            0x41, 0x8B, 0x6E, 0x78,                   // mov ebp,[r14+78h]
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
            0x49, 0x8B, 0x4E, 0x60,                   // mov rcx,[r14+60h]
            0x58,                                     // pop rax
            0x9D,                                     // popfq
            0xC3                                      // done: ret
        };

        unsigned char* thunk = AllocateNear(load + 8, 64);
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
        std::memcpy(thunk + 8, &videoAddress, sizeof(videoAddress));
        std::memcpy(thunk + 23, &targetAddress, sizeof(targetAddress));
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
        if (!VirtualProtect(load, sizeof(DecoderWorkerTargetLoadSignature),
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
                sizeof(DecoderWorkerTargetLoadSignature) - 5);
            FlushInstructionCache(GetCurrentProcess(), load,
                sizeof(DecoderWorkerTargetLoadSignature));
            DWORD ignoredProtection = 0;
            VirtualProtect(load, sizeof(DecoderWorkerTargetLoadSignature),
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

    HRESULT InstallFastDecodeHook(LONG threadCount)
    {
        if (!HasCompatibleEngine() || !HasFastDecodeEngine())
        {
            return EngineError();
        }

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

        auto slot = reinterpret_cast<PVOID volatile*>(ImageBase() + AvcodecOpenSlotRva);
        auto seekSlot = reinterpret_cast<PVOID volatile*>(
            ImageBase() + AvSeekFrameSlotRva);
        if (!IsReadable(const_cast<PVOID*>(slot), sizeof(void*)) ||
            !IsReadable(const_cast<PVOID*>(seekSlot), sizeof(void*)))
        {
            return EngineError();
        }

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

        // The process-local function-pointer hook must not outlive this DLL.
        // Pinning is safer than relying on Deviare's custom-DLL unload timing;
        // Windows releases it normally when vghd.exe exits.
        HMODULE pinnedModule = nullptr;
        if (!GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                reinterpret_cast<LPCWSTR>(&FastAvcodecOpen2), &pinnedModule))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        const HRESULT workerPatch = InstallDecoderWorkerTargetPatch();
        if (workerPatch < 0)
        {
            return workerPatch;
        }

        const HRESULT scalePatch = InstallScaleSkipPatch();
        if (scalePatch < 0)
        {
            return scalePatch;
        }

        InterlockedExchange(&g_decoderThreadCount, threadCount);
        InterlockedCompareExchangePointer(&g_originalAvcodecOpen2, expectedOpen, nullptr);
        InterlockedCompareExchangePointer(
            &g_originalAvSeekFrame, expectedSeek, nullptr);

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

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperPlaybackBridgeVersion()
{
    HasCompatibleEngine();
    HasFastForwardEngine();
    return 12;
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

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperSetMovieManager(SIZE_T managerAddress)
{
    __try
    {
        if (!HasCompatibleEngine())
        {
            return EngineError();
        }

        void* manager = reinterpret_cast<void*>(managerAddress);
        if (Movie(manager) == nullptr)
        {
            return E_INVALIDARG;
        }

        InterlockedExchangePointer(&g_movieManager, manager);
        return BridgeSuccess;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
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
        if (!IsMovie(movie))
        {
            return E_INVALIDARG;
        }

        InterlockedExchangePointer(&g_activeMovie, movie);
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

        InterlockedExchangePointer(&g_activeMovie, movie);
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

        void* manager = MovieManager();
        void* movie = Movie(manager);
        if (movie == nullptr)
        {
            movie = ActiveMovie();
            if (movie == nullptr ||
                !HasSignature(MoviePauseRva, MoviePauseSignature,
                    sizeof(MoviePauseSignature)))
            {
                return ManagerError();
            }
            FunctionAt<ManagerAction>(MoviePauseRva)(movie);
        }
        else
        {
            FunctionAt<ManagerAction>(PauseRva)(manager);
        }

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

        void* manager = MovieManager();
        void* movie = Movie(manager);
        if (movie == nullptr)
        {
            movie = ActiveMovie();
            if (movie == nullptr ||
                !HasSignature(MovieResumeRva, MovieResumeSignature,
                    sizeof(MovieResumeSignature)))
            {
                return ManagerError();
            }
            FunctionAt<ManagerAction>(MovieResumeRva)(movie);
        }
        else
        {
            FunctionAt<ManagerAction>(ResumeRva)(manager);
        }

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

        void* manager = MovieManager();
        void* movie = Movie(manager);
        const bool useManager = movie != nullptr;
        if (movie == nullptr)
        {
            movie = ActiveMovie();
            if (movie == nullptr ||
                !HasSignature(MovieSetPlayRateRva,
                    MovieSetPlayRateSignature,
                    sizeof(MovieSetPlayRateSignature)))
            {
                return ManagerError();
            }
        }

        static_assert(sizeof(double) == sizeof(rateBits), "Unexpected SIZE_T width");
        double playRate = 1.0;
        std::memcpy(&playRate, &rateBits, sizeof(playRate));
        if (!_finite(playRate) || playRate < 0.01 || playRate > 100.0)
        {
            return E_INVALIDARG;
        }

        if (useManager)
        {
            FunctionAt<ManagerSetPlayRate>(SetPlayRateRva)(manager, playRate);
        }
        else
        {
            FunctionAt<ManagerSetPlayRate>(MovieSetPlayRateRva)(movie, playRate);
        }
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

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetKeyframeSeekCount()
{
    return InterlockedCompareExchange(&g_keyframeSeekCount, 0, 0);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperGetLastKeyframeSeekFrame()
{
    return InterlockedCompareExchange(&g_lastKeyframeSeekFrame, -1, -1);
}

extern "C" __declspec(dllexport) HRESULT WINAPI IStripperPrepareFastForwardMilliseconds(
    SIZE_T targetMilliseconds)
{
    __try
    {
        if (!HasCompatibleEngine() || !HasFastForwardEngine() ||
            InterlockedCompareExchangePointer(&g_decodeScaleThunk,
                nullptr, nullptr) == nullptr ||
            InterlockedCompareExchangePointer(&g_decoderWorkerTargetThunk,
                nullptr, nullptr) == nullptr)
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
        const auto unlockMutex = reinterpret_cast<MutexAction>(GetProcAddress(
            qtCore, "?unlock@QMutex@@QEAAXXZ"));
        if (lockMutex == nullptr || unlockMutex == nullptr)
        {
            return EngineError();
        }

        void* mutex = reinterpret_cast<unsigned char*>(movie) + MovieMutexOffset;
        bool mutexLocked = false;
        HRESULT result = E_FAIL;
        __try
        {
            lockMutex(mutex);
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
                            if (!CanResetAnimationAlpha(animation))
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
                                        if (!ResetAnimationAlpha(animation))
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
                                                // currentFrame + 1. Its alpha position
                                                // was reset above, so CAnim rebuilds
                                                // every delta-alpha record through the
                                                // exact colour target before Movie
                                                // publishes that target as current.
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
        return result;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        InterlockedExchange(&g_decoderCatchupTargetFrame, -1);
        InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
        InterlockedExchange(&g_fastForwardTargetFrame, -1);
        InterlockedExchangePointer(&g_fastForwardMovie, nullptr);
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
            return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        }

        const auto currentAddress = reinterpret_cast<const int*>(
            reinterpret_cast<unsigned char*>(movie) + MovieCurrentFrameOffset);
        if (!IsReadable(currentAddress, sizeof(int)))
        {
            return ManagerError();
        }

        if (*currentAddress < targetFrame)
        {
            return S_OK;
        }

        InterlockedExchange(&g_fastForwardTargetFrame, -1);
        InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
        InterlockedExchangePointer(&g_fastForwardMovie, nullptr);
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
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
