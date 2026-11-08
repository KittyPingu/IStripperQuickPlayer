// Build with /p:VerifyCompatibility=true. No code is injected into iStripper.
#include "PlaybackBridge.cpp"
#include <gl/GL.h>
#pragma comment(lib, "opengl32.lib")

LRESULT CALLBACK TestArrowCursor(HWND, UINT, WPARAM, LPARAM)
{
    SetCursor(LoadCursorW(nullptr, IDC_ARROW));
    return 0;
}

LRESULT CALLBACK TestOpenGlWrapper(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == g_movieWindowProbeMessage) return 0;
    return CallWindowProcW(&MovieWindowProc, window, message, wParam, lParam);
}

int __cdecl TestScale() { return 777; }
const void* slowSceneCandidate = nullptr;
ULONGLONG sceneLookupDeadline = 0;
bool __cdecl TestSceneInherits(const void* object, const char* className)
{
    if (object == slowSceneCandidate)
    {
        while (GetTickCount64() < sceneLookupDeadline) Sleep(1);
        return false;
    }
    return std::strcmp(className, "ViewModel::ViewModelFullScreenScenes") == 0;
}

bool VerifySceneLookupResume()
{
    const auto slots = static_cast<void**>(VirtualAlloc(nullptr, 256 * 1024,
        MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (slots == nullptr) return false;
    const void* vtable = &sceneLookupDeadline;
    const auto candidate = [&](std::size_t index)
    {
        slots[index] = const_cast<void*>(vtable);
        slots[index + 1] = slots + index + 2;
        slots[index + 2] = slots + index;
    };
    candidate(0);
    candidate(12'000);
    slowSceneCandidate = slots;
    const QtObjectFunctions qt = { &TestSceneInherits, nullptr };
    const auto begin = reinterpret_cast<std::uintptr_t>(slots);
    const auto end = begin + 256 * 1024;
    auto cursor = begin;
    void* object = reinterpret_cast<void*>(1);
    sceneLookupDeadline = GetTickCount64() + 20;
    const HRESULT first = ScanFullscreenScenesViewModel(qt, vtable, cursor,
        end, sceneLookupDeadline, object);
    bool passed = first == HRESULT_FROM_WIN32(ERROR_TIMEOUT) && object == nullptr &&
        cursor > begin && cursor < reinterpret_cast<std::uintptr_t>(slots + 12'000);
    passed = passed && ScanFullscreenScenesViewModel(qt, vtable, cursor,
        end, GetTickCount64() + 1'000, object) == S_OK && object == slots + 12'000;
    // A destroyed controller must be rejected and its replacement discovered.
    slots[12'000] = nullptr;
    candidate(20'000);
    passed = passed && !IsQtObject(qt, object, vtable,
        "ViewModel::ViewModelFullScreenScenes") &&
        ScanFullscreenScenesViewModel(qt, vtable, cursor, end,
            GetTickCount64() + 1'000, object) == S_OK && object == slots + 20'000;
    slots[20'000] = nullptr;
    passed = passed && ScanFullscreenScenesViewModel(qt, vtable, cursor, end,
        GetTickCount64() + 1'000, object) == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) &&
        object == nullptr && cursor == end;
    // A wrap to the beginning can find a controller allocated below the old one.
    candidate(100);
    cursor = begin;
    passed = passed && ScanFullscreenScenesViewModel(qt, vtable, cursor, end,
        GetTickCount64() + 1'000, object) == S_OK && object == slots + 100;
    const auto saved = cursor;
    passed = passed && ScanFullscreenScenesViewModel(qt, vtable, cursor, end,
        0, object) == HRESULT_FROM_WIN32(ERROR_TIMEOUT) && cursor == saved && object == nullptr;
    VirtualFree(slots, 0, MEM_RELEASE);
    slowSceneCandidate = nullptr;
    return passed;
}

int startupMaskReads = 0;
void* __fastcall TestStartupMask(void*, int frame)
{
    static unsigned char emptyMask[8] = {};
    if (frame != startupMaskReads++) return nullptr;
    return emptyMask;
}
void __fastcall TestStartupCorrection(void*, int) {}

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
    if (argc == 2 && wcscmp(argv[1], L"--verify-cpu-hit-mask") == 0)
    {
        HDC source = CreateCompatibleDC(nullptr);
        if (!source) return 145;
        bool passed = true;
        const LONG bitmapHeight = -4;
        {
            BITMAPINFO bitmapInfo{};
            bitmapInfo.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bitmapInfo.bmiHeader.biWidth = 4;
            bitmapInfo.bmiHeader.biHeight = bitmapHeight;
            bitmapInfo.bmiHeader.biPlanes = 1;
            bitmapInfo.bmiHeader.biBitCount = 32;
            void* pixels = nullptr;
            HBITMAP bitmap = CreateDIBSection(source, &bitmapInfo, DIB_RGB_COLORS, &pixels, nullptr, 0);
            if (!bitmap || !pixels) return 146;
            const auto previous = SelectObject(source, bitmap);
            std::memset(pixels, 0, 64);
            auto alpha = static_cast<unsigned char*>(pixels);
            const int firstRow = 1;
            const int secondRow = 2;
            alpha[(firstRow * 4 + 1) * 4 + 3] = 255;
            alpha[(secondRow * 4 + 2) * 4 + 3] = 255;
            const auto mask = CpuMovieHitMask(source, SIZE{ 2, 2 }, POINT{ 1, 1 });
            const RECT bounds = { -200, 100, 0, 300 };
            passed = passed && mask && mask->pixels == std::vector<unsigned char>({ 255, 0, 0, 255 }) &&
                HitMovieMask(*mask, bounds, POINT{ -190, 110 }, false) &&
                !HitMovieMask(*mask, bounds, POINT{ -10, 110 }, false) &&
                HitMovieMask(*mask, bounds, POINT{ -10, 290 }, false) &&
                !CpuMovieHitMask(source, SIZE{ 2, 2 }, POINT{ 3, 3 }) &&
                !CpuMovieHitMask(source, SIZE{ 2, 2 }, POINT{ -1, 0 });
            if (!passed) { std::fprintf(stderr, "CPU bitmap extraction failed.\n"); return 149; }
            {
                const auto previousDpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                WNDCLASSW cls{};
                cls.hInstance = GetModuleHandleW(nullptr);
                cls.lpfnWndProc = DefWindowProcW;
                cls.lpszClassName = L"QuickPlayerCpuTestQWindowToolSaveBits";
                if (!RegisterClassW(&cls) || InstallCpuMovieMaskHook() < 0) return 147;
                HWND windows[2]{};
                POINT destination{ 40, 40 }, origin{ 1, 1 };
                SIZE size{ 2, 2 };
                BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
                UPDATELAYEREDWINDOWINFO update{ sizeof(update), nullptr, &destination, &size,
                    source, &origin, 0, &blend, ULW_ALPHA, nullptr };
                for (int index = 0; index < 2; ++index)
                {
                    windows[index] = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                        cls.lpszClassName, L"", WS_POPUP, 40, 40, 2, 2, nullptr, nullptr, cls.hInstance, nullptr);
                    if (!windows[index]) return 148;
                    g_movieWindows[index].window = windows[index];
                    const BOOL updated = index == 0 ? UpdateLayeredWindowIndirect(windows[index], &update) :
                        UpdateLayeredWindow(windows[index], nullptr, &destination, &size, source, &origin, 0, &blend, ULW_ALPHA);
                    passed = passed && updated &&
                        g_movieWindows[index].cpuMask &&
                        g_movieWindows[index].cpuMask->pixels[0] == (index == 0 ? 255 : 0);
                    if (!passed) { std::fprintf(stderr, "CPU window capture failed (index=%d error=%lu mask=%d).\n", index, GetLastError(), g_movieWindows[index].cpuMask != nullptr); return 149; }
                    ShowWindow(windows[index], SW_SHOWNOACTIVATE);
                    alpha[(firstRow * 4 + 1) * 4 + 3] = 0;
                    alpha[(firstRow * 4 + 2) * 4 + 3] = 255;
                }
                passed = passed && IsPointOverVisibleMoviePixel(windows[0], POINT{ 40, 40 }) &&
                    !IsPointOverVisibleMoviePixel(windows[0], POINT{ 41, 40 }) &&
                    IsPointOverVisibleMoviePixel(windows[1], POINT{ 41, 40 }) &&
                    FindVisibleMovieWindowAtPoint(POINT{ 41, 40 }).window == windows[1];
                if (!passed) { std::fprintf(stderr, "CPU window hit test failed: first=%d firstGap=%d second=%d.\n",
                    IsPointOverVisibleMoviePixel(windows[0], POINT{ 40, 40 }),
                    IsPointOverVisibleMoviePixel(windows[0], POINT{ 41, 40 }),
                    IsPointOverVisibleMoviePixel(windows[1], POINT{ 41, 40 })); return 149; }
                for (int index = 0; index < 2; ++index) { g_movieWindows[index] = {}; DestroyWindow(windows[index]); }
                UnregisterClassW(cls.lpszClassName, cls.hInstance);
                SetThreadDpiAwarenessContext(previousDpi);
            }
            SelectObject(source, previous);
            DeleteObject(bitmap);
        }
        DeleteDC(source);
        if (!passed) return 149;
        std::printf("CPU rendered-alpha capture, crop, orientation and per-window hit checks passed.\n");
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-window-diagnostics") == 0)
    {
        const HINSTANCE instance = GetModuleHandleW(nullptr);
        WNDCLASSW cls{};
        cls.hInstance = instance;
        cls.lpfnWndProc = DefWindowProcW;
        cls.lpszClassName = L"QuickPlayerWindowDiagnosticTest";
        if (!RegisterClassW(&cls)) return 140;
        HWND owner = CreateWindowW(cls.lpszClassName, L"Private title", WS_POPUP,
            0, 0, 32, 32, nullptr, nullptr, instance, nullptr);
        HWND owned = CreateWindowExW(WS_EX_TOOLWINDOW, cls.lpszClassName, L"", WS_POPUP,
            0, 0, 32, 32, owner, nullptr, instance, nullptr);
        HWND child = CreateWindowW(cls.lpszClassName, L"", WS_CHILD,
            0, 0, 16, 16, owned, nullptr, instance, nullptr);
        if (!owner || !owned || !child) return 141;
        WindowDiagnosticSnapshot snapshot;
        CollectTopLevelWindowDiagnostic(owned, reinterpret_cast<LPARAM>(&snapshot));
        wchar_t expected[128]{};
        swprintf_s(expected, L"owner=%p ownerClass=[%s]", owner, cls.lpszClassName);
        bool passed = snapshot.count == 2 && !snapshot.truncated &&
            snapshot.matches == 0 &&
            snapshot.windows.find(expected) != std::wstring::npos &&
            snapshot.windows.find(L"exStyle=0x00000080") != std::wstring::npos &&
            snapshot.windows.find(L"movieMatch=0") != std::wstring::npos &&
            snapshot.windows.find(L"rejectedBy=[class-mismatch;not-layered;has-owner;]") != std::wstring::npos &&
            snapshot.windows.find(L"Private title") == std::wstring::npos;
        for (const auto windowClass : { L"Qt51519QWindowToolSaveBits", L"Qt6120QWindowToolSaveBitsOwnDC" })
        {
            cls.lpszClassName = windowClass;
            cls.style = std::wcsstr(windowClass, L"OwnDC") ? CS_OWNDC : 0;
            if (!RegisterClassW(&cls)) return 143;
            HWND movie = CreateWindowExW(WS_EX_LAYERED, cls.lpszClassName, L"", WS_POPUP,
                0, 0, 32, 32, nullptr, nullptr, instance, nullptr);
            if (!movie) return 144;
            WindowDiagnosticSnapshot matching;
            CollectWindowDiagnostic(movie, reinterpret_cast<LPARAM>(&matching));
            passed = passed && matching.matches == 1 &&
                matching.windows.find(L"movieMatch=1 rejectedBy=[]") != std::wstring::npos;
            SetWindowLongPtrW(movie, GWLP_HWNDPARENT, reinterpret_cast<LONG_PTR>(owner));
            WindowDiagnosticSnapshot hasOwner;
            CollectWindowDiagnostic(movie, reinterpret_cast<LPARAM>(&hasOwner));
            passed = passed && hasOwner.matches == 0 &&
                hasOwner.windows.find(L"rejectedBy=[has-owner;]") != std::wstring::npos;
            SetWindowLongPtrW(movie, GWLP_HWNDPARENT, 0);
            SetWindowLongPtrW(movie, GWL_EXSTYLE, 0);
            WindowDiagnosticSnapshot missingLayered;
            CollectWindowDiagnostic(movie, reinterpret_cast<LPARAM>(&missingLayered));
            passed = passed && missingLayered.matches == 0 &&
                missingLayered.windows.find(L"rejectedBy=[not-layered;]") != std::wstring::npos;
            DestroyWindow(movie);
            UnregisterClassW(cls.lpszClassName, instance);
        }
        snapshot.count = 256;
        const auto length = snapshot.windows.size();
        passed = passed && !CollectWindowDiagnostic(owner, reinterpret_cast<LPARAM>(&snapshot)) &&
            snapshot.truncated && snapshot.windows.size() == length;
        DestroyWindow(child);
        DestroyWindow(owned);
        DestroyWindow(owner);
        UnregisterClassW(L"QuickPlayerWindowDiagnosticTest", instance);
        if (!passed) return 142;
        std::printf("Window diagnostic inventory checks passed.\n");
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-alpha-presentation") == 0)
    {
        const HINSTANCE instance = GetModuleHandleW(nullptr);
        WNDCLASSW cls{};
        cls.hInstance = instance;
        cls.style = CS_OWNDC;
        cls.lpfnWndProc = DefWindowProcW;
        cls.lpszClassName = L"QuickPlayerAlphaPresentationTest";
        if (!RegisterClassW(&cls)) return 132;
        g_movieWindowProbeMessage = RegisterWindowMessageW(L"QuickPlayerAlphaPresentationTest.Probe");
        HWND windows[3]{};
        HDC dcs[3]{};
        HGLRC contexts[3]{};
        WNDPROC originals[3]{};
        for (int index = 0; index < 3; ++index)
        {
            windows[index] = CreateWindowW(cls.lpszClassName, L"", WS_POPUP,
                -32000, -32000, 32, 32, nullptr, nullptr, instance, nullptr);
            if (!windows[index]) return 133;
            // Qt can retain a visible old HWND after dragging; keep all sources logically visible.
            ShowWindow(windows[index], SW_SHOWNOACTIVATE);
            dcs[index] = GetDC(windows[index]);
            PIXELFORMATDESCRIPTOR pixel{};
            pixel.nSize = sizeof(pixel);
            pixel.nVersion = 1;
            pixel.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
            pixel.iPixelType = PFD_TYPE_RGBA;
            pixel.cColorBits = 32;
            pixel.cAlphaBits = 8;
            const int format = ChoosePixelFormat(dcs[index], &pixel);
            if (!format || !SetPixelFormat(dcs[index], format, &pixel)) return 134;
            contexts[index] = wglCreateContext(dcs[index]);
            if (!contexts[index] || !InstallMovieWindowProc(windows[index], true, false)) return 135;
            originals[index] = OriginalMovieWindowProc(windows[index]);
        }
        if (!wglShareLists(contexts[0], contexts[2])) return 139;
        if (!wglMakeCurrent(dcs[1], contexts[1])) return 136;
        // An unrelated object can have the same numeric name in another context.
        unsigned unrelatedTexture = 0;
        glGenTextures(1, &unrelatedTexture);
        glBindTexture(GL_TEXTURE_2D, unrelatedTexture);
        const unsigned char blue[4] = {0, 0, 255, 255};
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, blue);
        glBindTexture(GL_TEXTURE_2D, 0);
        SetOpenGlHdrAlphaAntialiasing(true);
        ResumeOpenGlHdr();
        for (int cycle = 0; cycle < 30; ++cycle)
        {
            const int index = cycle % 3;
            if (!wglMakeCurrent(dcs[index], contexts[index])) return 136;
            glClearColor(cycle % 2 ? 1.f : 0.f, cycle % 2 ? 0.f : 1.f, 0, 1);
            glClear(GL_COLOR_BUFFER_BIT);
            TryEvaluateOpenGlHdr(windows[index], 32, 32);
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                DispatchMessageW(&message);
            while (glGetError() != GL_NO_ERROR) {}
            const long frames = OpenGlHdrFrameCount();
            const bool presented = TryEvaluateOpenGlHdr(windows[index], 32, 32);
            const unsigned error = glGetError();
            if (!presented || frames == OpenGlHdrFrameCount() || error != GL_NO_ERROR)
            {
                std::fprintf(stderr, "Alpha cycle %d: presented=%d, frames=%ld->%ld, GL error=0x%x\n",
                    cycle, presented, frames, OpenGlHdrFrameCount(), error);
                return 137;
            }
            bool active = false;
            const bool probed = TryHasMovieWindowProc(windows[index], active);
            if (!InstallMovieWindowProc(windows[index], probed, active)) return 138;
            if (OriginalMovieWindowProc(windows[index]) != originals[index]) return 140;
            if (index == 1)
            {
                unsigned char pixel[4]{};
                glBindTexture(GL_TEXTURE_2D, unrelatedTexture);
                glGetTexImage(GL_TEXTURE_2D, 0, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
                glBindTexture(GL_TEXTURE_2D, 0);
                if (std::memcmp(pixel, blue, sizeof(blue)) != 0) return 141;
            }
            SendMessageW(windows[index], WM_NULL, 0, 0);
            if (cycle == 14)
                Sleep(600); // A stopped visible owner must also recover without a drag notification.
            else
            {
                SetOpenGlHdrInteractiveMove(true);
                SetOpenGlHdrInteractiveMove(false);
            }
        }
        std::printf("30 alpha presentations across independent/shared contexts and drag resets passed.\n");
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-wheel-activation") == 0)
    {
        g_playerHoverDelay = 1000;
        g_playerHoverWindow = reinterpret_cast<HWND>(1);
        g_playerHoverStarted = GetTickCount64();
        g_playerWheelWhileLocked = 0;
        if (PlayerWheelActivationReady()) return 127;
        g_playerWheelWhileLocked = 1;
        if (!PlayerWheelActivationReady()) return 128;
        g_playerWheelWhileLocked = 0;
        g_playerHoverStarted = GetTickCount64() - 1001;
        if (!PlayerWheelActivationReady()) return 129;
        g_playerHoverWindow = nullptr;
        if (PlayerWheelActivationReady()) return 130;
        g_playerHoverDelay = 0;
        if (!PlayerWheelActivationReady()) return 131;
        std::printf("Wheel activation checks passed.\n");
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-opengl-subclass") == 0)
    {
        const HINSTANCE instance = GetModuleHandleW(nullptr);
        WNDCLASSW cls{};
        cls.hInstance = instance;
        cls.style = CS_OWNDC;
        cls.lpfnWndProc = DefWindowProcW;
        cls.lpszClassName = L"QuickPlayerOpenGlSubclassTest";
        if (!RegisterClassW(&cls)) return 120;
        g_movieWindowProbeMessage = RegisterWindowMessageW(L"QuickPlayerOpenGlSubclassTest.Probe");
        for (int cycle = 0; cycle < 20; ++cycle)
        {
            HWND window = CreateWindowW(cls.lpszClassName, L"", WS_POPUP,
                0, 0, 32, 32, nullptr, nullptr, instance, nullptr);
            if (!window) return 121;
            HDC dc = GetDC(window);
            PIXELFORMATDESCRIPTOR pixel{};
            pixel.nSize = sizeof(pixel);
            pixel.nVersion = 1;
            pixel.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
            pixel.iPixelType = PFD_TYPE_RGBA;
            pixel.cColorBits = 32;
            const int format = ChoosePixelFormat(dc, &pixel);
            if (!format || !SetPixelFormat(dc, format, &pixel)) return 122;
            HGLRC context = wglCreateContext(dc);
            if (!context || !wglMakeCurrent(dc, context)) return 123;
            if (!InstallMovieWindowProc(window, true, false)) return 124;
            for (int update = 0; update < 5; ++update)
            {
                glClearColor(0, .5f, 0, 1);
                glClear(GL_COLOR_BUFFER_BIT);
                SwapBuffers(dc);
                bool active = false;
                const bool probed = TryHasMovieWindowProc(window, active);
                if (!InstallMovieWindowProc(window, probed, active)) return 125;
                SendMessageW(window, WM_NULL, 0, 0);
                wglMakeCurrent(nullptr, nullptr);
                wglDeleteContext(context);
                context = wglCreateContext(dc);
                if (!context || !wglMakeCurrent(dc, context)) return 126;
            }
            wglMakeCurrent(nullptr, nullptr);
            wglDeleteContext(context);
            SetWindowLongPtrW(window, GWLP_WNDPROC,
                reinterpret_cast<LONG_PTR>(OriginalMovieWindowProc(window)));
            RemoveMovieWindow(window);
            ReleaseDC(window, dc);
            DestroyWindow(window);
        }
        UnregisterClassW(cls.lpszClassName, instance);
        std::printf("20 OpenGL windows with 100 context replacements passed.\n");
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-subclass-race") == 0)
    {
        const HINSTANCE instance = GetModuleHandleW(nullptr);
        WNDCLASSW testClass{};
        testClass.hInstance = instance;
        testClass.lpfnWndProc = DefWindowProcW;
        testClass.lpszClassName = L"QuickPlayerSubclassRaceTest";
        if (!RegisterClassW(&testClass)) return 117;
        HWND window = CreateWindowW(testClass.lpszClassName, L"", WS_POPUP,
            0, 0, 10, 10, nullptr, nullptr, instance, nullptr);
        if (!window) return 118;
        const WNDPROC original = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC));
        bool passed = InstallMovieWindowProc(window, true, false) &&
            OriginalMovieWindowProc(window) == original;
        // A second thread's probe said "not installed" before the first installer won.
        passed = passed && InstallMovieWindowProc(window, true, false) &&
            OriginalMovieWindowProc(window) == original;
        passed = passed && InstallMovieWindowProc(window, false, false) &&
            OriginalMovieWindowProc(window) == original;
        if (passed) SendMessageW(window, WM_NULL, 0, 0);
        g_movieWindowProbeMessage = RegisterWindowMessageW(L"QuickPlayerSubclassRaceTest.Probe");
        SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&TestOpenGlWrapper));
        bool active = true;
        const bool probed = TryHasMovieWindowProc(window, active);
        passed = passed && probed && !active && InstallMovieWindowProc(window, probed, active) &&
            OriginalMovieWindowProc(window) == original &&
            GetWindowLongPtrW(window, GWLP_WNDPROC) == reinterpret_cast<LONG_PTR>(&TestOpenGlWrapper);
        if (passed) SendMessageW(window, WM_NULL, 0, 0);
        // A real reset to the class procedure remains repairable.
        SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(original));
        passed = passed && InstallMovieWindowProc(window, true, false) &&
            OriginalMovieWindowProc(window) == original;
        g_nativeDragWindow = window;
        SetMovieWindowClickThrough(window, true);
        passed = passed && (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TRANSPARENT) == 0;
        MSLLHOOKSTRUCT mouse{};
        MovieMouseHook(HC_ACTION, WM_LBUTTONUP, reinterpret_cast<LPARAM>(&mouse));
        passed = passed && g_nativeDragWindow == window;
        MovieWindowProc(window, WM_LBUTTONUP, 0, 0);
        passed = passed && g_nativeDragWindow == nullptr;
        SetMovieWindowClickThrough(window, true);
        passed = passed && (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;
        SetMovieWindowClickThrough(window, false);
        SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(original));
        RemoveMovieWindow(window);
        DestroyWindow(window);
        UnregisterClassW(testClass.lpszClassName, instance);
        if (!passed) return 119;
        std::printf("Subclass installation race checks passed.\n");
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-player-position") == 0)
    {
        const RECT normal = ResetPositionBounds({9000, -2000, 9400, -1400}, {0, 0, 1920, 1080});
        if (normal.left != 760 || normal.top != 480 || normal.right != 1160 || normal.bottom != 1080) return 115;
        const RECT large = ResetPositionBounds({0, 0, 3840, 2160}, {-1920, 0, 0, 1080});
        if (large.left != -1920 || large.top != 0 || large.right != 0 || large.bottom != 1080) return 116;
        std::printf("Player position checks passed.\n");
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-hover-activation") == 0)
    {
        ICONINFO cursorInfo{};
        if (!GetIconInfo(HoverReadyCursor(), &cursorInfo)) return 111;
        const bool customCursor = !cursorInfo.fIcon && cursorInfo.hbmColor != nullptr &&
            HoverReadyCursor() != LoadCursorW(nullptr, IDC_HAND);
        if (cursorInfo.hbmColor) DeleteObject(cursorInfo.hbmColor);
        if (cursorInfo.hbmMask) DeleteObject(cursorInfo.hbmMask);
        if (!customCursor) return 112;
        g_playerLocked = 1;
        g_playerClickThrough = 0;
        g_playerHoverDelay = 1000;
        UpdatePlayerHover(reinterpret_cast<HWND>(1), true);
        if (!PlayerClickThrough() || PlayerHoverReady()) return 101;
        g_playerHoverStarted = GetTickCount64() - 1001;
        if (PlayerClickThrough() || !PlayerHoverReady()) return 102;
        UpdatePlayerHover(nullptr, false);
        if (!PlayerClickThrough() || PlayerHoverReady()) return 103;
        UpdatePlayerHover(reinterpret_cast<HWND>(2), true);
        if (!PlayerClickThrough()) return 104;
        g_playerLocked = 0;
        if (!PlayerClickThrough()) return 105;
        g_playerHoverStarted = GetTickCount64() - 1001;
        if (PlayerClickThrough() || !PlayerHoverReady()) return 108;
        UpdatePlayerHover(nullptr, false);
        if (!PlayerClickThrough() || PlayerHoverReady()) return 109;
        g_playerHoverDelay = 0;
        g_playerLocked = 1;
        if (PlayerClickThrough()) return 106;
        g_playerClickThrough = 1;
        if (!PlayerClickThrough()) return 107;
        g_playerLocked = 0;
        if (PlayerClickThrough() || PlayerHoverReady()) return 110;
        // Simulate Qt restoring the arrow after WM_SETCURSOR in its move handler.
        g_playerHoverDelay = 1000;
        g_playerClickThrough = 0;
        const HWND movie = reinterpret_cast<HWND>(1);
        g_movieWindows[0] = { movie, &TestArrowCursor };
        g_playerHoverWindow = movie;
        for (LONG locked : {0L, 1L})
        {
            g_playerLocked = locked;
            g_playerHoverStarted = GetTickCount64() - 1001;
            MovieWindowProc(movie, WM_MOUSEMOVE, 0, 0);
            if (GetCursor() != HoverReadyCursor()) return 113;
            g_playerHoverStarted = GetTickCount64();
            MovieWindowProc(movie, WM_MOUSEMOVE, 0, 0);
            if (GetCursor() != LoadCursorW(nullptr, IDC_ARROW)) return 114;
        }
        g_movieWindows[0] = {};
        std::printf("Hover activation checks passed.\n");
        return 0;
    }

    if (!VerifySceneLookupResume())
    {
        std::fprintf(stderr, "Fullscreen controller lookup resume checks failed\n");
        return 51;
    }
    if (argc == 2 && wcscmp(argv[1], L"--verify-fullscreen-controller-lookup") == 0)
        return 0;
    if (argc != 2)
    {
        std::fprintf(stderr, "Usage: PlaybackBridgeCompatibilityTests <vghd.exe>\n");
        return 2;
    }

    // Reject a URL for a different take or an unresolved template.
    const std::string roomUrl = "https://example.test/fileaccess/dressingroom/model/room_take1?jwt=test";
    if (!IsDressingRoomStreamUrl(roomUrl, "room_take1") ||
        !IsDressingRoomStreamUrl("https://example.test/fileaccess/wetroom/model/room_take1?jwt=test", "room_take1", true) ||
        IsDressingRoomStreamUrl(roomUrl, "room_take1", true) ||
        IsDressingRoomStreamUrl("https://example.test/fileaccess/wetroom/model/room_take1?jwt=test", "room_take1") ||
        IsDressingRoomStreamUrl(roomUrl, "room_take10") ||
        IsDressingRoomStreamUrl("http://DOMAIN/fileaccess/dressingroom/model/room_take1", "room_take1") ||
        IsDressingRoomStreamUrl("file:///fileaccess/dressingroom/model/room_take1?jwt=test", "room_take1") ||
        IsDressingRoomStreamUrl(roomUrl, "")) return 49;

    unsigned char soundClose[] = { 0x48, 0x8B, 0xD9, 0x33, 0xFF, 0x48, 0x8B, 0x49,
        0x10, 0x48, 0x85, 0xC9, 0x74, 0x17, 0x48, 0x89, 0x7B, 0x10,
        0x48, 0x89, 0x7B, 0x18 };
    if (SoundCloseOutputOffset(soundClose, sizeof(soundClose)) != 0x10 ||
        SoundCloseOutputOffset(soundClose, 13) != 0) return 46;
    soundClose[21] = 0x20;
    if (SoundCloseOutputOffset(soundClose, sizeof(soundClose)) != 0) return 46;

    // Consuming one slot must leave another slot's pending replacement intact.
    const unsigned char searched[] = { 1, 1, 2, 1, 2, 3 };
    const unsigned char pattern[] = { 1, 2, 3 };
    if (FindSequence(searched, sizeof(searched), pattern, sizeof(pattern)) != searched + 3 ||
        FindSequence(searched, 5, pattern, sizeof(pattern)) != nullptr ||
        FindSequence(searched, 2, pattern, sizeof(pattern)) != nullptr ||
        FindSequence(searched, sizeof(searched), pattern, 0) != nullptr) return 43;
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

    // Exercise actual Qt5/Qt6 string assignment and QVariant ownership reads.
    {
        const auto constructObject = reinterpret_cast<void(__fastcall*)(void*, void*)>(
            GetProcAddress(core, "??0QObject@@QEAA@PEAV0@@Z"));
        const auto destroyObject = reinterpret_cast<void(__fastcall*)(void*)>(
            GetProcAddress(core, "??1QObject@@UEAA@XZ"));
        const auto constructString = reinterpret_cast<QStringAnsiConstructor>(
            GetProcAddress(core, "??0QString@@QEAA@PEBD@Z"));
        const auto destroyString = reinterpret_cast<QStringDestructor>(
            GetProcAddress(core, "??1QString@@QEAA@XZ"));
        const auto setName = reinterpret_cast<void(__fastcall*)(void*, const void*)>(
            GetProcAddress(core, "?setObjectName@QObject@@QEAAXAEBVQString@@@Z"));
        const auto boolVariant = reinterpret_cast<void(__fastcall*)(void*, bool)>(
            GetProcAddress(core, "??0QVariant@@QEAA@_N@Z"));
        const auto stringVariant = reinterpret_cast<void(__fastcall*)(void*, const void*)>(
            GetProcAddress(core, "??0QVariant@@QEAA@AEBVQString@@@Z"));
        const auto destroyVariant = reinterpret_cast<void(__fastcall*)(void*)>(
            GetProcAddress(core, "??1QVariant@@QEAA@XZ"));
        const auto setProperty = reinterpret_cast<bool(__fastcall*)(void*, const char*, const void*)>(
            GetProcAddress(core, "?setProperty@QObject@@QEAA_NPEBDAEBVQVariant@@@Z"));
        if (!constructObject || !destroyObject || !constructString || !destroyString ||
            !setName || !boolVariant || !stringVariant || !destroyVariant || !setProperty) return 50;
        alignas(16) unsigned char object[512] = {}, owned[32] = {}, video[32] = {};
        constructObject(object, nullptr);
        QtByteArray name, observed;
        constructString(&name, "https://example.test/fileaccess/wetroom/model/room_take1?jwt=test");
        constructString(&observed, "previous");
        setName(object, &name);
        const bool propertyRead = ReadQtProperty(object, "objectName", &observed) &&
            QtStringUtf8(&observed) == QtStringUtf8(&name);
        boolVariant(owned, true);
        stringVariant(video, &name);
        setProperty(object, "owned", owned);
        setProperty(object, "videoUrl", video);
        const bool ownedRead = ReadWetRoomProperties(object, &observed) &&
            QtStringUtf8(&observed) == QtStringUtf8(&name);
        destroyVariant(owned);
        boolVariant(owned, false);
        setProperty(object, "owned", owned);
        const bool rejected = !ReadWetRoomProperties(object, nullptr);
        destroyVariant(owned);
        destroyVariant(video);
        destroyString(&observed);
        destroyString(&name);
        destroyObject(object);
        if (!propertyRead || !ownedRead || !rejected) return 50;
    }

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

    // Mode observations must refresh wheel routing without losing same-mode sizes.
    g_playerMode = 2;
    g_playerLargeSizePercent = 120;
    if (SetPlayerMode(1) < 0 || g_playerMode != 1 || g_playerLargeSizePercent != 0)
        return 48;
    g_playerLargeSizePercent = 120;
    if (SetPlayerMode(1) < 0 || g_playerLargeSizePercent != 120 ||
        SetPlayerMode(0) != E_INVALIDARG || g_playerMode != 1)
        return 48;
    SetPlayerMode(2);

    // A focus request must never act on a missing window or another process.
    if (FocusHostWindowForFullscreen(nullptr) != E_INVALIDARG ||
        FocusHostWindowForFullscreen(GetDesktopWindow()) != E_INVALIDARG)
        return 35;
    if (!IsHostForeground() &&
        FocusHostForFullscreen() != HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE))
        return 51;

    // Invalid bridge packets must fail before any live object discovery.
    char unterminatedScene[8192];
    std::memset(unterminatedScene, 'x', sizeof(unterminatedScene));
    if (IStripperStartFullscreenScene(0) != E_INVALIDARG ||
        IStripperStartFullscreenScene(reinterpret_cast<SIZE_T>(unterminatedScene)) != E_INVALIDARG)
        return 33;

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

    const char unicodeScene[] = "C:/scenes/Sc\xC3\xA8ne/Sc\xC3\xA8ne.scn";
    construct(&text, unicodeScene);
    const std::string decodedScene = QtStringUtf8(&text);
    destroy(&text);
    if (decodedScene != unicodeScene) return 34;

    wchar_t desktopPath[1024] = {};
    construct(&text, "C:/models/e1234/e1234_01234.vghd");
    bool validDesktopPath = ReadDesktopClipString(&text, desktopPath) &&
        wcscmp(desktopPath, L"e1234\\e1234_01234.vghd") == 0;
    destroy(&text);
    if (!validDesktopPath) return 31;
    construct(&text, "unrelated text");
    validDesktopPath = ReadDesktopClipString(&text, desktopPath);
    destroy(&text);
    if (validDesktopPath || sizeof(DesktopSnapshot) != 2208 ||
        offsetof(DesktopSnapshot, path) != 152 || sizeof(DesktopSelection) != 2072) return 32;

    // Exercise both storage layouts even when only one Qt runtime is installed.
    {
    struct LegacyString { int refs; int size; unsigned int alloc; unsigned int padding;
        std::intptr_t offset; wchar_t text[64]; } legacyString = {};
    wcscpy_s(legacyString.text, L"e1234_01234.vghd");
    legacyString.size = static_cast<int>(wcslen(legacyString.text));
    legacyString.offset = offsetof(LegacyString, text);
    void* legacyStringPointer = &legacyString;
    QtByteArray modernString = {};
    modernString.begin = legacyString.text;
    modernString.size = legacyString.size;
    if (!ReadDesktopClipString(&legacyStringPointer, desktopPath, false) ||
        !ReadDesktopClipString(&modernString, desktopPath, true)) return 33;
    legacyString.size = 1024;
    if (ReadDesktopClipString(&legacyStringPointer, desktopPath, false)) return 34;
    }

    if (!ResolveMovieOffsets() || !ResolveAnimationLayout())
    {
        std::fprintf(stderr, "Movie/layout resolution failed: 0x%lX\n",
            g_movieResolverMask);
        return 12;
    }
    VideoFfmpegVtableRva = FindVtableRva(".?AVVideoFFmpeg@@");
    if (!HasDesktopTransitionSignals()) return 36;
    {
        MovieHitMask mask;
        mask.width = mask.height = 2;
        mask.pixels = { 255, 0, 0, 255 };
        RECT bounds = { -200, 100, 0, 300 };
        if (HitMovieMask(mask, bounds, POINT{ -190, 110 }) ||
            !HitMovieMask(mask, bounds, POINT{ -10, 110 }) ||
            !HitMovieMask(mask, bounds, POINT{ -190, 290 }) ||
            HitMovieMask(mask, bounds, POINT{ -10, 290 }) ||
            HitMovieMask(mask, bounds, POINT{ 0, 110 }) ||
            HitMovieMask(mask, bounds, POINT{ -201, 110 })) return 43;
        mask.pixels.clear();
        if (HitMovieMask(mask, bounds, POINT{ -10, 110 })) return 43;
    }

    {
        g_desktopInstance = 12;
        g_desktopCompletedInstance = 0;
        g_desktopSelection = {};
        DesktopNaturalEnd();
        if (g_desktopCompletedInstance != 12) return 41;
        g_desktopSelection.instance = 12;
        g_desktopSelection.request = 42;
        DesktopNaturalEnd();
        if (g_desktopSelection.request != 42) return 41;
        DesktopSelection late;
        late.instance = 12;
        wcscpy_s(late.path, L"e1234\\e1234_01234.vghd");
        if (!DesktopSelectionCompleted(late)) return 42;
        late.instance = 11;
        if (DesktopSelectionCompleted(late)) return 42;
        late.instance = 0;
        if (DesktopSelectionCompleted(late)) return 42;
        late.instance = 12;
        late.path[0] = 0;
        if (DesktopSelectionCompleted(late)) return 42;
        g_desktopSelection = {};
        g_desktopInstance = g_desktopCompletedInstance = 0;
    }

    if (!HasDesktopMethod(".?AVLive@@", "Live", "checkForcedActor()", false)) return 40;
    if (GetProcAddress(QtCoreModule(), "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBD"
        "W4ConnectionType@Qt@@VQGenericArgument@@333333333@Z") == nullptr) return 40;
    if (IStripperRestoreDesktopPlayer(0) != E_INVALIDARG ||
        IStripperRestoreDesktopPlayer(3) != E_INVALIDARG) return 39;
    if (!HasDesktopMethod(".?AVLiveActor@@", "LiveActor",
        "updateHeightWithoutAnimation()", false)) return 39;
    {
        const wchar_t* windowClass = L"QuickPlayerTestQWindowToolSaveBitsOwnDC";
        const auto previousDpi = SetThreadDpiAwarenessContext(
            DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        WNDCLASSW definition = {};
        definition.style = CS_OWNDC;
        definition.lpfnWndProc = DefWindowProcW;
        definition.hInstance = GetModuleHandleW(nullptr);
        definition.lpszClassName = windowClass;
        if (!RegisterClassW(&definition)) return 38;
        RECT work = {};
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
        HWND window = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST, windowClass, L"", WS_POPUP,
            work.left + 10, work.top + 10, 8, 8, nullptr, nullptr,
            definition.hInstance, nullptr);
        if (!window) { UnregisterClassW(windowClass, definition.hInstance); return 38; }
        SetLayeredWindowAttributes(window, 0, 255, LWA_ALPHA);
        ShowWindow(window, SW_SHOWNOACTIVATE);
        g_movieWindows[0].window = window;
        std::vector<unsigned char> movie(MovieMutexOffset + sizeof(void*));
        std::vector<unsigned char> animation(AnimationInfoOffset + sizeof(void*));
        *reinterpret_cast<void**>(movie.data()) = ImageBase() + MovieVtableRva;
        *reinterpret_cast<void**>(movie.data() + MovieAnimationOffset) = animation.data();
        StoreActiveMovie(movie.data());
        auto mask = std::make_shared<MovieHitMask>();
        mask->movie = movie.data();
        mask->animation = animation.data();
        mask->width = mask->height = 2;
        mask->pixels = { 255, 255, 255, 255 };
        std::atomic_store(&g_movieHitMask, std::shared_ptr<const MovieHitMask>(mask));
        POINT point = { work.left + 12, work.top + 12 };
        auto opaque = FindVisibleMovieWindowAtPoint(point);
        mask = std::make_shared<MovieHitMask>(*mask);
        mask->pixels = { 0, 0, 0, 0 };
        std::atomic_store(&g_movieHitMask, std::shared_ptr<const MovieHitMask>(mask));
        auto transparent = FindVisibleMovieWindowAtPoint(point);
        std::atomic_store(&g_movieHitMask, std::shared_ptr<const MovieHitMask>{});
        InterlockedExchangePointer(&g_activeMovie, nullptr);
        g_movieWindows[0] = {};
        DestroyWindow(window);
        UnregisterClassW(windowClass, definition.hInstance);
        SetThreadDpiAwarenessContext(previousDpi);
        if (opaque.window != window || !opaque.visiblePixel ||
            transparent.window != window || transparent.visiblePixel) return 38;
    }
    {
        const auto movieSize = std::max(MovieAnimationOffset + sizeof(void*),
            std::max(MovieMutexOffset + sizeof(void*),
                std::max(MovieCurrentFrameOffset, MovieStateOffset) + sizeof(int)));
        std::vector<unsigned char> outgoing(movieSize), incoming(movieSize);
        std::vector<unsigned char> animation(std::max(AnimationInfoOffset,
            AnimationSsvOffset) + sizeof(void*));
        unsigned char info[4096] = {};
        for (auto* movie : { outgoing.data(), incoming.data() })
        {
            *reinterpret_cast<void**>(movie) = ImageBase() + MovieVtableRva;
            *reinterpret_cast<void**>(movie + MovieAnimationOffset) = animation.data();
        }
        *reinterpret_cast<void**>(animation.data() + AnimationInfoOffset) = info;
        StoreActiveMovie(outgoing.data());
        MarkMovieConsumed(outgoing.data());
        InterlockedExchange(&g_movieCaptureArmed, 1);
        ObserveDesktopMovie(incoming.data());
        g_seekReadinessAlphaProgress = RequiredAlphaProgressObservations;
        g_seekReadinessWmvProgress = RequiredAlphaProgressObservations;
        ObserveDesktopMovie(incoming.data());
        if (g_seekReadinessAlphaProgress != 0 || g_seekReadinessWmvProgress != 0) return 45;
        CapturingMovieAdvance(outgoing.data());
        if (ActiveMovie() != incoming.data() || g_movieCaptureArmed != 0) return 37;
        *reinterpret_cast<int*>(incoming.data() + MovieStateOffset) = PlayingState;
        *reinterpret_cast<int*>(info + AnimationTotalFramesOffset) = 120;
        *reinterpret_cast<int*>(info + AnimationFramesPerSecondOffset) = 30;
        *reinterpret_cast<int*>(incoming.data() + MovieStateOffset) = PausedState;
        if (IStripperPrepareFastForwardMilliseconds(1000) != HRESULT_FROM_WIN32(ERROR_NOT_READY) ||
            g_fastForwardTargetFrame != -1) return 44;
        *reinterpret_cast<int*>(incoming.data() + MovieStateOffset) = PlayingState;
        const auto instance = g_desktopInstance;
        if (IStripperResetPlaybackSession() < 0 || ActiveMovie() != incoming.data() ||
            g_desktopInstance != instance) return 39;
        *reinterpret_cast<int*>(incoming.data() + MovieStateOffset) = 0;
        if (IStripperResetPlaybackSession() < 0 || ActiveMovie() != nullptr) return 40;
        const std::size_t scanSize = 64 * 1024 * 1024;
        auto scan = static_cast<unsigned char*>(VirtualAlloc(nullptr, scanSize,
            MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
        if (scan == nullptr) return 41;
        auto lateMovie = scan + scanSize - 4096;
        std::memcpy(lateMovie, incoming.data(), incoming.size());
        *reinterpret_cast<int*>(lateMovie + MovieStateOffset) = PlayingState;
        g_movieDiscoveryCursor = reinterpret_cast<std::uintptr_t>(scan);
        const bool bounded = DiscoverActiveMovie(1) == nullptr &&
            g_movieDiscoveryCursor > reinterpret_cast<std::uintptr_t>(scan) &&
            g_movieDiscoveryCursor < reinterpret_cast<std::uintptr_t>(lateMovie);
        const bool resumed = DiscoverActiveMovie() == lateMovie && g_movieDiscoveryCursor == 0;
        VirtualFree(scan, 0, MEM_RELEASE);
        if (!bounded || !resumed)
        {
            std::fprintf(stderr, "Discovery fixture: bounded=%d resumed=%d movieSize=%zu stateOffset=%zu\n",
                bounded, resumed, movieSize, MovieStateOffset);
            return 42;
        }
        InterlockedExchangePointer(&g_activeMovie, nullptr);
        InterlockedExchangePointer(&g_activeAnimation, nullptr);
        InterlockedExchangePointer(&g_consumedMovie, nullptr);
        InterlockedExchangePointer(&g_consumedAnimation, nullptr);
        InterlockedExchangePointer(&g_consumedInfo, nullptr);
        g_desktopMovie = g_desktopAnimation = g_desktopInfo = nullptr;
    }
    unsigned char desktopHeader[256] = {};
    strcpy_s(reinterpret_cast<char*>(desktopHeader), sizeof(desktopHeader), "HD3e1234_01234.vghd");
    if (!DesktopClipIdentity(desktopHeader, desktopPath) ||
        wcscmp(desktopPath, L"e1234\\e1234_01234.vghd") != 0) return 35;
    if (AnimationAlphaEncodingOffset != 0)
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
        for (int sample = 0; sample < 8; ++sample)
            if (ObserveDecodedAlphaProgress(animation.data(), 10)) return 47;
        ResetSeekReadiness();
        for (int frame = 10; frame < 14; frame++)
            if (ObserveDecodedAlphaProgress(animation.data(), frame)) return 31;
        if (!ObserveDecodedAlphaProgress(animation.data(), 14) ||
            CaptureAlphaCheckpoint(animation.data(), 14, 30) != 1) return 32;
        ResetSeekReadiness();
        for (int frame = 0; frame < 4; frame++)
            if (ObserveDecodedAlphaProgress(animation.data(), frame)) return 45;
        if (!ObserveDecodedAlphaProgress(animation.data(), 4)) return 45;
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
        if (AnimationAlphaStartupDecoderRva != 0)
        {
        unsigned char ssv[256] = {};
        void* vtable[10] = {};
        vtable[8] = reinterpret_cast<void*>(&TestStartupMask);
        vtable[9] = reinterpret_cast<void*>(&TestStartupCorrection);
        *reinterpret_cast<void***>(ssv) = vtable;
        ssv[AnimationAlphaStartupFlagOffset] = 1;
        *reinterpret_cast<void**>(animation.data() + AnimationSsvOffset) = ssv;
        *reinterpret_cast<int*>(info + AnimationAlphaEncodingOffset) = 7;
        if (!PrimeAnimationAlphaIndex(animation.data(), 1) || startupMaskReads != 120) return 46;
        startupMaskReads = 0;
        if (!PrimeAnimationAlphaIndex(animation.data(), 1) || startupMaskReads != 120) return 46;
        startupMaskReads = 0;
        if (!PrimeAnimationAlphaIndex(animation.data(), AnimationAlphaStartupFrameLimit + 1) ||
            startupMaskReads != 0) return 46;
        }
        std::printf("Independent/delta alpha readiness and startup checks passed (encoding field %zX).\n",
            AnimationAlphaEncodingOffset);
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
