// Build with /p:VerifyCompatibility=true. No code is injected into iStripper.
#include "PlaybackBridge.cpp"

int __cdecl TestScale() { return 777; }

int RunTestThunk(void* thunk, void* video, bool worker)
{
    // Seed the actual nonvolatile register used at each hook site. Preserve
    // it and supply Windows x64 shadow space before calling the generated hook.
    unsigned char workerCode[] = {
        0x53,0x41,0x57,0x48,0xBB,0,0,0,0,0,0,0,0,
        0x48,0x83,0xEC,0x28,0x48,0xB8,0,0,0,0,0,0,0,0,
        0xFF,0xD0,0x44,0x89,0xF8,0x48,0x83,0xC4,0x28,0x41,0x5F,0x5B,0xC3
    };
    unsigned char scaleCode[] = {
        0x55,0x48,0xBD,0,0,0,0,0,0,0,0,0x48,0x83,0xEC,0x20,
        0x48,0xB8,0,0,0,0,0,0,0,0,0xFF,0xD0,
        0x48,0x83,0xC4,0x20,0x5D,0xC3
    };
    auto code = worker ? workerCode : scaleCode;
    const auto size = worker ? sizeof(workerCode) : sizeof(scaleCode);
    std::memcpy(code + (worker ? 5 : 3), &video, sizeof(video));
    std::memcpy(code + (worker ? 19 : 17), &thunk, sizeof(thunk));
    const auto memory = VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (memory == nullptr) return -999;
    std::memcpy(memory, code, size);
    DWORD protection = 0;
    VirtualProtect(memory, size, PAGE_EXECUTE_READ, &protection);
    FlushInstructionCache(GetCurrentProcess(), memory, size);
    const int result = reinterpret_cast<int(__cdecl*)()>(memory)();
    VirtualFree(memory, 0, MEM_RELEASE);
    return result;
}

