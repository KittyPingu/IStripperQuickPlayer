#include <Windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <dcomp.h>
#include <d3dcompiler.h>
#include <mutex>
#include <vector>
#include <cwchar>
#include "OpenGlHdr.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dcomp.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace
{
    constexpr unsigned GlTexture2D = 0x0DE1;
    constexpr unsigned GlTextureBinding2D = 0x8069;
    constexpr unsigned WglAccessWriteDiscardNv = 0x0002;

    using WglGetCurrentContextFn = HGLRC(WINAPI*)();
    using WglGetProcAddressFn = PROC(WINAPI*)(LPCSTR);
    using WglDxOpenDeviceFn = HANDLE(WINAPI*)(void*);
    using WglDxCloseDeviceFn = BOOL(WINAPI*)(HANDLE);
    using WglDxRegisterObjectFn = HANDLE(WINAPI*)(HANDLE, void*, unsigned,
        unsigned, unsigned);
    using WglDxUnregisterObjectFn = BOOL(WINAPI*)(HANDLE, HANDLE);
    using WglDxLockObjectsFn = BOOL(WINAPI*)(HANDLE, int, HANDLE*);
    using WglDxUnlockObjectsFn = BOOL(WINAPI*)(HANDLE, int, HANDLE*);
    using GlGenTexturesFn = void(APIENTRY*)(int, unsigned*);
    using GlBindTextureFn = void(APIENTRY*)(unsigned, unsigned);
    using GlGetIntegervFn = void(APIENTRY*)(unsigned, int*);
    using GlReadBufferFn = void(APIENTRY*)(unsigned);
    using GlCopyTexSubImage2DFn = void(APIENTRY*)(unsigned, int, int, int,
        int, int, int, int);
    using GlCopyImageSubDataFn = void(APIENTRY*)(unsigned, unsigned, int,
        int, int, int, unsigned, unsigned, int, int, int, int, int, int, int);
    using RtxCreateFn = void*(__cdecl*)(ID3D11Device*, int, int, int*,
        wchar_t*, int);
    using RtxEvaluateHdrFn = int(__cdecl*)(void*, ID3D11Texture2D*,
        ID3D11Texture2D*, int, int, int, int, int, int, wchar_t*, int);
    using RtxDestroyFn = void(__cdecl*)(void*);

    struct State
    {
        HGLRC glContext = nullptr;
        int width = 0;
        int height = 0;
        int targetWidth = 0;
        int targetHeight = 0;
        ID3D11Device* device = nullptr;
        ID3D11DeviceContext* context = nullptr;
        ID3D11Texture2D* input = nullptr;
        ID3D11Texture2D* alphaCopy = nullptr;
        ID3D11Texture2D* output = nullptr;
        ID3D11ShaderResourceView* inputView = nullptr;
        ID3D11ShaderResourceView* alphaView = nullptr;
        ID3D11ShaderResourceView* outputView = nullptr;
        ID3D11VertexShader* vertexShader = nullptr;
        ID3D11PixelShader* pixelShader = nullptr;
        ID3D11PixelShader* pixelShaderRaw = nullptr;
        ID3D11Buffer* presentationConstants = nullptr;
        ID3D11SamplerState* sampler = nullptr;
        IDXGISwapChain1* swapChain = nullptr;
        IDCompositionDevice* composition = nullptr;
        IDCompositionTarget* compositionTarget = nullptr;
        IDCompositionVisual* visual = nullptr;
        HWND sourceWindow = nullptr;
        WNDPROC sourceWindowProc = nullptr;
        HWND overlayWindow = nullptr;
        HANDLE interopDevice = nullptr;
        HANDLE interopObject = nullptr;
        unsigned glTexture = 0;
        HMODULE trueHdrModule = nullptr;
        HMODULE bridgeModule = nullptr;
        void* rtxSession = nullptr;
        WglDxCloseDeviceFn closeDevice = nullptr;
        WglDxUnregisterObjectFn unregisterObject = nullptr;
        RtxEvaluateHdrFn evaluate = nullptr;
        RtxDestroyFn destroy = nullptr;
    };

    struct CachedSurface
    {
        int width = 0;
        int height = 0;
        HANDLE interopObject = nullptr;
        unsigned glTexture = 0;
        ID3D11Texture2D* input = nullptr;
        ID3D11Texture2D* alphaCopy = nullptr;
        ID3D11Texture2D* output = nullptr;
        ID3D11ShaderResourceView* inputView = nullptr;
        ID3D11ShaderResourceView* alphaView = nullptr;
        ID3D11ShaderResourceView* outputView = nullptr;
    };

    std::mutex stateMutex;
    State state;
    std::vector<CachedSurface> cachedSurfaces;
    volatile LONG status = 0;
    volatile LONG frameCount = 0;
    volatile LONG suspended = 0;
    volatile LONG interactiveMove = 0;
    volatile LONG playerLocked = 0;
    volatile LONG hdrEnabled = 0;
    volatile LONG alphaAntialiasing = 0;
    volatile LONG overlayPositionPending = 0;
    volatile LONG overlayPositionWidth = 0;
    volatile LONG overlayPositionHeight = 0;

    UINT OverlayCreateMessage()
    {
        static const UINT message = RegisterWindowMessageW(
            L"IStripperQuickPlayer.RtxHdrOverlay.Create.v101");
        return message;
    }

    UINT OverlayPositionMessage()
    {
        static const UINT message = RegisterWindowMessageW(
            L"IStripperQuickPlayer.RtxHdrOverlay.Position.v1");
        return message;
    }

    LRESULT CALLBACK OverlayWindowProc(HWND window, UINT message,
        WPARAM wParam, LPARAM lParam)
    {
        HWND source = reinterpret_cast<HWND>(GetWindowLongPtrW(
            window, GWLP_USERDATA));
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (message == WM_SETCURSOR)
        {
            SetCursor(LoadCursorW(nullptr, IDC_ARROW));
            return TRUE;
        }
        if (message == WM_CLOSE)
        {
            InterlockedExchange(&overlayPositionPending, 0);
            DestroyWindow(window);
            return 0;
        }
        if (message == OverlayPositionMessage())
        {
            InterlockedExchange(&overlayPositionPending, 0);
            if (!source || !IsWindow(source) ||
                !IsWindowVisible(source) ||
                InterlockedCompareExchange(&suspended, 0, 0) != 0)
            {
                ShowWindow(window, SW_HIDE);
                return 0;
            }
            POINT origin = {};
            ClientToScreen(source, &origin);
            const int width = InterlockedCompareExchange(
                &overlayPositionWidth, 0, 0);
            const int height = InterlockedCompareExchange(
                &overlayPositionHeight, 0, 0);
            if (width > 0 && height > 0)
            {
                // This procedure owns the overlay HWND, so positioning it
                // here cannot synchronously block iStripper's render thread.
                SetWindowPos(window, source, origin.x, origin.y,
                    width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW |
                        SWP_NOSENDCHANGING);
            }
            return 0;
        }
        if (message == WM_LBUTTONDOWN)
        {
            if (InterlockedCompareExchange(&playerLocked, 0, 0) != 0)
                return 0;
            POINT point = {
                static_cast<short>(LOWORD(lParam)),
                static_cast<short>(HIWORD(lParam))
            };
            ClientToScreen(window, &point);
            if (source && IsWindow(source))
            {
                // Enter the source window's native move loop. Moving it with
                // SetWindowPos only changes the HWND temporarily; Qt retains
                // its old geometry and snaps the show back when drag ends.
                SendMessageW(source, WM_NCLBUTTONDOWN, HTCAPTION,
                    MAKELPARAM(point.x, point.y));
            }
            return 0;
        }
        if ((message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL) &&
            HandleOpenGlHdrMouseWheel(wParam))
        {
            return 0;
        }
        if (source && IsWindow(source) && message >= WM_MOUSEFIRST &&
            message <= WM_MOUSELAST)
        {
            LPARAM translated = lParam;
            if (message != WM_MOUSEWHEEL && message != WM_MOUSEHWHEEL)
            {
                POINT point = {
                    static_cast<short>(LOWORD(lParam)),
                    static_cast<short>(HIWORD(lParam))
                };
                ClientToScreen(window, &point);
                ScreenToClient(source, &point);
                translated = MAKELPARAM(point.x, point.y);
            }
            return SendMessageW(source, message, wParam, translated);
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    HWND CreateOverlayWindowOnOwnerThread(HWND sourceWindow)
    {
        HMODULE module = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&OverlayWindowProc), &module);
        WNDCLASSW windowClass = {};
        windowClass.lpfnWndProc = OverlayWindowProc;
        windowClass.hInstance = module;
        windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        windowClass.lpszClassName = L"IStripperQuickPlayerRtxHdrOverlay";
        RegisterClassW(&windowClass);
        HWND overlay = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT |
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            windowClass.lpszClassName, L"", WS_POPUP, 0, 0, 1, 1,
            nullptr, nullptr, module, nullptr);
        if (overlay)
        {
            // Top-level WS_EX_TRANSPARENT windows only receive true
            // cross-process hit-test transparency when they are layered.
            // The DComp swap chain remains responsible for per-pixel alpha;
            // this constant alpha merely enables the documented HWND input
            // behavior without changing the rendered image.
            SetLayeredWindowAttributes(overlay, 0, 255, LWA_ALPHA);
            SetWindowLongPtrW(overlay, GWLP_USERDATA,
                reinterpret_cast<LONG_PTR>(sourceWindow));
            if ((GetWindowLongPtrW(sourceWindow, GWL_EXSTYLE) &
                    WS_EX_TOPMOST) != 0)
            {
                // Qt can leave WS_EX_TOPMOST set while its show HWND is
                // actually in the normal z-order band. Promote the visual
                // surface first and the cleared Qt input/control window
                // second, producing an adjacent topmost pair with Qt above.
                SetWindowPos(overlay, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE |
                    SWP_NOSENDCHANGING);
                SetWindowPos(sourceWindow, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE |
                    SWP_NOSENDCHANGING);
            }
        }
        return overlay;
    }

    LRESULT CALLBACK SourceWindowProc(HWND window, UINT message,
        WPARAM wParam, LPARAM lParam)
    {
        if (message == OverlayCreateMessage())
        {
            HWND overlay = state.overlayWindow;
            if (!overlay || !IsWindow(overlay))
            {
                overlay = CreateOverlayWindowOnOwnerThread(window);
                InterlockedExchange(&overlayPositionPending, 0);
                InterlockedExchangePointer(
                    reinterpret_cast<PVOID volatile*>(&state.overlayWindow),
                    overlay);
            }
            return reinterpret_cast<LRESULT>(overlay);
        }
        if (message == WM_MOUSEACTIVATE)
            return MA_NOACTIVATE;
        if (message == WM_SETCURSOR)
        {
            SetCursor(LoadCursorW(nullptr, IDC_ARROW));
            return TRUE;
        }
        WNDPROC original = state.sourceWindowProc;
        const LRESULT result = original
            ? CallWindowProcW(original, window, message, wParam, lParam)
            : DefWindowProcW(window, message, wParam, lParam);
        // Qt can call SetCursor directly while processing mouse movement,
        // after WM_SETCURSOR has already returned. Apply our cursor last so
        // its transient busy/pointing-hand cursors cannot leak through.
        if (message == WM_MOUSEMOVE)
            SetCursor(LoadCursorW(nullptr, IDC_ARROW));
        return result;
    }

    void Log(const wchar_t* message)
    {
        wchar_t path[MAX_PATH] = {};
        DWORD length = GetTempPathW(ARRAYSIZE(path), path);
        if (!length || length >= ARRAYSIZE(path) ||
            wcscat_s(path, L"IstripperQuickPlayer-rtx-hdr.log")) return;
        HANDLE file = CreateFileW(path, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        wchar_t line[640] = {};
        int count = swprintf_s(line, L"pid=%lu %ls\r\n",
            GetCurrentProcessId(), message);
        DWORD written = 0;
        if (count > 0) WriteFile(file, line,
            static_cast<DWORD>(count * sizeof(wchar_t)), &written, nullptr);
        CloseHandle(file);
    }

    template<typename T>
    void ReleaseCom(T*& value)
    {
        if (value) value->Release();
        value = nullptr;
    }

    void CacheActiveSurface()
    {
        if (!state.interopObject) return;
        cachedSurfaces.push_back({
            state.width, state.height, state.interopObject, state.glTexture,
            state.input, state.alphaCopy, state.output, state.inputView,
            state.alphaView, state.outputView });
        state.interopObject = nullptr;
        state.glTexture = 0;
        state.input = nullptr;
        state.alphaCopy = nullptr;
        state.output = nullptr;
        state.inputView = nullptr;
        state.alphaView = nullptr;
        state.outputView = nullptr;
        state.width = state.height = 0;
    }

    bool ActivateCachedSurface(int width, int height)
    {
        for (auto entry = cachedSurfaces.begin();
            entry != cachedSurfaces.end(); ++entry)
        {
            if (entry->width != width || entry->height != height) continue;
            state.width = entry->width;
            state.height = entry->height;
            state.interopObject = entry->interopObject;
            state.glTexture = entry->glTexture;
            state.input = entry->input;
            state.alphaCopy = entry->alphaCopy;
            state.output = entry->output;
            state.inputView = entry->inputView;
            state.alphaView = entry->alphaView;
            state.outputView = entry->outputView;
            cachedSurfaces.erase(entry);
            return true;
        }
        return false;
    }

    void ReleaseCachedSurfaces()
    {
        for (auto& surface : cachedSurfaces)
        {
            if (surface.interopObject && state.unregisterObject)
                state.unregisterObject(
                    state.interopDevice, surface.interopObject);
            ReleaseCom(surface.outputView);
            ReleaseCom(surface.alphaView);
            ReleaseCom(surface.inputView);
            ReleaseCom(surface.output);
            ReleaseCom(surface.alphaCopy);
            ReleaseCom(surface.input);
        }
        cachedSurfaces.clear();
    }

    void ReleasePresentationState(bool restoreWindow = true)
    {
        ReleaseCom(state.visual);
        ReleaseCom(state.compositionTarget);
        ReleaseCom(state.composition);
        ReleaseCom(state.swapChain);
        HWND overlay = static_cast<HWND>(InterlockedExchangePointer(
            reinterpret_cast<PVOID volatile*>(&state.overlayWindow), nullptr));
        if (overlay && IsWindow(overlay)) PostMessageW(overlay, WM_CLOSE, 0, 0);
        if (restoreWindow)
        {
            if (state.sourceWindow && state.sourceWindowProc &&
                IsWindow(state.sourceWindow))
                SetWindowLongPtrW(state.sourceWindow, GWLP_WNDPROC,
                    reinterpret_cast<LONG_PTR>(state.sourceWindowProc));
            state.sourceWindowProc = nullptr;
            state.sourceWindow = nullptr;
        }
    }

    void ReleaseSurfaceState(bool preserveWindow = false)
    {
        if (state.interopObject && state.unregisterObject)
            state.unregisterObject(state.interopDevice, state.interopObject);
        state.interopObject = nullptr;
        ReleasePresentationState(!preserveWindow);
        ReleaseCom(state.sampler);
        ReleaseCom(state.presentationConstants);
        ReleaseCom(state.pixelShaderRaw);
        ReleaseCom(state.pixelShader);
        ReleaseCom(state.vertexShader);
        ReleaseCom(state.outputView);
        ReleaseCom(state.alphaView);
        ReleaseCom(state.inputView);
        ReleaseCom(state.output);
        ReleaseCom(state.alphaCopy);
        ReleaseCom(state.input);
        state.glContext = nullptr;
        state.glTexture = 0;
        state.width = state.height = 0;
        state.targetWidth = state.targetHeight = 0;
    }

    void ReleaseState()
    {
        ReleaseSurfaceState();
        ReleaseCachedSurfaces();
        if (state.rtxSession && state.destroy) state.destroy(state.rtxSession);
        state.rtxSession = nullptr;
        if (state.interopDevice && state.closeDevice)
            state.closeDevice(state.interopDevice);
        state.interopDevice = nullptr;
        ReleaseCom(state.context);
        ReleaseCom(state.device);
        if (state.bridgeModule) FreeLibrary(state.bridgeModule);
        state.bridgeModule = nullptr;
        if (state.trueHdrModule) FreeLibrary(state.trueHdrModule);
        state.trueHdrModule = nullptr;
        state = {};
    }

    bool ModuleDirectory(wchar_t* path, size_t capacity)
    {
        HMODULE module = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&ReleaseOpenGlHdr), &module))
            return false;
        DWORD length = GetModuleFileNameW(module, path,
            static_cast<DWORD>(capacity));
        if (!length || length >= capacity) return false;
        wchar_t* slash = wcsrchr(path, L'\\');
        if (!slash) return false;
        slash[1] = L'\0';
        return true;
    }

    bool EnsureRtxSession()
    {
        if (state.rtxSession) return true;
        if (!state.device) return false;
        wchar_t directory[MAX_PATH] = {};
        wchar_t path[MAX_PATH] = {};
        if (!ModuleDirectory(directory, ARRAYSIZE(directory))) return false;
        if (wcscpy_s(path, directory) ||
            wcscat_s(path, L"nvngx_truehdr.dll")) return false;
        if (!state.trueHdrModule) state.trueHdrModule = LoadLibraryW(path);
        if (wcscpy_s(path, directory) ||
            wcscat_s(path, L"IStripperRtxVideoBridge64.dll")) return false;
        if (!state.bridgeModule) state.bridgeModule = LoadLibraryW(path);
        if (!state.bridgeModule) return false;
        auto create = reinterpret_cast<RtxCreateFn>(GetProcAddress(
            state.bridgeModule, "RtxVideo_CreateD3D11"));
        state.evaluate = reinterpret_cast<RtxEvaluateHdrFn>(GetProcAddress(
            state.bridgeModule, "RtxVideo_EvaluateTrueHdrD3D11"));
        state.destroy = reinterpret_cast<RtxDestroyFn>(GetProcAddress(
            state.bridgeModule, "RtxVideo_Destroy"));
        if (!create || !state.evaluate || !state.destroy) return false;
        int features = 0;
        wchar_t error[512] = {};
        state.rtxSession = create(state.device, 0, 1, &features,
            error, ARRAYSIZE(error));
        if (state.rtxSession && (features & 2)) return true;
        if (state.rtxSession && state.destroy) state.destroy(state.rtxSession);
        state.rtxSession = nullptr;
        return false;
    }

    bool CreateOverlay(int width, int height, bool flipVertical)
    {
        HWND overlay = state.overlayWindow;
        if (!overlay || !IsWindow(overlay)) return false;
        IDXGIDevice* dxgiDevice = nullptr;
        IDXGIFactory2* factory = nullptr;
        HRESULT result = state.device->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
        if (SUCCEEDED(result)) result = CreateDXGIFactory2(
            0, IID_PPV_ARGS(&factory));
        DXGI_SWAP_CHAIN_DESC1 description = {};
        description.Width = static_cast<UINT>(width);
        description.Height = static_cast<UINT>(height);
        description.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        description.SampleDesc.Count = 1;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.BufferCount = 2;
        description.Scaling = DXGI_SCALING_STRETCH;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        description.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        if (SUCCEEDED(result)) result = factory->CreateSwapChainForComposition(
            state.device, &description, nullptr, &state.swapChain);
        IDXGISwapChain3* swapChain3 = nullptr;
        if (SUCCEEDED(result)) result = state.swapChain->QueryInterface(
            IID_PPV_ARGS(&swapChain3));
        if (SUCCEEDED(result)) result = swapChain3->SetColorSpace1(
            DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
        if (SUCCEEDED(result)) result = DCompositionCreateDevice(dxgiDevice,
            __uuidof(IDCompositionDevice),
            reinterpret_cast<void**>(&state.composition));
        // Keep the HDR presentation in its own HWND immediately behind the
        // Qt show/control windows. Its input transparency is driven from the
        // decoded alpha mask, independently of the cleared Qt framebuffer.
        if (SUCCEEDED(result)) result = state.composition->CreateTargetForHwnd(
            overlay, TRUE, &state.compositionTarget);
        if (SUCCEEDED(result)) result = state.composition->CreateVisual(
            &state.visual);
        if (SUCCEEDED(result)) result = state.visual->SetContent(state.swapChain);
        if (SUCCEEDED(result)) result = state.compositionTarget->SetRoot(
            state.visual);
        if (SUCCEEDED(result)) result = state.composition->Commit();
        if (swapChain3) swapChain3->Release();
        if (factory) factory->Release();
        if (dxgiDevice) dxgiDevice->Release();
        if (FAILED(result)) return false;

        static const char shaderNormal[] =
            "struct O{float4 p:SV_POSITION;float2 uv:TEXCOORD0;};"
            "O VS(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);"
            "o.uv=uv;o.p=float4(uv.x*2-1,1-uv.y*2,0,1);return o;}"
            "Texture2D<float4> H:register(t0);Texture2D<float4> S:register(t1);"
            "cbuffer M:register(b0){float hdr;float3 pad;}"
            "SamplerState L:register(s0);"
            "float3 Lin(float3 c){return lerp(pow((c+.055)/1.055,2.4),"
            "c/12.92,step(c,.04045));}"
            "float4 PS(O i):SV_TARGET{float2 uv=i.uv;"
            "float3 h=max(H.Sample(L,uv).rgb,0);float a=S.Sample(L,uv).a;"
            "h=hdr>.5?h:Lin(saturate(h));"
            "return float4(h*a,a);}";
        static const char shaderNormalAa[] =
            "struct O{float4 p:SV_POSITION;float2 uv:TEXCOORD0;};"
            "O VS(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);"
            "o.uv=uv;o.p=float4(uv.x*2-1,1-uv.y*2,0,1);return o;}"
            "Texture2D<float4> H:register(t0);Texture2D<float4> S:register(t1);"
            "cbuffer M:register(b0){float hdr;float3 pad;}"
            "SamplerState L:register(s0);"
            "float3 Lin(float3 c){return lerp(pow((c+.055)/1.055,2.4),"
            "c/12.92,step(c,.04045));}"
            "float AA(float a){float lo=1.0/255.0,hi=254.0/255.0;"
            "float w=max(fwidth(a),1.0/255.0);"
            "float v=smoothstep(lo-w,lo+w,a);"
            "float o=smoothstep(hi-w,hi+w,a);return lerp(a*v,1,o);}"
            "float4 PS(O i):SV_TARGET{float2 uv=i.uv;"
            "float3 h=max(H.Sample(L,uv).rgb,0);float a=AA(S.Sample(L,uv).a);"
            "h=hdr>.5?h:Lin(saturate(h));"
            "return float4(h*a,a);}";
        static const char shaderFlipped[] =
            "struct O{float4 p:SV_POSITION;float2 uv:TEXCOORD0;};"
            "O VS(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);"
            "o.uv=uv;o.p=float4(uv.x*2-1,1-uv.y*2,0,1);return o;}"
            "Texture2D<float4> H:register(t0);Texture2D<float4> S:register(t1);"
            "cbuffer M:register(b0){float hdr;float3 pad;}"
            "SamplerState L:register(s0);"
            "float3 Lin(float3 c){return lerp(pow((c+.055)/1.055,2.4),"
            "c/12.92,step(c,.04045));}"
            "float4 PS(O i):SV_TARGET{float2 uv=float2(i.uv.x,1-i.uv.y);"
            "float3 h=max(H.Sample(L,uv).rgb,0);float a=S.Sample(L,uv).a;"
            "h=hdr>.5?h:Lin(saturate(h));"
            "return float4(h*a,a);}";
        static const char shaderFlippedAa[] =
            "struct O{float4 p:SV_POSITION;float2 uv:TEXCOORD0;};"
            "O VS(uint id:SV_VertexID){O o;float2 uv=float2((id<<1)&2,id&2);"
            "o.uv=uv;o.p=float4(uv.x*2-1,1-uv.y*2,0,1);return o;}"
            "Texture2D<float4> H:register(t0);Texture2D<float4> S:register(t1);"
            "cbuffer M:register(b0){float hdr;float3 pad;}"
            "SamplerState L:register(s0);"
            "float3 Lin(float3 c){return lerp(pow((c+.055)/1.055,2.4),"
            "c/12.92,step(c,.04045));}"
            "float AA(float a){float lo=1.0/255.0,hi=254.0/255.0;"
            "float w=max(fwidth(a),1.0/255.0);"
            "float v=smoothstep(lo-w,lo+w,a);"
            "float o=smoothstep(hi-w,hi+w,a);return lerp(a*v,1,o);}"
            "float4 PS(O i):SV_TARGET{float2 uv=float2(i.uv.x,1-i.uv.y);"
            "float3 h=max(H.Sample(L,uv).rgb,0);float a=AA(S.Sample(L,uv).a);"
            "h=hdr>.5?h:Lin(saturate(h));"
            "return float4(h*a,a);}";
        const char* shader = flipVertical ? shaderFlipped : shaderNormal;
        const char* shaderAa = flipVertical ? shaderFlippedAa : shaderNormalAa;
        ID3DBlob* vertexCode = nullptr;
        ID3DBlob* pixelCode = nullptr;
        ID3DBlob* pixelCodeAa = nullptr;
        ID3DBlob* errors = nullptr;
        result = D3DCompile(shader, strlen(shader) + 1, "OpenGlHdr.hlsl", nullptr,
            nullptr, "VS", "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
            &vertexCode, &errors);
        if (errors) errors->Release();
        errors = nullptr;
        if (SUCCEEDED(result)) result = D3DCompile(shader, strlen(shader) + 1,
            "OpenGlHdr.hlsl", nullptr, nullptr, "PS", "ps_5_0",
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &pixelCode, &errors);
        if (errors) errors->Release();
        errors = nullptr;
        if (SUCCEEDED(result)) result = D3DCompile(shaderAa,
            strlen(shaderAa) + 1, "OpenGlHdr.hlsl", nullptr, nullptr, "PS",
            "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &pixelCodeAa,
            &errors);
        if (errors) errors->Release();
        if (SUCCEEDED(result) && !state.vertexShader)
            result = state.device->CreateVertexShader(
                vertexCode->GetBufferPointer(), vertexCode->GetBufferSize(),
                nullptr, &state.vertexShader);
        if (SUCCEEDED(result) && !state.pixelShaderRaw)
            result = state.device->CreatePixelShader(
                pixelCode->GetBufferPointer(), pixelCode->GetBufferSize(),
                nullptr, &state.pixelShaderRaw);
        if (SUCCEEDED(result) && !state.pixelShader)
            result = state.device->CreatePixelShader(
                pixelCodeAa->GetBufferPointer(), pixelCodeAa->GetBufferSize(),
                nullptr, &state.pixelShader);
        D3D11_BUFFER_DESC constants = {};
        constants.ByteWidth = 16;
        constants.Usage = D3D11_USAGE_DEFAULT;
        constants.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        if (SUCCEEDED(result) && !state.presentationConstants)
            result = state.device->CreateBuffer(
                &constants, nullptr, &state.presentationConstants);
        if (vertexCode) vertexCode->Release();
        if (pixelCode) pixelCode->Release();
        if (pixelCodeAa) pixelCodeAa->Release();
        if (SUCCEEDED(result) && !state.inputView)
            result = state.device->CreateShaderResourceView(
                state.input, nullptr, &state.inputView);
        if (SUCCEEDED(result) && !state.alphaView)
            result = state.device->CreateShaderResourceView(
                state.alphaCopy, nullptr, &state.alphaView);
        if (SUCCEEDED(result) && !state.outputView)
            result = state.device->CreateShaderResourceView(
                state.output, nullptr, &state.outputView);
        D3D11_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW =
            D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        if (SUCCEEDED(result) && !state.sampler)
            result = state.device->CreateSamplerState(&sampler, &state.sampler);
        if (FAILED(result)) return false;
        return true;
    }

    void RequestOverlayWindow()
    {
        if (state.sourceWindow && IsWindow(state.sourceWindow))
            PostMessageW(state.sourceWindow, OverlayCreateMessage(), 0, 0);
    }

    bool EnsurePresentation(bool flipVertical)
    {
        if (state.swapChain) return true;
        if (!state.overlayWindow || !IsWindow(state.overlayWindow))
        {
            RequestOverlayWindow();
            return false;
        }
        return CreateOverlay(state.targetWidth, state.targetHeight,
            flipVertical);
    }

    void PositionOverlay()
    {
        HWND overlay = state.overlayWindow;
        if (!overlay || !IsWindow(overlay)) return;
        if (InterlockedCompareExchange(&suspended, 0, 0) != 0 ||
            !state.sourceWindow || !IsWindowVisible(state.sourceWindow))
        {
            ShowWindowAsync(overlay, SW_HIDE);
            return;
        }
        InterlockedExchange(&overlayPositionWidth, state.targetWidth);
        InterlockedExchange(&overlayPositionHeight, state.targetHeight);
        if (InterlockedExchange(&overlayPositionPending, 1) == 0)
        {
            // The OpenGL render thread must never synchronously position a
            // GUI-thread HWND: Qt can wait for this render thread during clip
            // or drag transitions, producing an AB/BA deadlock.
            if (!PostMessageW(overlay, OverlayPositionMessage(), 0, 0))
                InterlockedExchange(&overlayPositionPending, 0);
        }
    }

    bool PresentOutput(int width, int height)
    {
        const bool useHdr = InterlockedCompareExchange(&hdrEnabled, 0, 0) != 0;
        ID3D11ShaderResourceView* colorView = useHdr
            ? state.outputView : state.inputView;
        if (!state.swapChain || !colorView || !state.alphaView ||
            !state.vertexShader || !state.pixelShader || !state.pixelShaderRaw ||
            !state.presentationConstants)
            return false;
        ID3D11Texture2D* backBuffer = nullptr;
        if (FAILED(state.swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))))
            return false;
        ID3D11RenderTargetView* target = nullptr;
        HRESULT result = state.device->CreateRenderTargetView(
            backBuffer, nullptr, &target);
        if (SUCCEEDED(result))
        {
            state.context->OMSetRenderTargets(1, &target, nullptr);
            const FLOAT transparent[] = { 0, 0, 0, 0 };
            state.context->ClearRenderTargetView(target, transparent);
            D3D11_VIEWPORT viewport = { 0, 0, static_cast<float>(width),
                static_cast<float>(height), 0, 1 };
            state.context->RSSetViewports(1, &viewport);
            state.context->IASetPrimitiveTopology(
                D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            state.context->VSSetShader(state.vertexShader, nullptr, 0);
            ID3D11PixelShader* shader = InterlockedCompareExchange(
                &alphaAntialiasing, 0, 0) != 0
                ? state.pixelShader : state.pixelShaderRaw;
            state.context->PSSetShader(shader, nullptr, 0);
            const float mode[] = { useHdr ? 1.0f : 0.0f, 0, 0, 0 };
            state.context->UpdateSubresource(
                state.presentationConstants, 0, nullptr, mode, 0, 0);
            state.context->PSSetConstantBuffers(
                0, 1, &state.presentationConstants);
            ID3D11ShaderResourceView* views[] = {
                colorView, state.alphaView };
            state.context->PSSetShaderResources(0, 2, views);
            state.context->PSSetSamplers(0, 1, &state.sampler);
            state.context->Draw(3, 0);
            ID3D11ShaderResourceView* empty[] = { nullptr, nullptr };
            state.context->PSSetShaderResources(0, 2, empty);
            state.context->OMSetRenderTargets(0, nullptr, nullptr);
        }
        if (target) target->Release();
        backBuffer->Release();
        if (FAILED(result)) return false;
        PositionOverlay();
        return SUCCEEDED(state.swapChain->Present(1, 0));
    }

    bool RebindPresentation(HWND sourceWindow, int targetWidth,
        int targetHeight, HGLRC currentContext)
    {
        HWND previousWindow = state.sourceWindow;
        ReleasePresentationState();
        SetLastError(ERROR_SUCCESS);
        const LONG_PTR original = SetWindowLongPtrW(sourceWindow, GWLP_WNDPROC,
            reinterpret_cast<LONG_PTR>(&SourceWindowProc));
        if (original == 0 && GetLastError() != ERROR_SUCCESS)
            return false;
        state.sourceWindow = sourceWindow;
        state.sourceWindowProc = reinterpret_cast<WNDPROC>(original);
        state.glContext = currentContext;
        state.targetWidth = targetWidth;
        state.targetHeight = targetHeight;
        RequestOverlayWindow();
        wchar_t message[192] = {};
        swprintf_s(message,
            L"RTX HDR presentation rebound from hwnd=%p to hwnd=%p at %dx%d",
            previousWindow, sourceWindow, targetWidth, targetHeight);
        Log(message);
        return true;
    }

    bool Initialize(HWND sourceWindow, int width, int height,
        int targetWidth, int targetHeight, HGLRC currentContext,
        HMODULE openGl, WglGetProcAddressFn wglProc)
    {
        // iStripper creates a new Qt/OpenGL HWND for each clip. Those contexts
        // share textures, so a same-sized clip change only needs a new DComp
        // target. Target-only resizing also does not require new interop.
        if (state.interopObject && state.sourceWindow != sourceWindow &&
            state.width == width && state.height == height)
            return RebindPresentation(sourceWindow, targetWidth, targetHeight,
                currentContext);
        if (state.interopObject && state.sourceWindow == sourceWindow &&
            state.width == width && state.height == height &&
            (state.targetWidth != targetWidth ||
                state.targetHeight != targetHeight))
            return RebindPresentation(sourceWindow, targetWidth, targetHeight,
                currentContext);

        const bool preserveWindow = state.sourceWindow == sourceWindow &&
            state.sourceWindowProc && IsWindow(sourceWindow);
        if (state.interopObject)
        {
            // NVIDIA's WGL unregister can deadlock against Qt's render thread
            // during a clip/size transition. Keep each size registered and
            // reuse it when iStripper switches between small and large shows.
            ReleasePresentationState(!preserveWindow);
            CacheActiveSurface();
            if (ActivateCachedSurface(width, height))
            {
                state.glContext = currentContext;
                state.targetWidth = targetWidth;
                state.targetHeight = targetHeight;
                if (!preserveWindow)
                {
                    state.sourceWindow = sourceWindow;
                    SetLastError(ERROR_SUCCESS);
                    const LONG_PTR original = SetWindowLongPtrW(sourceWindow,
                        GWLP_WNDPROC,
                        reinterpret_cast<LONG_PTR>(&SourceWindowProc));
                    if (original == 0 && GetLastError() != ERROR_SUCCESS)
                        return false;
                    state.sourceWindowProc =
                        reinterpret_cast<WNDPROC>(original);
                }
                RequestOverlayWindow();
                wchar_t message[128] = {};
                swprintf_s(message,
                    L"RTX HDR cached surface reused at %dx%d", width, height);
                Log(message);
                return true;
            }
        }
        else
        {
            ReleaseSurfaceState(preserveWindow);
        }
        auto openDevice = reinterpret_cast<WglDxOpenDeviceFn>(
            wglProc("wglDXOpenDeviceNV"));
        state.closeDevice = reinterpret_cast<WglDxCloseDeviceFn>(
            wglProc("wglDXCloseDeviceNV"));
        auto registerObject = reinterpret_cast<WglDxRegisterObjectFn>(
            wglProc("wglDXRegisterObjectNV"));
        state.unregisterObject = reinterpret_cast<WglDxUnregisterObjectFn>(
            wglProc("wglDXUnregisterObjectNV"));
        if (!openDevice || !state.closeDevice || !registerObject ||
            !state.unregisterObject) return false;

        if (!state.device)
        {
            D3D_FEATURE_LEVEL levels[] = {
                D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
            if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE,
                    nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
                    ARRAYSIZE(levels), D3D11_SDK_VERSION, &state.device,
                    nullptr, &state.context))) return false;
        }

        D3D11_TEXTURE2D_DESC texture = {};
        texture.Width = static_cast<UINT>(width);
        texture.Height = static_cast<UINT>(height);
        texture.MipLevels = texture.ArraySize = 1;
        texture.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        texture.SampleDesc.Count = 1;
        texture.Usage = D3D11_USAGE_DEFAULT;
        texture.BindFlags = D3D11_BIND_SHADER_RESOURCE |
            D3D11_BIND_RENDER_TARGET | D3D11_BIND_UNORDERED_ACCESS;
        if (FAILED(state.device->CreateTexture2D(
                &texture, nullptr, &state.input))) return false;
        if (FAILED(state.device->CreateTexture2D(
                &texture, nullptr, &state.alphaCopy))) return false;
        texture.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        if (FAILED(state.device->CreateTexture2D(
                &texture, nullptr, &state.output))) return false;

        auto genTextures = reinterpret_cast<GlGenTexturesFn>(
            GetProcAddress(openGl, "glGenTextures"));
        auto bindTexture = reinterpret_cast<GlBindTextureFn>(
            GetProcAddress(openGl, "glBindTexture"));
        auto getInteger = reinterpret_cast<GlGetIntegervFn>(
            GetProcAddress(openGl, "glGetIntegerv"));
        if (!genTextures || !bindTexture || !getInteger) return false;
        int previousTexture = 0;
        getInteger(GlTextureBinding2D, &previousTexture);
        genTextures(1, &state.glTexture);
        bindTexture(GlTexture2D, state.glTexture);
        if (!state.interopDevice) state.interopDevice = openDevice(state.device);
        if (!state.interopDevice) return false;
        state.interopObject = registerObject(state.interopDevice, state.input,
            state.glTexture, GlTexture2D, WglAccessWriteDiscardNv);
        bindTexture(GlTexture2D, static_cast<unsigned>(previousTexture));
        if (!state.interopObject) return false;
        state.glContext = currentContext;
        state.width = width;
        state.height = height;
        state.targetWidth = targetWidth;
        state.targetHeight = targetHeight;
        if (!preserveWindow)
        {
            state.sourceWindow = sourceWindow;
            SetLastError(ERROR_SUCCESS);
            const LONG_PTR original = SetWindowLongPtrW(sourceWindow,
                GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&SourceWindowProc));
            if (original == 0 && GetLastError() != ERROR_SUCCESS)
                return false;
            state.sourceWindowProc = reinterpret_cast<WNDPROC>(original);
        }
        RequestOverlayWindow();
        wchar_t message[128] = {};
        swprintf_s(message, L"OpenGL to D3D11 video bridge initialized at %dx%d",
            width, height);
        Log(message);
        return true;
    }
}

