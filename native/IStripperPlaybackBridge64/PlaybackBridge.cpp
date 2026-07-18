#include <Windows.h>
#include <TlHelp32.h>
#include <cstdint>
#include <cstdlib>
#include <cwchar>
#include <cstring>
#include <float.h>

// This bridge discovers private vghd functions and vtables from the loaded image.
// Structural field offsets come from vghd-offsets.ini. Every resolved address and
// object is validated so an incompatible update fails closed.
namespace
{
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
    std::uintptr_t VideoWmvCoreVtableRva = 0;

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
    std::size_t SsvVideoDecoderOffset = 0;
    std::size_t VideoFormatContextOffset = 0;
    std::size_t VideoFrameQueueOffset = 0;
    std::size_t VideoFrameQueueMutexOffset = 0;
    std::size_t VideoCurrentFrameOffset = 0;
    std::size_t StreamIndexEntriesOffset = 0;
    std::size_t StreamIndexEntryCountOffset = 0;

    constexpr int PlayingState = 3;
    constexpr int PausedState = 4;
    constexpr HRESULT BridgeSuccess = 1;
    constexpr unsigned ExpectedAvformatVersion =
        (57u << 16) | (47u << 8) | 101u;

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

    constexpr std::size_t DecodeScaleCallLength = 6;
    constexpr std::size_t DecoderWorkerTargetLoadLength = 8;

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
    LONG volatile g_alphaCheckpointRestoreCount = 0;
    LONG volatile g_lastAlphaCheckpointFrame = -1;
    LONG volatile g_keyframeSeekCount = 0;
    LONG volatile g_lastKeyframeSeekFrame = -1;
    LONG volatile g_offsetsResolved = 0;
    LONG volatile g_offsetResolverMask = 0;
    LONG volatile g_movieResolverMask = 0;
    LONG volatile g_fastDecodeResolverMask = 0;
    LONG volatile g_fastDecodeInstallStage = 0;
    SRWLOCK g_decodePatchLock = SRWLOCK_INIT;
    SRWLOCK g_offsetResolveLock = SRWLOCK_INIT;
    SRWLOCK g_alphaCheckpointLock = SRWLOCK_INIT;
    PVOID volatile g_fastForwardMovie = nullptr;
    LONG volatile g_fastForwardTargetFrame = -1;

    struct AlphaCheckpoint
    {
        int frame = -1;
        int bucket = -1;
        std::size_t outputSize = 0;
        unsigned char* data = nullptr;
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

    std::size_t ReadLayoutOffset(const wchar_t* key,
        const wchar_t* profilePath)
    {
        wchar_t value[32] = {};
        if (GetPrivateProfileStringW(L"layout", key, L"", value,
                static_cast<DWORD>(sizeof(value) / sizeof(value[0])),
                profilePath) == 0)
        {
            return 0;
        }
        return static_cast<std::size_t>(_wcstoui64(value, nullptr, 0));
    }