bool ResolveTestImports()
{
    // iStripper initializes FFmpeg function slots through QLibrary, not PE
    // imports. Fill only the two slots needed by the offline resolver test.
    struct Symbol { const char* module; const char* name; };
    const Symbol symbols[] = {
        { "avcodec-57.dll", "avcodec_open2" }, { "swscale-4.dll", "sws_scale" }
    };
    const auto base = ImageBase();
    const auto headers = ImageHeaders();
    wchar_t imagePath[MAX_PATH] = {};
    GetModuleFileNameW(GetModuleHandleW(L"vghd.exe"), imagePath, _countof(imagePath));
    wchar_t* separator = wcsrchr(imagePath, L'\\');
    if (separator == nullptr) return false;
    *separator = 0;
    std::wstring codecDirectory = imagePath;
    if (GetFileAttributesW((codecDirectory + L"\\ffmpeg64\\avcodec-57.dll").c_str()) != INVALID_FILE_ATTRIBUTES)
        codecDirectory += L"\\ffmpeg64";
    SetDllDirectoryW(codecDirectory.c_str());
    for (const auto& symbol : symbols)
    {
        const auto module = LoadLibraryA(symbol.module);
        const auto address = module == nullptr ? nullptr : GetProcAddress(module, symbol.name);
        const unsigned char* text = nullptr;
        const auto imageSections = IMAGE_FIRST_SECTION(headers);
        for (unsigned index = 0; index < headers->FileHeader.NumberOfSections && text == nullptr; ++index)
            text = FindSequence(base + imageSections[index].VirtualAddress, imageSections[index].Misc.VirtualSize,
                reinterpret_cast<const unsigned char*>(symbol.name), std::strlen(symbol.name) + 1);
        if (address == nullptr || text == nullptr) { std::fprintf(stderr, "Test symbol %s unavailable address %p text %p\n", symbol.name, address, text); return false; }
        void** slot = nullptr;
        const auto sections = IMAGE_FIRST_SECTION(headers);
        for (unsigned index = 0; index < headers->FileHeader.NumberOfSections; ++index)
        {
            if ((sections[index].Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) continue;
            const auto start = base + sections[index].VirtualAddress;
            const auto size = sections[index].Misc.VirtualSize;
            for (std::size_t offset = 0; offset + 40 < size; ++offset)
            {
                const auto instruction = start + offset;
                if (instruction[0] != 0x48 || instruction[1] != 0x8D || instruction[2] != 0x15 ||
                    instruction + 7 + *reinterpret_cast<const std::int32_t*>(instruction + 3) != text) continue;
                const unsigned char store[] = { 0x48,0x89,0x05 };
                const auto write = FindSequence(instruction + 7, 32, store, sizeof(store));
                if (write == nullptr) continue;
                const auto candidate = reinterpret_cast<void**>(const_cast<unsigned char*>(write + 7 +
                    *reinterpret_cast<const std::int32_t*>(write + 3)));
                if (slot != nullptr && slot != candidate) { std::fprintf(stderr, "Duplicate test slot %s\n", symbol.name); return false; }
                slot = candidate;
            }
        }
        DWORD protection = 0, ignored = 0;
        if (slot == nullptr || !VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &protection)) return false;
        *slot = address;
        VirtualProtect(slot, sizeof(void*), protection, &ignored);
    }
    return true;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2)
    {
        std::fprintf(stderr, "Usage: PlaybackBridgeCompatibilityTests <vghd.exe>\n");
        return 2;
    }

    // Consuming one slot must leave another slot's pending replacement intact.
    // Null cards keep this scheduling check independent of the host allocator.
    auto firstNode = reinterpret_cast<void*>(1);
    auto secondNode = reinterpret_cast<void*>(2);
    g_fullScreenReplacements = {
        { firstNode, nullptr, "first", GetTickCount64() + 60'000 },
        { secondNode, nullptr, "second", GetTickCount64() + 60'000 } };
    PrepareFullScreenReplacement(firstNode);
    if (g_fullScreenReplacements.size() != 1 ||
        g_fullScreenReplacements.front().node != secondNode) return 26;
    g_fullScreenReplacements.push_back({ firstNode, nullptr, "expired", 0 });
    PrepareFullScreenReplacement(nullptr);
    if (g_fullScreenReplacements.size() != 1 ||
        g_fullScreenReplacements.front().node != secondNode) return 27;
    PrepareFullScreenReplacement(secondNode);
    if (!g_fullScreenReplacements.empty()) return 28;

    // Exercise both container ABIs even when only one runtime is installed.
    struct LegacyList
    {
        int referenceCount, capacity, begin, end;
        void* values[3];
    } legacy = { 1, 3, 1, 3, { nullptr, &argc, argv } };
    void* legacyPointer = &legacy;
    std::vector<void*> values;
    if (!ReadQtPointerList(&legacyPointer, 2, values, false) ||
        values.size() != 2 || values[0] != &argc || values[1] != argv)
        return 3;
    legacy.end = 4;
    if (ReadQtPointerList(&legacyPointer, 8, values, false))
        return 4;

    struct ModernList { void* allocation; void** begin; std::intptr_t size; };
    void* entries[] = { &argc, argv };
    ModernList modern = { nullptr, entries, 2 };
    if (!ReadQtPointerList(&modern, 2, values, true) ||
        values.size() != 2 || values[0] != &argc || values[1] != argv ||
        ReadQtPointerList(&modern, 1, values, true))
        return 5;
    modern.size = -1;
    if (ReadQtPointerList(&modern, 8, values, true))
        return 6;
    modern.size = 0;
    modern.begin = nullptr;
    if (!ReadQtPointerList(&modern, 8, values, true) || !values.empty())
        return 7;

    wchar_t fullPath[MAX_PATH] = {};
    if (!GetFullPathNameW(argv[1], _countof(fullPath), fullPath, nullptr))
        return 8;
    wchar_t* separator = wcsrchr(fullPath, L'\\');
    if (separator == nullptr)
        return 8;
    const std::wstring directory(fullPath, separator);
    SetDllDirectoryW(directory.c_str());
    const std::wstring corePath = directory + L"\\Qt6Core.dll";
    HMODULE core = LoadLibraryW(corePath.c_str());
    if (core == nullptr)
        core = LoadLibraryW((directory + L"\\Qt5Core.dll").c_str());
    if (core == nullptr || LoadLibraryExW(fullPath, nullptr,
            DONT_RESOLVE_DLL_REFERENCES) == nullptr)
        return 9;

    if (UsesQt6())
    {
        using ConstructTimer = void(__fastcall*)(void*, void*);
        using DestroyTimer = void(__fastcall*)(void*);
        using SetSingleShot = void(__fastcall*)(void*, bool);
        const auto constructTimer = reinterpret_cast<ConstructTimer>(GetProcAddress(core,
            "??0QTimer@@QEAA@PEAVQObject@@@Z"));
        const auto destroyTimer = reinterpret_cast<DestroyTimer>(GetProcAddress(core,
            "??1QTimer@@UEAA@XZ"));
        const auto setSingleShot = reinterpret_cast<SetSingleShot>(GetProcAddress(core,
            "?setSingleShot@QTimer@@QEAAX_N@Z"));
        if (constructTimer == nullptr || destroyTimer == nullptr || setSingleShot == nullptr)
            return 26;
        alignas(16) unsigned char timer[512] = {};
        constructTimer(timer, nullptr);
        bool property = true;
        if (!ReadQt6BoolProperty(timer, "active", property) || property)
            return 28;
        setSingleShot(timer, true);
        if (!ReadQt6BoolProperty(timer, "singleShot", property) || !property)
            return 29;
        setSingleShot(timer, false);
        if (!ReadQt6BoolProperty(timer, "singleShot", property) || property ||
            ReadQt6BoolProperty(timer, "missingProperty", property))
            return 30;
        destroyTimer(timer);
        // A mapped executable has no live FullScreen window, even when the
        // persisted registry mode belongs to the separately running process.
        if (IsFullScreenModeActive()) return 27;
    }

    // The exported string constructor/destructor own the runtime's allocation.
    using ConstructString = void(__fastcall*)(void*, const char*);
    using DestroyString = void(__fastcall*)(void*);
    const auto construct = reinterpret_cast<ConstructString>(GetProcAddress(
        core, "??0QString@@QEAA@PEBD@Z"));
    const auto destroy = reinterpret_cast<DestroyString>(GetProcAddress(
        core, "??1QString@@QEAA@XZ"));
    if (construct == nullptr || destroy == nullptr)
        return 10;
    QtByteArray text;
    construct(&text, "QuickPlayer compatibility");
    const std::string decoded = QtStringUtf8(&text);
    destroy(&text);
    if (decoded != "QuickPlayer compatibility")
        return 11;

    if (!ResolveMovieOffsets() || !ResolveAnimationLayout())
    {
        std::fprintf(stderr, "Movie/layout resolution failed: 0x%lX\n",
            g_movieResolverMask);
        return 12;
    }
    VideoFfmpegVtableRva = FindVtableRva(".?AVVideoFFmpeg@@");
    if (UsesQt6())
    {
        std::vector<unsigned char> animation(AnimationInfoOffset + 8);
        unsigned char info[4096] = {};
        unsigned char output[4] = {};
        *reinterpret_cast<void**>(animation.data() + AnimationAlphaOutputOffset) = output;
        *reinterpret_cast<int*>(animation.data() + AnimationAlphaWidthOffset) = 2;
        *reinterpret_cast<int*>(animation.data() + AnimationAlphaHeightOffset) = 2;
        *reinterpret_cast<void**>(animation.data() + AnimationSsvOffset) = info;
        *reinterpret_cast<void**>(animation.data() + AnimationInfoOffset) = info;
        *reinterpret_cast<int*>(info + AnimationTotalFramesOffset) = 120;
        *reinterpret_cast<int*>(info + AnimationAlphaEncodingOffset) = 7;
        ClearAlphaCheckpoints();
        for (int frame = 10; frame < 14; frame++)
            if (ObserveDecodedAlphaProgress(animation.data(), frame)) return 31;
        if (!ObserveDecodedAlphaProgress(animation.data(), 14) ||
            CaptureAlphaCheckpoint(animation.data(), 14, 30) != 1) return 32;
        ClearAlphaCheckpoints();
        *reinterpret_cast<int*>(info + AnimationAlphaEncodingOffset) = 5;
        for (int frame = 10; frame < 15; frame++)
            if (ObserveDecodedAlphaProgress(animation.data(), frame)) return 33;
        for (int frame = 1; frame <= 4; frame++)
        {
            *reinterpret_cast<int*>(animation.data() + AnimationAlphaFrameOffset) = frame;
            const bool ready = ObserveDecodedAlphaProgress(animation.data(), frame + 14);
            if (ready != (frame == 4)) return 34;
        }
        ClearAlphaCheckpoints();
    }
    if (!ResolveVideoOffsets())
    {
        std::fprintf(stderr, "Video resolution failed: frame %zX, queue %zX, mutex %zX, format %zX, seek %zX; WMV reader %zX held %zX interface %zX advanced %zX event %zX result %zX count %zX paused %zX mutex %zX color %zX clear %zX peek %zX\n",
            VideoCurrentFrameOffset, VideoFrameQueueOffset, VideoFrameQueueMutexOffset,
            VideoFormatContextOffset, AvSeekFrameSlotRva, WmvReaderObjectOffset,
            WmvFrameHeldOffset, WmvReaderInterfaceOffset, WmvAdvancedInterfaceOffset,
            WmvStatusEventOffset, WmvLastResultOffset, WmvSampleCounterOffset,
            WmvReaderPausedOffset, WmvQueueMutexOffset, WmvColorQueueOffset,
            WmvClearQueuesRva, WmvPeekFrameRva);
        return 14;
    }
    InterlockedExchange(&g_offsetResolverMask, 15);
    InterlockedExchange(&g_offsetsResolved, 1);
    if (CompatibilityMask() != 0x3F)
        return 13;
    const LONG requiredAudioMask = UsesQt6() ? 0x1FF : 0x3F;
    if (!ResolveTestImports() || !ResolveFastDecodeOffsets() ||
        (g_audioResolverMask & requiredAudioMask) != requiredAudioMask)
    {
        std::fprintf(stderr, "Fast decode/audio failed: decode mask %lX, audio mask %lX\n",
            g_fastDecodeResolverMask, g_audioResolverMask);
        return 16;
    }
    const auto start = FindFullScreenStartNextShow();
    if (!ResolveFullScreenLayout(start))
    {
        std::fprintf(stderr, "Fullscreen failed: start %p scene %zX nodes %zX card %zX tag %zX clips %zX size %zX queue %zX nextCard %zX pending %zX selection %zX expectedCard %zX mode %zX ctor %zX insert %zX take %zX clip %zX new %zX\n",
            start, FsClipNodeSceneOffset, SceneNodesOffset, FsClipNodePlayableCardOffset,
            PlayableCardTagOffset, PlayableCardClipsOffset, PlayableCardSize,
            CardSequencerNextOffset, NextCardPlayableCardOffset, SceneExpectedPendingOffset,
            SceneExpectedSelectionOffset, SceneExpectedCardOffset, SceneExpectedModeOffset,
            PlayableCardExactConstructorRva, CardSequencerInsertNextRva,
            CardSequencerTakeNextAtRva, FsClipNodeNextShowClipRva, VghdOperatorNewRva);
        return 15;
    }
    // Value lists differ from pointer lists in Qt6: QString and NextCard
    // elements are objects in the backing allocation, not element pointers.
    unsigned char records[64] = {};
    QtByteArray recordList;
    recordList.begin = records;
    recordList.size = 2;
    if (!ReadQtValueList(&recordList, 32, 2, values, true) ||
        values.size() != 2 || values[0] != records || values[1] != records + 32 ||
        ReadQtValueList(&recordList, 32, 1, values, true)) return 17;

    using Allocate = void*(__cdecl*)(void**, std::intptr_t, std::intptr_t, std::intptr_t, int);
    using Deallocate = void(__cdecl*)(void*, std::intptr_t, std::intptr_t);
    if (UsesQt6())
    {
        const auto allocate = reinterpret_cast<Allocate>(GetProcAddress(core,
            "?allocate@QArrayData@@SAPEAXPEAPEAU1@_J11W4AllocationOption@1@@Z"));
        const auto deallocate = reinterpret_cast<Deallocate>(GetProcAddress(core,
            "?deallocate@QArrayData@@SAXPEAU1@_J1@Z"));
        if (allocate == nullptr || deallocate == nullptr) return 18;
        QtByteArray sharedList;
        sharedList.begin = allocate(&sharedList.data, 16, 8, 1, 0);
        sharedList.size = 1;
        if (sharedList.begin == nullptr || !ReadQtValueList(&sharedList, 16, 1, values) ||
            *reinterpret_cast<const int*>(sharedList.data) != 1) return 19;
        deallocate(sharedList.data, 16, 8);

        // Lock a real Qt6 basic mutex and flush synthetic inline frame values.
        unsigned char video[0x400] = {};
        std::uintptr_t mutex = 0;
        auto frameValues = records;
        *reinterpret_cast<int*>(frameValues) = 3;
        *reinterpret_cast<int*>(frameValues + 24) = 4;
        frameValues[VideoQueueEntryReadyOffset] = 1;
        frameValues[24 + VideoQueueEntryReadyOffset] = 1;
        auto queue = reinterpret_cast<QtByteArray*>(video + VideoFrameQueueOffset);
        queue->begin = frameValues;
        queue->size = 2;
        *reinterpret_cast<void**>(video + VideoFrameQueueMutexOffset) = &mutex;
        int dropped = 0;
        if (!ArmDecoderCatchup(video, 50, nullptr, nullptr, dropped) || dropped != 2 ||
            *reinterpret_cast<int*>(frameValues) != -1 || *reinterpret_cast<int*>(frameValues + 24) != -1 ||
            frameValues[VideoQueueEntryReadyOffset] != 0 || frameValues[24 + VideoQueueEntryReadyOffset] != 0)
            return 20;
        InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
        InterlockedExchange(&g_decoderCatchupTargetFrame, -1);

        *reinterpret_cast<int*>(video + VideoCurrentFrameOffset) = 5;
        if (InstallDecoderWorkerTargetPatch() < 0) return 21;
        InterlockedExchangePointer(&g_decoderCatchupVideo, video);
        InterlockedExchange(&g_decoderCatchupTargetFrame, 50);
        if (RunTestThunk(g_decoderWorkerTargetThunk, video, true) != 50 ||
            g_decoderCatchupTargetFrame != -1 ||
            RunTestThunk(g_decoderWorkerTargetThunk, video, true) != 5) return 22;

        auto scaleSlot = reinterpret_cast<void**>(ImageBase() + DecodeScaleSlotRva);
        DWORD protection = 0, ignored = 0;
        VirtualProtect(scaleSlot, sizeof(void*), PAGE_READWRITE, &protection);
        *scaleSlot = reinterpret_cast<void*>(&TestScale);
        VirtualProtect(scaleSlot, sizeof(void*), protection, &ignored);
        if (InstallScaleSkipPatch() < 0) return 23;
        InterlockedExchange(&g_fastForwardTargetFrame, 50);
        if (RunTestThunk(g_decodeScaleThunk, video, false) != 0 || g_skippedScaleCount != 1) return 24;
        *reinterpret_cast<int*>(video + VideoCurrentFrameOffset) = 50;
        if (RunTestThunk(g_decodeScaleThunk, video, false) != 777 || g_skippedScaleCount != 1) return 25;
        InterlockedExchangePointer(&g_decoderCatchupVideo, nullptr);
    }
    std::printf("Qt %d container, string, audio, decoder, seek and fullscreen resolver checks passed.\n",
        UsesQt6() ? 6 : 5);
    return 0;
}