bool TryEvaluateOpenGlHdr(HWND window, int width, int height)
{
    if (InterlockedCompareExchange(&suspended, 0, 0) != 0) return false;
    if (InterlockedCompareExchange(&interactiveMove, 0, 0) != 0)
        return false;
    HMODULE openGl = GetModuleHandleW(L"opengl32.dll");
    if (!openGl || !window || width < 2 || height < 2) return false;
    auto currentContext = reinterpret_cast<WglGetCurrentContextFn>(
        GetProcAddress(openGl, "wglGetCurrentContext"));
    auto wglProc = reinterpret_cast<WglGetProcAddressFn>(
        GetProcAddress(openGl, "wglGetProcAddress"));
    if (!currentContext || !wglProc || !currentContext()) return false;

    std::lock_guard<std::mutex> lock(stateMutex);
    if (InterlockedCompareExchange(&suspended, 0, 0) != 0) return false;
    // During a clip transition Qt may present the incoming and outgoing show
    // HWNDs concurrently. Keep the established surface until Qt hides it so
    // the two renderers cannot repeatedly steal the single HDR presentation.
    if (state.interopObject && state.sourceWindow != window &&
        IsWindow(state.sourceWindow) && IsWindowVisible(state.sourceWindow))
        return false;
    if (!state.interopObject || state.sourceWindow != window ||
        state.width != width || state.height != height)
    {
        if (!Initialize(window, width, height, width, height,
                currentContext(), openGl, wglProc))
        {
            ReleaseState();
            InterlockedExchange(&status, -1);
            return false;
        }
    }
    if (!EnsurePresentation(true)) return false;

    auto lockObjects = reinterpret_cast<WglDxLockObjectsFn>(
        wglProc("wglDXLockObjectsNV"));
    auto unlockObjects = reinterpret_cast<WglDxUnlockObjectsFn>(
        wglProc("wglDXUnlockObjectsNV"));
    auto bindTexture = reinterpret_cast<GlBindTextureFn>(
        GetProcAddress(openGl, "glBindTexture"));
    auto getInteger = reinterpret_cast<GlGetIntegervFn>(
        GetProcAddress(openGl, "glGetIntegerv"));
    auto copy = reinterpret_cast<GlCopyTexSubImage2DFn>(
        GetProcAddress(openGl, "glCopyTexSubImage2D"));
    auto readBuffer = reinterpret_cast<GlReadBufferFn>(
        GetProcAddress(openGl, "glReadBuffer"));
    HANDLE object = state.interopObject;
    if (!lockObjects || !unlockObjects || !bindTexture || !getInteger ||
        !readBuffer || !copy ||
        !lockObjects(state.interopDevice, 1, &object)) return false;
    int previousTexture = 0;
    int previousReadBuffer = 0;
    int readFramebuffer = 0;
    getInteger(GlTextureBinding2D, &previousTexture);
    getInteger(0x0C02, &previousReadBuffer); // GL_READ_BUFFER
    getInteger(0x8CAA, &readFramebuffer); // GL_READ_FRAMEBUFFER_BINDING
    if (readFramebuffer == 0) readBuffer(0x0405); // GL_BACK
    bindTexture(GlTexture2D, state.glTexture);
    copy(GlTexture2D, 0, 0, 0, 0, 0, width, height);
    bindTexture(GlTexture2D, static_cast<unsigned>(previousTexture));
    if (readFramebuffer == 0)
        readBuffer(static_cast<unsigned>(previousReadBuffer));
    unlockObjects(state.interopDevice, 1, &object);
    state.context->CopyResource(state.alphaCopy, state.input);

    const bool useHdr = InterlockedCompareExchange(&hdrEnabled, 0, 0) != 0;
    wchar_t error[512] = {};
    bool succeeded = !useHdr || (EnsureRtxSession() &&
        state.evaluate(state.rtxSession, state.input, state.output,
            width, height, 100, 100, 50, 1000,
            error, ARRAYSIZE(error)) != 0);
    LONG previous = InterlockedExchange(&status, succeeded ? 1 : -1);
    if (succeeded)
    {
        if (PresentOutput(state.width, state.height))
            InterlockedIncrement(&frameCount);
        if (previous != 1) Log(useHdr
            ? L"NVIDIA TrueHDR frame evaluation active"
            : L"SDR alpha presentation active");
    }
    else if (previous != -1)
        Log(L"NVIDIA TrueHDR frame evaluation failed");
    return succeeded;
}