    bool LoadLayoutOffsets(const wchar_t* profilePath)
    {
        AnimationAlphaOutputOffset =
            ReadLayoutOffset(L"AnimationAlphaOutput", profilePath);
        AnimationAlphaWidthOffset =
            ReadLayoutOffset(L"AnimationAlphaWidth", profilePath);
        AnimationAlphaHeightOffset =
            ReadLayoutOffset(L"AnimationAlphaHeight", profilePath);
        AnimationAlphaScratch1Offset =
            ReadLayoutOffset(L"AnimationAlphaScratch1", profilePath);
        AnimationAlphaScratch2Offset =
            ReadLayoutOffset(L"AnimationAlphaScratch2", profilePath);
        AnimationAlphaScratchPointer1Offset =
            ReadLayoutOffset(L"AnimationAlphaScratchPointer1", profilePath);
        AnimationAlphaScratchPointer2Offset =
            ReadLayoutOffset(L"AnimationAlphaScratchPointer2", profilePath);
        AnimationAlphaGenerationOffset =
            ReadLayoutOffset(L"AnimationAlphaGeneration", profilePath);
        AnimationAlphaFrameOffset =
            ReadLayoutOffset(L"AnimationAlphaFrame", profilePath);
        AnimationSsvOffset =
            ReadLayoutOffset(L"AnimationSsv", profilePath);
        AnimationInfoOffset =
            ReadLayoutOffset(L"AnimationInfo", profilePath);
        AnimationTotalFramesOffset =
            ReadLayoutOffset(L"AnimationTotalFrames", profilePath);
        AnimationFramesPerSecondOffset =
            ReadLayoutOffset(L"AnimationFramesPerSecond", profilePath);
        SsvVideoDecoderOffset =
            ReadLayoutOffset(L"SsvVideoDecoder", profilePath);
        StreamIndexEntriesOffset =
            ReadLayoutOffset(L"StreamIndexEntries", profilePath);
        StreamIndexEntryCountOffset =
            ReadLayoutOffset(L"StreamIndexEntryCount", profilePath);

        return AnimationAlphaOutputOffset > 0 &&
            AnimationAlphaWidthOffset > 0 &&
            AnimationAlphaHeightOffset > 0 &&
            AnimationAlphaScratch1Offset > 0 &&
            AnimationAlphaScratch2Offset > AnimationAlphaScratch1Offset &&
            AnimationAlphaScratchPointer1Offset >
                AnimationAlphaScratch2Offset &&
            AnimationSsvOffset > AnimationAlphaScratchPointer2Offset &&
            AnimationInfoOffset > AnimationSsvOffset &&
            AnimationTotalFramesOffset > 0 &&
            AnimationFramesPerSecondOffset > 0 &&
            SsvVideoDecoderOffset > 0 &&
            StreamIndexEntriesOffset > 0 &&
            StreamIndexEntryCountOffset > StreamIndexEntriesOffset;
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
        if (kind == 3)
        {
            const unsigned char mutex[] = { 0x48, 0x81, 0xC1 };
            const unsigned char current[] = { 0x44, 0x8B, 0xBB };
            if (AnimationFrameRva == 0 ||
                FindSequence(candidate, 64, mutex, sizeof(mutex)) == nullptr ||
                FindSequence(candidate, 128, current, sizeof(current)) == nullptr)
            {
                return false;
            }
            const auto animationFrame = ImageBase() + AnimationFrameRva;
            for (std::size_t offset = 0; offset + 12 <= 224; offset++)
            {
                if (candidate[offset] != 0x48 ||
                    candidate[offset + 1] != 0x8B ||
                    candidate[offset + 2] != 0x8B ||
                    candidate[offset + 7] != 0xE8)
                {
                    continue;
                }
                const auto displacement =
                    *reinterpret_cast<const std::int32_t*>(
                        candidate + offset + 8);
                if (candidate + offset + 12 + displacement == animationFrame)
                {
                    return true;
                }
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

    bool ResolveMovieOffsets()
    {
        LONG mask = 0;
        auto animationFrame = FindUniqueFunction(AnimationFrameSignature,
            sizeof(AnimationFrameSignature), -1);
        mask |= animationFrame != nullptr ? 16 : 0;
        AnimationFrameRva = animationFrame == nullptr
            ? 0
            : RvaFromAddress(animationFrame);
        auto pause = FindUniqueFunction(MoviePauseSignature,
            sizeof(MoviePauseSignature), 0);
        mask |= pause != nullptr ? 1 : 0;
        auto resume = FindUniqueFunction(MovieResumeSignature,
            sizeof(MovieResumeSignature), 1);
        mask |= resume != nullptr ? 2 : 0;
        auto setRate = FindUniqueFunction(MovieSetPlayRateSignature,
            sizeof(MovieSetPlayRateSignature), 2);
        mask |= setRate != nullptr ? 4 : 0;
        auto advance = FindUniqueFunction(MovieAdvanceSignature,
            sizeof(MovieAdvanceSignature), 3);
        mask |= advance != nullptr ? 8 : 0;
        if (pause == nullptr || resume == nullptr || setRate == nullptr ||
            advance == nullptr || animationFrame == nullptr)
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
        MovieAdvanceRva = RvaFromAddress(advance);
        MovieMutexOffset = *reinterpret_cast<const std::uint32_t*>(
            pauseMutex + 3);
        MovieStateOffset = pauseState[2];

        const unsigned char loadCurrent[] = { 0x44, 0x8B, 0xBB };
        const auto currentLoad = FindSequence(advance, 128,
            loadCurrent, sizeof(loadCurrent));
        const auto advanceMutex = FindSequence(advance, 64,
            addMutex, sizeof(addMutex));
        if (currentLoad == nullptr || advanceMutex == nullptr ||
            *reinterpret_cast<const std::uint32_t*>(advanceMutex + 3) !=
                MovieMutexOffset)
        {
            InterlockedExchange(&g_movieResolverMask, mask);
            return false;
        }
        mask |= 64;
        MovieCurrentFrameOffset =
            *reinterpret_cast<const std::uint32_t*>(currentLoad + 3);

        MovieAnimationOffset = 0;
        for (std::size_t offset = 0; offset < 224; offset++)
        {
            if (advance[offset] == 0x48 && advance[offset + 1] == 0x8B &&
                advance[offset + 2] == 0x8B &&
                advance[offset + 7] == 0xE8)
            {
                const std::int32_t displacement =
                    *reinterpret_cast<const std::int32_t*>(
                        advance + offset + 8);
                const auto target = advance + offset + 12 + displacement;
                if (target == animationFrame)
                {
                    MovieAnimationOffset =
                        *reinterpret_cast<const std::uint32_t*>(
                            advance + offset + 3);
                    break;
                }
            }
        }
        mask |= MovieAnimationOffset > 0 ? 128 : 0;
        MovieVtableRva = FindVtableRva(".?AVMovie@@");
        mask |= MovieVtableRva > 0 ? 256 : 0;
        const bool resolved =
            MovieAnimationOffset > 0 && MovieCurrentFrameOffset > 0 &&
            MovieMutexOffset > 0 && MovieStateOffset > 0 &&
            rateStore[4] > 0 && MovieVtableRva > 0;
        mask |= resolved ? 512 : 0;
        InterlockedExchange(&g_movieResolverMask, mask);
        return resolved;
    }

    bool ResolveVideoOffsets()
    {
        VideoFfmpegVtableRva = FindVtableRva(".?AVVideoFFmpeg@@");
        VideoWmvCoreVtableRva = FindVtableRva(".?AVVideoWmvCore@@");
        if (!IsRvaInImage(VideoFfmpegVtableRva,
                sizeof(void*) * 15))
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
        VideoSeekRva = RvaFromAddress(seek);

        const unsigned char workerLoadPrefix[] =
            { 0x41, 0x8B, 0x6E };
        const unsigned char queuePrefix[] =
            { 0x49, 0x8B, 0x4E };
        const unsigned char queueSuffix[] =
            { 0x8B, 0x41, 0x0C };
        const unsigned char* targetLoad = nullptr;
        for (std::size_t offset = 0; offset < 512; offset++)
        {
            if (std::memcmp(worker + offset, workerLoadPrefix,
                    sizeof(workerLoadPrefix)) == 0 &&
                std::memcmp(worker + offset + 4, queuePrefix,
                    sizeof(queuePrefix)) == 0)
            {
                targetLoad = worker + offset;
                break;
            }
        }
        if (targetLoad == nullptr)
        {
            return false;
        }
        DecoderWorkerTargetLoadRva = RvaFromAddress(targetLoad);
        VideoCurrentFrameOffset = targetLoad[3];
        VideoFrameQueueMutexOffset = targetLoad[7];

        VideoFrameQueueOffset = 0;
        for (std::size_t offset = 8; offset < 128; offset++)
        {
            if (std::memcmp(targetLoad + offset, queuePrefix,
                    sizeof(queuePrefix)) == 0 &&
                std::memcmp(targetLoad + offset + 4, queueSuffix,
                    sizeof(queueSuffix)) == 0)
            {
                VideoFrameQueueOffset = targetLoad[offset + 3];
                break;
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

        const unsigned char clearCurrent[] = { 0xC7, 0x43 };
        const auto currentClear = FindSequence(suffix, 96,
            clearCurrent, sizeof(clearCurrent));
        if (currentClear == nullptr ||
            *reinterpret_cast<const std::uint32_t*>(currentClear + 3) != 0)
        {
            return false;
        }
        if (VideoCurrentFrameOffset != currentClear[2])
        {
            return false;
        }
        return VideoFrameQueueOffset > 0 &&
            VideoFrameQueueMutexOffset > 0 &&
            VideoFormatContextOffset > 0 &&
            IsRvaInImage(AvSeekFrameSlotRva, sizeof(void*));
    }

    void ImageProfileSection(wchar_t (&section)[64])
    {
        const auto headers = ImageHeaders();
        if (headers == nullptr)
        {
            section[0] = L'\0';
            return;
        }
        swprintf_s(section, L"image_%08X_%08X",
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
            L"VideoWmvCoreVtableRva", VideoWmvCoreVtableRva);
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
            L"VideoFormatContextOffset", VideoFormatContextOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoFrameQueueOffset", VideoFrameQueueOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoFrameQueueMutexOffset", VideoFrameQueueMutexOffset);
        WriteResolvedValue(profilePath, section,
            L"VideoCurrentFrameOffset", VideoCurrentFrameOffset);
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
            const bool hasLayout = hasPath &&
                LoadLayoutOffsets(profilePath);
            mask |= hasLayout ? 2 : 0;
            const bool hasMovie = hasLayout && ResolveMovieOffsets();
            mask |= hasMovie ? 4 : 0;
            const bool hasVideo = hasMovie && ResolveVideoOffsets();
            mask |= hasVideo ? 8 : 0;
            const bool resolved = hasVideo;
            InterlockedExchange(&g_offsetResolverMask, mask);
            if (resolved)
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
        LONG mask = 0;
        if (!EnsureEngineOffsets())
        {
            InterlockedExchange(&g_fastDecodeResolverMask, mask);
            return false;
        }
        mask |= 1;
        if (IsRvaInImage(AvcodecOpenSlotRva, sizeof(void*)) &&
            IsRvaInImage(DecodeScaleSlotRva, sizeof(void*)) &&
            IsRvaInImage(DecodeScaleCallRva,
                DecodeScaleCallLength))
        {
            InterlockedExchange(&g_fastDecodeResolverMask, 0x3F);
            return true;
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
            InterlockedExchange(&g_fastDecodeResolverMask, mask);
            return false;
        }
        mask |= 2;

        std::uintptr_t openSlot = 0;
        std::uintptr_t scaleSlot = 0;
        std::uintptr_t scaleCall = 0;
        if (!IsRvaInImage(VideoFfmpegVtableRva,
                sizeof(void*) * 15))
        {
            InterlockedExchange(&g_fastDecodeResolverMask, mask);
            return false;
        }
        auto vtable = reinterpret_cast<unsigned char**>(
            base + VideoFfmpegVtableRva);
        auto scaleFunction = vtable[3];
        auto openFunction = vtable[12];
        if (!IsExecutableAddress(scaleFunction) ||
            !IsExecutableAddress(openFunction))
        {
            InterlockedExchange(&g_fastDecodeResolverMask, mask);
            return false;
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
            InterlockedExchange(&g_fastDecodeResolverMask, mask);
            return false;
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
                    InterlockedExchange(&g_fastDecodeResolverMask, mask);
                    return false;
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
                        InterlockedExchange(&g_fastDecodeResolverMask, mask);
                        return false;
                    }
                    scaleSlot = candidateSlot;
                    scaleCall = candidateCall;
                }
            }
        }
        mask |= scaleSlot != 0 && scaleCall != 0 ? 16 : 0;
        if (openSlot == 0 || scaleSlot == 0 || scaleCall == 0)
        {
            InterlockedExchange(&g_fastDecodeResolverMask, mask);
            return false;
        }
        AvcodecOpenSlotRva = openSlot;
        DecodeScaleSlotRva = scaleSlot;
        DecodeScaleCallRva = scaleCall;

        wchar_t profilePath[MAX_PATH] = {};
        wchar_t section[64] = {};
        if (OffsetProfilePath(profilePath))
        {
            ImageProfileSection(section);
            WriteResolvedValue(profilePath, section,
                L"AvcodecOpenSlotRva", AvcodecOpenSlotRva);
            WriteResolvedValue(profilePath, section,
                L"DecodeScaleSlotRva", DecodeScaleSlotRva);
            WriteResolvedValue(profilePath, section,
                L"DecodeScaleCallRva", DecodeScaleCallRva);
        }
        mask |= 32;
        InterlockedExchange(&g_fastDecodeResolverMask, mask);
        return true;
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
            VideoCurrentFrameOffset > 0x7F ||
            VideoFrameQueueMutexOffset > 0x7F)
        {
            return false;
        }
        const auto load = ImageBase() + DecoderWorkerTargetLoadRva;
        return load[0] == 0x41 && load[1] == 0x8B &&
            load[2] == 0x6E &&
            load[3] == static_cast<unsigned char>(
                VideoCurrentFrameOffset) &&
            load[4] == 0x49 && load[5] == 0x8B &&
            load[6] == 0x4E &&
            load[7] == static_cast<unsigned char>(
                VideoFrameQueueMutexOffset);
    }

