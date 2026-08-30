// This software contains source code provided by NVIDIA Corporation.
// The NGX integration is derived from the RTX Video SDK 1.1 DX11 sample API.
#include <Windows.h>
#include <d3d11_4.h>
#include <mutex>
#include <new>
#include <nvsdk_ngx_defs.h>
#include <nvsdk_ngx_defs_vsr.h>
#include <nvsdk_ngx_helpers_vsr.h>
#include <nvsdk_ngx_defs_truehdr.h>
#include <nvsdk_ngx_helpers_truehdr.h>

namespace
{
    std::mutex ngxMutex;

    struct Session
    {
        ID3D11Device* device = nullptr;
        ID3D11DeviceContext* context = nullptr;
        ID3D10Multithread* multithread = nullptr;
        NVSDK_NGX_Parameter* parameters = nullptr;
        NVSDK_NGX_Handle* vsr = nullptr;
        NVSDK_NGX_Handle* trueHdr = nullptr;
        bool initialized = false;

        ~Session() { Release(); }

        void Release()
        {
            std::scoped_lock lock(ngxMutex);
            if (vsr) NVSDK_NGX_D3D11_ReleaseFeature(vsr);
            vsr = nullptr;
            if (trueHdr) NVSDK_NGX_D3D11_ReleaseFeature(trueHdr);
            trueHdr = nullptr;
            if (parameters) NVSDK_NGX_D3D11_DestroyParameters(parameters);
            parameters = nullptr;
            if (initialized && device) NVSDK_NGX_D3D11_Shutdown1(device);
            initialized = false;
            if (multithread) multithread->Release();
            multithread = nullptr;
            if (context) context->Release();
            context = nullptr;
            if (device) device->Release();
            device = nullptr;
        }
    };

    void Error(wchar_t* output, int capacity, const wchar_t* message)
    {
        if (!output || capacity <= 0) return;
        wcsncpy_s(output, static_cast<size_t>(capacity), message, _TRUNCATE);
    }
}

extern "C" __declspec(dllexport) void* __cdecl RtxVideo_CreateD3D11(
    ID3D11Device* device, int enableVsr, int enableTrueHdr,
    int* availableFeatures, wchar_t* error, int errorCapacity)
{
    if (availableFeatures) *availableFeatures = 0;
    if (!device)
    {
        Error(error, errorCapacity, L"The Direct3D 11 device is missing.");
        return nullptr;
    }
    Session* session = new (std::nothrow) Session();
    if (!session)
    {
        Error(error, errorCapacity, L"Could not allocate the RTX Video session.");
        return nullptr;
    }
    session->device = device;
    device->AddRef();
    device->GetImmediateContext(&session->context);
    session->context->QueryInterface(IID_PPV_ARGS(&session->multithread));
    if (session->multithread) session->multithread->SetMultithreadProtected(TRUE);

    bool created = false;
    {
        std::scoped_lock lock(ngxMutex);
        NVSDK_NGX_Result result = NVSDK_NGX_D3D11_Init(0, L".", device);
        if (NVSDK_NGX_FAILED(result))
        {
            Error(error, errorCapacity,
                L"NVIDIA NGX could not initialize. Update the NVIDIA driver and verify that this is an RTX GPU.");
        }
        else
        {
            session->initialized = true;
            result = NVSDK_NGX_D3D11_GetCapabilityParameters(&session->parameters);
            if (NVSDK_NGX_FAILED(result) || !session->parameters)
                Error(error, errorCapacity, L"NVIDIA RTX Video capabilities could not be queried.");
            else
            {
                int features = 0;
                if (enableVsr)
                {
                    int available = 0;
                    session->parameters->Get(NVSDK_NGX_Parameter_VSR_Available,
                        &available);
                    NVSDK_NGX_Feature_Create_Params create = {};
                    if (available && NVSDK_NGX_SUCCEED(
                        NGX_D3D11_CREATE_VSR_EXT(session->context, &session->vsr,
                            session->parameters, &create))) features |= 1;
                }
                if (enableTrueHdr)
                {
                    int available = 0;
                    session->parameters->Get(
                        NVSDK_NGX_Parameter_TrueHDR_Available, &available);
                    NVSDK_NGX_Feature_Create_Params create = {};
                    if (available && NVSDK_NGX_SUCCEED(
                        NGX_D3D11_CREATE_TRUEHDR_EXT(session->context,
                            &session->trueHdr, session->parameters, &create)))
                        features |= 2;
                }
                created = features != 0;
                if (availableFeatures) *availableFeatures = features;
                if (!created)
                    Error(error, errorCapacity, L"The requested NVIDIA RTX Video features are unavailable on this system.");
            }
        }
    }
    if (!created)
    {
        delete session;
        return nullptr;
    }
    return session;
}