bool TryEvaluateOpenGlTextureHdr(HWND window, unsigned int sourceTexture,
    int textureWidth, int textureHeight, int targetWidth, int targetHeight)
{
    if (InterlockedCompareExchange(&suspended, 0, 0) != 0) return false;
    if (InterlockedCompareExchange(&interactiveMove, 0, 0) != 0)
        return false;
    HMODULE openGl = GetModuleHandleW(L"opengl32.dll");
    if (!openGl || !window || !sourceTexture || textureWidth < 2 ||
        textureHeight < 2 || targetWidth < 2 || targetHeight < 2) return false;
    auto currentContext = reinterpret_cast<WglGetCurrentContextFn>(
        GetProcAddress(openGl, "wglGetCurrentContext"));
    auto wglProc = reinterpret_cast<WglGetProcAddressFn>(
        GetProcAddress(openGl, "wglGetProcAddress"));
    if (!currentContext || !wglProc || !currentContext()) return false;

    std::lock_guard<std::mutex> lock(stateMutex);
    if (InterlockedCompareExchange(&suspended, 0, 0) != 0) return false;
    if (!state.interopObject || state.sourceWindow != window ||
        state.width != textureWidth ||
        state.height != textureHeight || state.targetWidth != targetWidth ||
        state.targetHeight != targetHeight)
    {
        if (!Initialize(window, textureWidth, textureHeight,
                targetWidth, targetHeight, currentContext(), openGl, wglProc))
        {
            ReleaseState();
            InterlockedExchange(&status, -1);
            return false;
        }
    }
    if (!EnsurePresentation(false)) return false;

    auto lockObjects = reinterpret_cast<WglDxLockObjectsFn>(
        wglProc("wglDXLockObjectsNV"));
    auto unlockObjects = reinterpret_cast<WglDxUnlockObjectsFn>(
        wglProc("wglDXUnlockObjectsNV"));
    auto copyImage = reinterpret_cast<GlCopyImageSubDataFn>(
        wglProc("glCopyImageSubData"));
    HANDLE object = state.interopObject;
    if (!lockObjects || !unlockObjects || !copyImage ||
        !lockObjects(state.interopDevice, 1, &object)) return false;
    copyImage(sourceTexture, GlTexture2D, 0, 0, 0, 0,
        state.glTexture, GlTexture2D, 0, 0, 0, 0,
        textureWidth, textureHeight, 1);
    unlockObjects(state.interopDevice, 1, &object);
    state.context->CopyResource(state.alphaCopy, state.input);

    const bool useHdr = InterlockedCompareExchange(&hdrEnabled, 0, 0) != 0;
    wchar_t error[512] = {};
    bool succeeded = !useHdr || (EnsureRtxSession() &&
        state.evaluate(state.rtxSession, state.input, state.output,
            textureWidth, textureHeight, 100, 100, 50, 1000,
            error, ARRAYSIZE(error)) != 0);
    LONG previous = InterlockedExchange(&status, succeeded ? 1 : -1);
    if (!succeeded)
    {
        if (previous != -1) Log(L"NVIDIA TrueHDR texture evaluation failed");
        return false;
    }

    if (!PresentOutput(targetWidth, targetHeight)) return false;
    InterlockedIncrement(&frameCount);
    if (previous != 1) Log(useHdr
        ? L"NVIDIA TrueHDR show-texture evaluation active"
        : L"SDR alpha show-texture presentation active");
    return true;
}