    int CompatibilityMask()
    {
        if (!EnsureEngineOffsets())
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
        mask |= HasSignature(MovieAdvanceRva, MovieAdvanceSignature,
            sizeof(MovieAdvanceSignature)) ? 32 : 0;
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
        if (checkpointSize > MaximumAlphaCheckpointBytes)
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

        while (g_alphaCheckpointCount > 0 &&
            (g_alphaCheckpointCount >= MaximumAlphaCheckpoints ||
                checkpointSize >
                    MaximumAlphaCheckpointBytes - g_alphaCheckpointBytes))
        {
            g_alphaCheckpointBytes -=
                g_alphaCheckpoints[0].outputSize +
                stateSize;
            FreeAlphaCheckpoint(g_alphaCheckpoints[0]);
            std::memmove(&g_alphaCheckpoints[0], &g_alphaCheckpoints[1],
                static_cast<std::size_t>(g_alphaCheckpointCount - 1) *
                    sizeof(AlphaCheckpoint));
            g_alphaCheckpoints[--g_alphaCheckpointCount] = {};
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
        g_alphaCheckpointBytes += checkpointSize;
        const int count = g_alphaCheckpointCount;
        ReleaseSRWLockExclusive(&g_alphaCheckpointLock);
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
        thunk[5] = static_cast<unsigned char>(VideoCurrentFrameOffset);
        thunk[51] = static_cast<unsigned char>(
            VideoFrameQueueMutexOffset);
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

        InterlockedExchange(&g_decoderThreadCount, threadCount);
        InterlockedCompareExchangePointer(&g_originalAvcodecOpen2, expectedOpen, nullptr);
        InterlockedCompareExchangePointer(
            &g_originalAvSeekFrame, expectedSeek, nullptr);
        InterlockedExchange(&g_fastDecodeInstallStage, 256);

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
    return 14;
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

        void* movie = ActiveMovie();
        if (movie == nullptr ||
            !HasSignature(MoviePauseRva, MoviePauseSignature,
                sizeof(MoviePauseSignature)))
        {
            return ManagerError();
        }
        FunctionAt<ManagerAction>(MoviePauseRva)(movie);

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

        FunctionAt<ManagerSetPlayRate>(MovieSetPlayRateRva)(movie, playRate);
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
        const auto lockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexAction>(GetProcAddress(
                qtCore, "?lock@QMutex@@QEAAXXZ"));
        const auto unlockMutex = qtCore == nullptr
            ? nullptr
            : reinterpret_cast<MutexAction>(GetProcAddress(
                qtCore, "?unlock@QMutex@@QEAAXXZ"));
        if (movie == nullptr || lockMutex == nullptr || unlockMutex == nullptr)
        {
            return ManagerError();
        }

        void* mutex = reinterpret_cast<unsigned char*>(movie) + MovieMutexOffset;
        bool mutexLocked = false;
        HRESULT result = E_FAIL;
        __try
        {
            lockMutex(mutex);
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
                            void* anyVideo = AnimationVideoDecoder(animation);
                            if (IsWmvDecoder(anyVideo))
                            {
                                result = EngineError();
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