extern "C" __declspec(dllexport) int __cdecl RtxVideo_EvaluateTrueHdrD3D11(
    void* handle, ID3D11Texture2D* input, ID3D11Texture2D* output,
    int width, int height, int contrast, int saturation, int middleGray,
    int maxLuminance, wchar_t* error, int errorCapacity)
{
    Session* session = static_cast<Session*>(handle);
    if (!session || !session->trueHdr || !input || !output)
    {
        Error(error, errorCapacity, L"The RTX Video HDR evaluation resources are incomplete.");
        return 0;
    }
    NVSDK_NGX_D3D11_TRUEHDR_Eval_Params evaluate = {};
    evaluate.pInput = input;
    evaluate.pOutput = output;
    evaluate.InputSubrectBR = { static_cast<unsigned int>(width), static_cast<unsigned int>(height) };
    evaluate.OutputSubrectBR = { static_cast<unsigned int>(width), static_cast<unsigned int>(height) };
    evaluate.Contrast = static_cast<unsigned int>(contrast);
    evaluate.Saturation = static_cast<unsigned int>(saturation);
    evaluate.MiddleGray = static_cast<unsigned int>(middleGray);
    evaluate.MaxLuminance = static_cast<unsigned int>(maxLuminance);
    std::scoped_lock lock(ngxMutex);
    if (session->multithread) session->multithread->Enter();
    NVSDK_NGX_Result result = NGX_D3D11_EVALUATE_TRUEHDR_EXT(session->context,
        session->trueHdr, session->parameters, &evaluate);
    if (session->multithread) session->multithread->Leave();
    if (NVSDK_NGX_FAILED(result))
    {
        Error(error, errorCapacity, L"NVIDIA RTX Video HDR failed to process the frame.");
        return 0;
    }
    return 1;
}

extern "C" __declspec(dllexport) int __cdecl RtxVideo_EvaluateVsrD3D11(
    void* handle, ID3D11Texture2D* input, ID3D11Texture2D* output,
    int inputWidth, int inputHeight, int outputWidth, int outputHeight,
    int quality, wchar_t* error, int errorCapacity)
{
    Session* session = static_cast<Session*>(handle);
    if (!session || !session->vsr || !input || !output)
    {
        Error(error, errorCapacity, L"The RTX Video evaluation resources are incomplete.");
        return 0;
    }
    NVSDK_NGX_D3D11_VSR_Eval_Params evaluate = {};
    evaluate.pInput = input;
    evaluate.pOutput = output;
    evaluate.InputSubrectSize = { static_cast<unsigned int>(inputWidth), static_cast<unsigned int>(inputHeight) };
    evaluate.OutputSubrectSize = { static_cast<unsigned int>(outputWidth), static_cast<unsigned int>(outputHeight) };
    evaluate.QualityLevel = static_cast<NVSDK_NGX_VSR_QualityLevel>(quality);
    std::scoped_lock lock(ngxMutex);
    if (session->multithread) session->multithread->Enter();
    NVSDK_NGX_Result result = NGX_D3D11_EVALUATE_VSR_EXT(session->context,
        session->vsr, session->parameters, &evaluate);
    if (session->multithread) session->multithread->Leave();
    if (NVSDK_NGX_FAILED(result))
    {
        Error(error, errorCapacity, L"NVIDIA RTX Video Super Resolution failed to process the frame.");
        return 0;
    }
    return 1;
}

extern "C" __declspec(dllexport) void __cdecl RtxVideo_Destroy(void* handle)
{
    delete static_cast<Session*>(handle);
}