void SetOpenGlHdrClickThrough(HWND sourceWindow, bool)
{
    if (!sourceWindow || state.sourceWindow != sourceWindow) return;
    HWND overlay = state.overlayWindow;
    if (!overlay || !IsWindow(overlay)) return;
    const LONG_PTR style = GetWindowLongPtrW(overlay, GWL_EXSTYLE);
    // The HDR presentation window is visual-only. Keeping it permanently out
    // of hit testing avoids changing compositor-visible window regions as the
    // decoded alpha under the pointer changes. The source Qt window and the
    // low-level alpha hook own all interaction semantics.
    const LONG_PTR updated = style | WS_EX_LAYERED | WS_EX_TRANSPARENT;
    if (updated != style)
        SetWindowLongPtrW(overlay, GWL_EXSTYLE, updated);
}

void SetOpenGlHdrPlayerLocked(bool locked)
{
    InterlockedExchange(&playerLocked, locked ? 1 : 0);
}

void SetOpenGlHdrInteractiveMove(bool active)
{
    InterlockedExchange(&interactiveMove, active ? 1 : 0);
    if (active)
    {
        HWND overlay = state.overlayWindow;
        if (overlay && IsWindow(overlay)) ShowWindowAsync(overlay, SW_HIDE);
    }
}

void SetOpenGlHdrEnabled(bool enabled)
{
    const LONG changed = InterlockedExchange(&hdrEnabled, enabled ? 1 : 0);
    if (changed == (enabled ? 1 : 0)) return;
    std::lock_guard<std::mutex> lock(stateMutex);
    if (InterlockedCompareExchange(&suspended, 0, 0) != 0 ||
        InterlockedCompareExchange(&status, 0, 0) != 1 || !state.input)
        return;
    if (!enabled)
    {
        PresentOutput(state.targetWidth, state.targetHeight);
        return;
    }
    wchar_t error[512] = {};
    const bool succeeded = EnsureRtxSession() &&
        state.evaluate(state.rtxSession, state.input, state.output,
            state.width, state.height, 100, 100, 50, 1000,
            error, ARRAYSIZE(error)) != 0;
    InterlockedExchange(&status, succeeded ? 1 : -1);
    if (succeeded)
        PresentOutput(state.targetWidth, state.targetHeight);
}

void SetOpenGlHdrAlphaAntialiasing(bool enabled)
{
    InterlockedExchange(&alphaAntialiasing, enabled ? 1 : 0);
    std::lock_guard<std::mutex> lock(stateMutex);
    if (InterlockedCompareExchange(&suspended, 0, 0) == 0 &&
        InterlockedCompareExchange(&status, 0, 0) == 1)
        PresentOutput(state.targetWidth, state.targetHeight);
}

void HideOpenGlHdr()
{
    InterlockedExchange(&suspended, 1);
    HWND overlay = state.overlayWindow;
    if (overlay && IsWindow(overlay)) ShowWindowAsync(overlay, SW_HIDE);
}

void ReleaseOpenGlHdr()
{
    HideOpenGlHdr();
    std::lock_guard<std::mutex> lock(stateMutex);
    // Keep the NVIDIA, D3D11 and WGL interop session warm while disabled.
    // The overlay is hidden and evaluation is suspended, so re-enabling HDR
    // does not need to rebuild the session.
    InterlockedExchange(&status, 0);
}

void SuspendOpenGlHdrSurface()
{
    InterlockedExchange(&suspended, 1);
    std::lock_guard<std::mutex> lock(stateMutex);
    // DirectComposition can retain the last committed frame after hiding its
    // HWND. Destroy only presentation resources for panic. NVIDIA WGL
    // interop remains registered because synchronous unregister can deadlock
    // against Qt's render thread during a transition.
    ReleasePresentationState(false);
    InterlockedExchange(&status, 0);
}

void ResumeOpenGlHdr()
{
    InterlockedExchange(&suspended, 0);
}

long OpenGlHdrStatus() { return InterlockedCompareExchange(&status, 0, 0); }
long OpenGlHdrFrameCount()
{
    return InterlockedCompareExchange(&frameCount, 0, 0);
}
