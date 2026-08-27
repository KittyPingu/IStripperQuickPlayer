#include <windows.h>
#include <d3d12.h>
#include <d3d11_3.h>
#include <d3dcompiler.h>
#include <DirectML.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include "onnxruntime_cxx_api.h"
#include "dml_provider_factory.h"

using Microsoft::WRL::ComPtr;

namespace
{
constexpr const char* ShaderSource = R"(
Texture2D<float> Y : register(t0);
Texture2D<float2> UV : register(t1);
RWStructuredBuffer<float> RGB : register(u0);
RWTexture2D<float4> Display : register(u1);
cbuffer Params : register(b0) {
  uint SourceWidth, SourceHeight, TargetWidth, TargetHeight;
  uint FullRange, Bt709, Unused0, Unused1;
};
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  if (id.x < SourceWidth && id.y < SourceHeight) {
    uint2 displayPoint = id.xy;
    float displayY = Y.Load(int3(displayPoint, 0));
    float2 displayUv = UV.Load(int3(displayPoint / 2, 0)) - .5;
    displayY = FullRange != 0 ? displayY : max(0, displayY - 16.0 / 255.0) * (255.0 / 219.0);
    float3 displayRgb = Bt709 != 0
      ? float3(displayY + 1.5748 * displayUv.y, displayY - .187324 * displayUv.x - .468124 * displayUv.y, displayY + 1.8556 * displayUv.x)
      : float3(displayY + 1.402 * displayUv.y, displayY - .344136 * displayUv.x - .714136 * displayUv.y, displayY + 1.772 * displayUv.x);
    Display[displayPoint] = float4(saturate(displayRgb), 1);
  }
  if (id.x >= TargetWidth || id.y >= TargetHeight) return;
  uint2 p = uint2(min(SourceWidth - 1, id.x * SourceWidth / TargetWidth),
                  min(SourceHeight - 1, id.y * SourceHeight / TargetHeight));
  float y = Y.Load(int3(p, 0));
  float2 uv = UV.Load(int3(p / 2, 0)) - .5;
  y = FullRange != 0 ? y : max(0, y - 16.0 / 255.0) * (255.0 / 219.0);
  float3 rgb = Bt709 != 0
    ? float3(y + 1.5748 * uv.y, y - .187324 * uv.x - .468124 * uv.y, y + 1.8556 * uv.x)
    : float3(y + 1.402 * uv.y, y - .344136 * uv.x - .714136 * uv.y, y + 1.772 * uv.x);
  uint i = id.y * TargetWidth + id.x;
  uint pixels = TargetWidth * TargetHeight;
  RGB[i] = saturate(rgb.r); RGB[pixels + i] = saturate(rgb.g); RGB[pixels * 2 + i] = saturate(rgb.b);
})";

constexpr const char* AlphaShaderSource = R"(
StructuredBuffer<float> AlphaInput : register(t0);
RWTexture2D<float> AlphaOutput : register(u0);
cbuffer Params : register(b0) { uint Width, Height; };
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
  if (id.x >= Width || id.y >= Height) return;
  AlphaOutput[id.xy] = saturate(AlphaInput[id.y * Width + id.x]);
})";

void ThrowIfFailed(HRESULT result, const char* operation)
{
    if (FAILED(result))
        throw std::runtime_error(std::string(operation) + " failed (0x" +
            std::to_string(static_cast<unsigned long>(result)) + ").");
}

void CopyError(const std::string& message, wchar_t* buffer, int characters)
{
    if (!buffer || characters <= 0) return;
    int count = MultiByteToWideChar(CP_UTF8, 0, message.c_str(), -1,
        buffer, characters);
    if (!count) buffer[0] = L'\0';
    buffer[characters - 1] = L'\0';
}

ComPtr<ID3D12Resource> CreateBuffer(ID3D12Device* device, UINT64 bytes,
    D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags,
    D3D12_RESOURCE_STATES state)
{
    D3D12_HEAP_PROPERTIES properties{};
    properties.Type = heap;
    properties.CreationNodeMask = properties.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC description{};
    description.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    description.Width = std::max<UINT64>(bytes, 4);
    description.Height = 1;
    description.DepthOrArraySize = 1;
    description.MipLevels = 1;
    description.SampleDesc.Count = 1;
    description.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    description.Flags = flags;
    ComPtr<ID3D12Resource> resource;
    ThrowIfFailed(device->CreateCommittedResource(&properties,
        D3D12_HEAP_FLAG_NONE, &description, state, nullptr,
        IID_PPV_ARGS(&resource)), "CreateCommittedResource");
    return resource;
}

struct GpuValue
{
    ComPtr<ID3D12Resource> resource;
    void* allocation{};
    Ort::Value value{nullptr};
};

class Session
{
public:
    Session(std::wstring model, int width, int height, float ratio,
        bool temporal) : modelPath_(std::move(model)), width_(width),
        height_(height), ratio_(ratio), temporal_(temporal), env_(
            ORT_LOGGING_LEVEL_WARNING, "IStripperRvmGpu") {}

    ~Session()
    {
        ReleaseGpuValue(source_);
        ReleaseGpuValue(ratioValue_);
        for (auto& value : initialStates_) ReleaseGpuValue(value);
        ReleaseOutputs(outputsA_);
        ReleaseOutputs(outputsB_);
        if (completionEvent_) CloseHandle(completionEvent_);
    }

    void Reset() { hasState_ = false; currentA_ = true; }

    HANDLE CreateDisplayHandle()
    {
        if (!display_) throw std::runtime_error(
            "The GPU display surface has not been initialized.");
        HANDLE handle{};
        ThrowIfFailed(displayDxgi_->CreateSharedHandle(nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            nullptr, &handle), "Create display shared handle");
        return handle;
    }

    HANDLE CreateAlphaHandle()
    {
        if (!alphaDxgi_) throw std::runtime_error(
            "The GPU alpha surface has not been initialized.");
        HANDLE handle{};
        ThrowIfFailed(alphaDxgi_->CreateSharedHandle(nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            nullptr, &handle), "Create alpha shared handle");
        return handle;
    }

    ID3D12Resource* DisplayTexture() const { return display_.Get(); }

    void Run(ID3D12Resource* decoded, unsigned subresource,
        ID3D12Fence* decodeFence, uint64_t decodeFenceValue,
        bool fullRange, bool bt709, bool p010, uint8_t* alpha,
        int alphaLength, bool readback)
    {
        if (!decoded || !decodeFence || !alpha ||
            alphaLength < width_ * height_)
            throw std::invalid_argument("Invalid GPU RVM frame arguments.");
        if (!device_) Initialize(decoded);
        ComPtr<ID3D12Device> frameDevice;
        ThrowIfFailed(decoded->GetDevice(IID_PPV_ARGS(&frameDevice)),
            "GetDevice");
        if (frameDevice.Get() != device_.Get())
            throw std::runtime_error("The decoded frame changed GPU adapters.");

        Preprocess(decoded, subresource, decodeFence, decodeFenceValue,
            fullRange, bt709, p010);
        RunModel();
        ComPtr<ID3D12Resource> alphaResource = PublishAlpha(CurrentOutputs()[0]);
        if (readback) ReadAlpha(alphaResource.Get(), alpha, alphaLength);
    }

private:
    using Outputs = std::array<std::unique_ptr<Ort::Value>, 5>;
    std::wstring modelPath_;
    int width_;
    int height_;
    float ratio_;
    bool temporal_;
    bool hasState_{};
    bool currentA_{true};
    Ort::Env env_;
    const OrtDmlApi* dmlApi_{};
    ComPtr<ID3D12Device> device_;
    ComPtr<ID3D12CommandQueue> queue_;
    ComPtr<ID3D12CommandAllocator> allocator_;
    ComPtr<ID3D12GraphicsCommandList> list_;
    ComPtr<ID3D12Fence> completionFence_;
    uint64_t completionValue_{};
    HANDLE completionEvent_{};
    ComPtr<ID3D12RootSignature> rootSignature_;
    ComPtr<ID3D12PipelineState> pipeline_;
    ComPtr<ID3D12DescriptorHeap> descriptors_;
    ComPtr<ID3D12RootSignature> alphaRootSignature_;
    ComPtr<ID3D12PipelineState> alphaPipeline_;
    ComPtr<ID3D12DescriptorHeap> alphaDescriptors_;
    UINT descriptorSize_{};
    std::unique_ptr<Ort::Session> session_;
    std::unique_ptr<Ort::IoBinding> binding_;
    std::unique_ptr<Ort::Allocator> outputAllocator_;
    const OrtMemoryInfo* outputMemoryInfo_{};
    GpuValue source_;
    GpuValue ratioValue_;
    std::array<GpuValue, 4> initialStates_;
    Outputs outputsA_{};
    Outputs outputsB_{};
    ComPtr<ID3D12Resource> readback_;
    ComPtr<ID3D12Resource> display_;
    ComPtr<ID3D11Texture2D> displayD3D11_;
    ComPtr<IDXGIResource1> displayDxgi_;
    ComPtr<ID3D12Resource> alphaTexture_;
    ComPtr<ID3D11Texture2D> alphaD3D11_;
    ComPtr<IDXGIResource1> alphaDxgi_;

    void Initialize(ID3D12Resource* decoded)
    {
        ThrowIfFailed(decoded->GetDevice(IID_PPV_ARGS(&device_)), "GetDevice");
        D3D12_COMMAND_QUEUE_DESC queueDescription{};
        queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        ThrowIfFailed(device_->CreateCommandQueue(&queueDescription,
            IID_PPV_ARGS(&queue_)), "CreateCommandQueue");
        ThrowIfFailed(device_->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator_)),
            "CreateCommandAllocator");
        ThrowIfFailed(device_->CreateCommandList(0,
            D3D12_COMMAND_LIST_TYPE_DIRECT, allocator_.Get(), nullptr,
            IID_PPV_ARGS(&list_)), "CreateCommandList");
        ThrowIfFailed(list_->Close(), "Close command list");
        ThrowIfFailed(device_->CreateFence(0, D3D12_FENCE_FLAG_NONE,
            IID_PPV_ARGS(&completionFence_)), "CreateFence");
        completionEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!completionEvent_) ThrowIfFailed(HRESULT_FROM_WIN32(GetLastError()),
            "CreateEvent");
        CreateComputePipeline();
        CreateAlphaPipeline();

        ComPtr<IDMLDevice> dmlDevice;
        ThrowIfFailed(DMLCreateDevice(device_.Get(), DML_CREATE_DEVICE_FLAG_NONE,
            IID_PPV_ARGS(&dmlDevice)),
            "DMLCreateDevice");
        const OrtApi& api = Ort::GetApi();
        Ort::ThrowOnError(api.GetExecutionProviderApi("DML", ORT_API_VERSION,
            reinterpret_cast<const void**>(&dmlApi_)));
        Ort::SessionOptions options;
        options.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
        options.DisableMemPattern();
        options.SetExecutionMode(ExecutionMode::ORT_SEQUENTIAL);
        Ort::ThrowOnError(dmlApi_->SessionOptionsAppendExecutionProvider_DML1(
            options, dmlDevice.Get(), queue_.Get()));
        session_ = std::make_unique<Ort::Session>(env_, modelPath_.c_str(), options);
        binding_ = std::make_unique<Ort::IoBinding>(*session_);

        std::array<const OrtMemoryInfo*, 6> outputInfo{};
        Ort::ThrowOnError(api.SessionGetMemoryInfoForOutputs(*session_,
            outputInfo.data(), outputInfo.size()));
        outputMemoryInfo_ = outputInfo[0];
        OrtAllocator* rawAllocator{};
        Ort::ThrowOnError(api.CreateAllocator(*session_, outputMemoryInfo_,
            &rawAllocator));
        outputAllocator_ = std::make_unique<Ort::Allocator>(rawAllocator);

        source_ = CreateGpuTensor(static_cast<UINT64>(width_) * height_ * 3 * 4,
            {1, 3, height_, width_});
        D3D12_RESOURCE_DESC decodedDescription = decoded->GetDesc();
        CreateSharedTexture(static_cast<UINT>(decodedDescription.Width),
            decodedDescription.Height, DXGI_FORMAT_R8G8B8A8_UNORM,
            displayD3D11_, displayDxgi_, display_);
        CreateSharedTexture(static_cast<UINT>(width_),
            static_cast<UINT>(height_), DXGI_FORMAT_R8_UNORM,
            alphaD3D11_, alphaDxgi_, alphaTexture_);
        ratioValue_ = CreateGpuTensor(4, {1});
        for (auto& state : initialStates_)
            state = CreateGpuTensor(4, {1, 1, 1, 1});
        UploadConstant(ratioValue_.resource.Get(), &ratio_, 4);
        float zero = 0;
        for (auto& state : initialStates_)
            UploadConstant(state.resource.Get(), &zero, 4);
        readback_ = CreateBuffer(device_.Get(),
            static_cast<UINT64>(width_) * height_ * 4,
            D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE,
            D3D12_RESOURCE_STATE_COPY_DEST);
    }

    void CreateSharedTexture(UINT width, UINT height, DXGI_FORMAT format,
        ComPtr<ID3D11Texture2D>& d3d11Texture,
        ComPtr<IDXGIResource1>& dxgiResource,
        ComPtr<ID3D12Resource>& d3d12Resource)
    {
        LUID target = device_->GetAdapterLuid();
        ComPtr<IDXGIFactory2> factory;
        ThrowIfFailed(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),
            "CreateDXGIFactory1");
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT index = 0; ; ++index)
        {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory->EnumAdapters1(index, &candidate) == DXGI_ERROR_NOT_FOUND)
                break;
            DXGI_ADAPTER_DESC1 description{};
            candidate->GetDesc1(&description);
            if (description.AdapterLuid.HighPart == target.HighPart &&
                description.AdapterLuid.LowPart == target.LowPart)
            {
                adapter = candidate;
                break;
            }
        }
        if (!adapter) throw std::runtime_error(
            "Could not find the D3D12 decoder adapter for D3D11 sharing.");
        ComPtr<ID3D11Device> d3d11;
        ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0};
        ThrowIfFailed(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN,
            nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
            ARRAYSIZE(levels), D3D11_SDK_VERSION, &d3d11, nullptr, &context),
            "Create sharing D3D11 device");
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width;
        description.Height = height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = format;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE |
            D3D11_BIND_UNORDERED_ACCESS;
        description.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
            D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        ThrowIfFailed(d3d11->CreateTexture2D(&description, nullptr,
            &d3d11Texture), "Create shared D3D11 texture");
        ThrowIfFailed(d3d11Texture.As(&dxgiResource),
            "Query shared texture");
        HANDLE handle{};
        ThrowIfFailed(dxgiResource->CreateSharedHandle(nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            nullptr, &handle), "Create D3D11 texture handle");
        HRESULT opened = device_->OpenSharedHandle(handle,
            IID_PPV_ARGS(&d3d12Resource));
        CloseHandle(handle);
        ThrowIfFailed(opened, "Open D3D11 texture in D3D12");
    }

    GpuValue CreateGpuTensor(UINT64 bytes, const std::vector<int64_t>& shape)
    {
        GpuValue result;
        result.resource = CreateBuffer(device_.Get(), bytes,
            D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATE_COMMON);
        Ort::ThrowOnError(dmlApi_->CreateGPUAllocationFromD3DResource(
            result.resource.Get(), &result.allocation));
        result.value = Ort::Value::CreateTensor<float>(
            outputMemoryInfo_,
            static_cast<float*>(result.allocation), bytes / 4,
            shape.data(), shape.size());
        return result;
    }

    void ReleaseGpuValue(GpuValue& value)
    {
        value.value = Ort::Value(nullptr);
        if (value.allocation && dmlApi_)
        {
            if (OrtStatus* status = dmlApi_->FreeGPUAllocation(value.allocation))
                Ort::GetApi().ReleaseStatus(status);
        }
        value.allocation = nullptr;
        value.resource.Reset();
    }

    static void ReleaseOutputs(Outputs& outputs)
    {
        for (auto& output : outputs) output.reset();
    }

    void UploadConstant(ID3D12Resource* destination, const void* data, UINT64 bytes)
    {
        auto upload = CreateBuffer(device_.Get(), bytes, D3D12_HEAP_TYPE_UPLOAD,
            D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
        void* mapped{};
        ThrowIfFailed(upload->Map(0, nullptr, &mapped), "Map upload buffer");
        memcpy(mapped, data, static_cast<size_t>(bytes));
        upload->Unmap(0, nullptr);
        BeginCommands();
        list_->CopyBufferRegion(destination, 0, upload.Get(), 0, bytes);
        EndCommandsAndWait();
    }

    void CreateComputePipeline()
    {
        D3D12_DESCRIPTOR_RANGE ranges[2]{};
        ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        ranges[0].NumDescriptors = 2;
        ranges[0].BaseShaderRegister = 0;
        ranges[0].OffsetInDescriptorsFromTableStart = 0;
        ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
        ranges[1].NumDescriptors = 2;
        ranges[1].BaseShaderRegister = 0;
        ranges[1].OffsetInDescriptorsFromTableStart = 2;
        D3D12_ROOT_PARAMETER parameters[2]{};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].DescriptorTable.NumDescriptorRanges = 2;
        parameters[0].DescriptorTable.pDescriptorRanges = ranges;
        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[1].Constants.ShaderRegister = 0;
        parameters[1].Constants.Num32BitValues = 8;
        D3D12_ROOT_SIGNATURE_DESC rootDescription{};
        rootDescription.NumParameters = 2;
        rootDescription.pParameters = parameters;
        ComPtr<ID3DBlob> serialized, errors;
        ThrowIfFailed(D3D12SerializeRootSignature(&rootDescription,
            D3D_ROOT_SIGNATURE_VERSION_1, &serialized, &errors),
            errors ? static_cast<const char*>(errors->GetBufferPointer()) :
                "D3D12SerializeRootSignature");
        ThrowIfFailed(device_->CreateRootSignature(0, serialized->GetBufferPointer(),
            serialized->GetBufferSize(), IID_PPV_ARGS(&rootSignature_)),
            "CreateRootSignature");
        ComPtr<ID3DBlob> shader;
        ThrowIfFailed(D3DCompile(ShaderSource, strlen(ShaderSource),
            "RvmGpuPreprocess.hlsl", nullptr, nullptr, "main", "cs_5_1",
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &shader, &errors),
            errors ? static_cast<const char*>(errors->GetBufferPointer()) :
                "D3DCompile");
        D3D12_COMPUTE_PIPELINE_STATE_DESC pipelineDescription{};
        pipelineDescription.pRootSignature = rootSignature_.Get();
        pipelineDescription.CS = {shader->GetBufferPointer(), shader->GetBufferSize()};
        ThrowIfFailed(device_->CreateComputePipelineState(&pipelineDescription,
            IID_PPV_ARGS(&pipeline_)), "CreateComputePipelineState");
        D3D12_DESCRIPTOR_HEAP_DESC heapDescription{};
        heapDescription.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDescription.NumDescriptors = 4;
        heapDescription.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ThrowIfFailed(device_->CreateDescriptorHeap(&heapDescription,
            IID_PPV_ARGS(&descriptors_)), "CreateDescriptorHeap");
        descriptorSize_ = device_->GetDescriptorHandleIncrementSize(
            D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    }

    void CreateAlphaPipeline()
    {
        D3D12_DESCRIPTOR_RANGE ranges[2]{};
        ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        ranges[0].NumDescriptors = 1;
        ranges[0].BaseShaderRegister = 0;
        ranges[0].OffsetInDescriptorsFromTableStart = 0;
        ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
        ranges[1].NumDescriptors = 1;
        ranges[1].BaseShaderRegister = 0;
        ranges[1].OffsetInDescriptorsFromTableStart = 1;
        D3D12_ROOT_PARAMETER parameters[2]{};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].DescriptorTable.NumDescriptorRanges = 2;
        parameters[0].DescriptorTable.pDescriptorRanges = ranges;
        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[1].Constants.ShaderRegister = 0;
        parameters[1].Constants.Num32BitValues = 2;
        D3D12_ROOT_SIGNATURE_DESC rootDescription{};
        rootDescription.NumParameters = 2;
        rootDescription.pParameters = parameters;
        ComPtr<ID3DBlob> serialized, errors;
        ThrowIfFailed(D3D12SerializeRootSignature(&rootDescription,
            D3D_ROOT_SIGNATURE_VERSION_1, &serialized, &errors),
            errors ? static_cast<const char*>(errors->GetBufferPointer()) :
                "Serialize alpha root signature");
        ThrowIfFailed(device_->CreateRootSignature(0,
            serialized->GetBufferPointer(), serialized->GetBufferSize(),
            IID_PPV_ARGS(&alphaRootSignature_)), "Create alpha root signature");
        ComPtr<ID3DBlob> shader;
        ThrowIfFailed(D3DCompile(AlphaShaderSource, strlen(AlphaShaderSource),
            "RvmGpuAlpha.hlsl", nullptr, nullptr, "main", "cs_5_1",
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &shader, &errors),
            errors ? static_cast<const char*>(errors->GetBufferPointer()) :
                "Compile alpha shader");
        D3D12_COMPUTE_PIPELINE_STATE_DESC pipelineDescription{};
        pipelineDescription.pRootSignature = alphaRootSignature_.Get();
        pipelineDescription.CS = {shader->GetBufferPointer(), shader->GetBufferSize()};
        ThrowIfFailed(device_->CreateComputePipelineState(&pipelineDescription,
            IID_PPV_ARGS(&alphaPipeline_)), "Create alpha pipeline");
        D3D12_DESCRIPTOR_HEAP_DESC heapDescription{};
        heapDescription.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDescription.NumDescriptors = 2;
        heapDescription.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ThrowIfFailed(device_->CreateDescriptorHeap(&heapDescription,
            IID_PPV_ARGS(&alphaDescriptors_)), "Create alpha descriptors");
    }

    void Preprocess(ID3D12Resource* decoded, unsigned subresource,
        ID3D12Fence* decodeFence, uint64_t decodeFenceValue,
        bool fullRange, bool bt709, bool p010)
    {
        ThrowIfFailed(queue_->Wait(decodeFence, decodeFenceValue),
            "Wait for decoded frame");
        D3D12_RESOURCE_DESC sourceDescription = decoded->GetDesc();
        UINT arraySize = sourceDescription.DepthOrArraySize;
        D3D12_CPU_DESCRIPTOR_HANDLE cpu =
            descriptors_->GetCPUDescriptorHandleForHeapStart();
        D3D12_SHADER_RESOURCE_VIEW_DESC y{};
        y.Format = p010 ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;
        y.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        y.ViewDimension = arraySize > 1 ? D3D12_SRV_DIMENSION_TEXTURE2DARRAY :
            D3D12_SRV_DIMENSION_TEXTURE2D;
        if (arraySize > 1)
        {
            y.Texture2DArray.MipLevels = 1;
            y.Texture2DArray.ArraySize = 1;
            y.Texture2DArray.FirstArraySlice = subresource;
        }
        else y.Texture2D.MipLevels = 1;
        device_->CreateShaderResourceView(decoded, &y, cpu);
        cpu.ptr += descriptorSize_;
        D3D12_SHADER_RESOURCE_VIEW_DESC uv = y;
        uv.Format = p010 ? DXGI_FORMAT_R16G16_UNORM : DXGI_FORMAT_R8G8_UNORM;
        uv.Texture2D.PlaneSlice = 1;
        uv.Texture2DArray.PlaneSlice = 1;
        device_->CreateShaderResourceView(decoded, &uv, cpu);
        cpu.ptr += descriptorSize_;
        D3D12_UNORDERED_ACCESS_VIEW_DESC output{};
        output.Format = DXGI_FORMAT_UNKNOWN;
        output.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
        output.Buffer.NumElements = width_ * height_ * 3;
        output.Buffer.StructureByteStride = 4;
        device_->CreateUnorderedAccessView(source_.resource.Get(), nullptr,
            &output, cpu);
        cpu.ptr += descriptorSize_;
        D3D12_UNORDERED_ACCESS_VIEW_DESC displayOutput{};
        displayOutput.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        displayOutput.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        device_->CreateUnorderedAccessView(display_.Get(), nullptr,
            &displayOutput, cpu);

        BeginCommands();
        D3D12_RESOURCE_BARRIER barriers[3]{};
        barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[0].Transition.pResource = decoded;
        barriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        barriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[1].Transition.pResource = source_.resource.Get();
        barriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        barriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        barriers[2].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[2].Transition.pResource = display_.Get();
        barriers[2].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barriers[2].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        barriers[2].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        list_->ResourceBarrier(3, barriers);
        list_->SetComputeRootSignature(rootSignature_.Get());
        list_->SetPipelineState(pipeline_.Get());
        ID3D12DescriptorHeap* heaps[] = {descriptors_.Get()};
        list_->SetDescriptorHeaps(1, heaps);
        list_->SetComputeRootDescriptorTable(0,
            descriptors_->GetGPUDescriptorHandleForHeapStart());
        unsigned constants[8] = {static_cast<unsigned>(sourceDescription.Width),
            sourceDescription.Height, static_cast<unsigned>(width_),
            static_cast<unsigned>(height_), fullRange ? 1u : 0u,
            bt709 ? 1u : 0u, 0, 0};
        list_->SetComputeRoot32BitConstants(1, 8, constants, 0);
        UINT dispatchWidth = std::max<UINT>(static_cast<UINT>(width_),
            static_cast<UINT>(sourceDescription.Width));
        UINT dispatchHeight = std::max<UINT>(static_cast<UINT>(height_),
            sourceDescription.Height);
        list_->Dispatch((dispatchWidth + 15) / 16,
            (dispatchHeight + 15) / 16, 1);
        std::swap(barriers[0].Transition.StateBefore,
            barriers[0].Transition.StateAfter);
        std::swap(barriers[1].Transition.StateBefore,
            barriers[1].Transition.StateAfter);
        std::swap(barriers[2].Transition.StateBefore,
            barriers[2].Transition.StateAfter);
        list_->ResourceBarrier(3, barriers);
        EndCommands(false);
    }

    void RunModel()
    {
        binding_->ClearBoundInputs();
        binding_->ClearBoundOutputs();
        binding_->BindInput("src", source_.value);
        Outputs* current = hasState_ ? (currentA_ ? &outputsA_ : &outputsB_) : nullptr;
        static constexpr const char* stateInputs[] = {"r1i", "r2i", "r3i", "r4i"};
        for (int i = 0; i < 4; ++i)
            binding_->BindInput(stateInputs[i], current && temporal_
                ? *(*current)[i + 1] : initialStates_[i].value);
        binding_->BindInput("downsample_ratio", ratioValue_.value);
        static constexpr const char* outputNames[] = {"pha", "r1o", "r2o", "r3o", "r4o"};
        Outputs& destination = !hasState_ || !currentA_ ? outputsA_ : outputsB_;
        bool destinationReady = destination[0] != nullptr;
        for (int i = 0; i < 5; ++i)
            if (destinationReady) binding_->BindOutput(outputNames[i], *destination[i]);
            else binding_->BindOutput(outputNames[i], outputMemoryInfo_);
        session_->Run(Ort::RunOptions{nullptr}, *binding_);
        if (!destinationReady)
        {
            std::vector<Ort::Value> values = binding_->GetOutputValues();
            if (values.size() != 5)
                throw std::runtime_error("RVM returned an unexpected output count.");
            for (int i = 0; i < 5; ++i)
                destination[i] = std::make_unique<Ort::Value>(std::move(values[i]));
        }
        hasState_ = temporal_;
        currentA_ = &destination == &outputsA_;
    }

    Outputs& CurrentOutputs() { return currentA_ ? outputsA_ : outputsB_; }

    ComPtr<ID3D12Resource> PublishAlpha(
        const std::unique_ptr<Ort::Value>& alphaValue)
    {
        void* allocation = alphaValue->GetTensorMutableRawData();
        ComPtr<ID3D12Resource> alphaResource;
        Ort::ThrowOnError(dmlApi_->GetD3D12ResourceFromAllocation(
            *outputAllocator_, allocation, &alphaResource));
        D3D12_CPU_DESCRIPTOR_HANDLE cpu =
            alphaDescriptors_->GetCPUDescriptorHandleForHeapStart();
        D3D12_SHADER_RESOURCE_VIEW_DESC input{};
        input.Format = DXGI_FORMAT_UNKNOWN;
        input.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        input.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        input.Buffer.NumElements = width_ * height_;
        input.Buffer.StructureByteStride = 4;
        device_->CreateShaderResourceView(alphaResource.Get(), &input, cpu);
        cpu.ptr += descriptorSize_;
        D3D12_UNORDERED_ACCESS_VIEW_DESC output{};
        output.Format = DXGI_FORMAT_R8_UNORM;
        output.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        device_->CreateUnorderedAccessView(alphaTexture_.Get(), nullptr,
            &output, cpu);
        BeginCommands();
        D3D12_RESOURCE_BARRIER barriers[2]{};
        barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[0].Transition.pResource = alphaResource.Get();
        barriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        barriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[1].Transition.pResource = alphaTexture_.Get();
        barriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        barriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        list_->ResourceBarrier(2, barriers);
        list_->SetComputeRootSignature(alphaRootSignature_.Get());
        list_->SetPipelineState(alphaPipeline_.Get());
        ID3D12DescriptorHeap* heaps[] = {alphaDescriptors_.Get()};
        list_->SetDescriptorHeaps(1, heaps);
        list_->SetComputeRootDescriptorTable(0,
            alphaDescriptors_->GetGPUDescriptorHandleForHeapStart());
        unsigned constants[2] = {static_cast<unsigned>(width_),
            static_cast<unsigned>(height_)};
        list_->SetComputeRoot32BitConstants(1, 2, constants, 0);
        list_->Dispatch((width_ + 15) / 16, (height_ + 15) / 16, 1);
        std::swap(barriers[0].Transition.StateBefore,
            barriers[0].Transition.StateAfter);
        std::swap(barriers[1].Transition.StateBefore,
            barriers[1].Transition.StateAfter);
        list_->ResourceBarrier(2, barriers);
        EndCommandsAndWait();
        return alphaResource;
    }

    void ReadAlpha(ID3D12Resource* alphaResource,
        uint8_t* destination, int destinationLength)
    {
        UINT64 bytes = static_cast<UINT64>(width_) * height_ * 4;
        BeginCommands();
        list_->CopyBufferRegion(readback_.Get(), 0, alphaResource, 0, bytes);
        EndCommandsAndWait();
        const float* values{};
        D3D12_RANGE range{0, static_cast<SIZE_T>(bytes)};
        ThrowIfFailed(readback_->Map(0, &range,
            reinterpret_cast<void**>(const_cast<float**>(&values))),
            "Map alpha readback");
        int pixels = std::min(width_ * height_, destinationLength);
        for (int i = 0; i < pixels; ++i)
            destination[i] = static_cast<uint8_t>(std::clamp(
                static_cast<int>(values[i] * 255.0f + .5f), 0, 255));
        D3D12_RANGE written{0, 0};
        readback_->Unmap(0, &written);
    }

    void BeginCommands()
    {
        ThrowIfFailed(allocator_->Reset(), "Reset command allocator");
        ThrowIfFailed(list_->Reset(allocator_.Get(), nullptr),
            "Reset command list");
    }

    void EndCommands(bool wait)
    {
        ThrowIfFailed(list_->Close(), "Close command list");
        ID3D12CommandList* lists[] = {list_.Get()};
        queue_->ExecuteCommandLists(1, lists);
        if (wait) WaitForQueue();
    }

    void EndCommandsAndWait() { EndCommands(true); }

    void WaitForQueue()
    {
        uint64_t value = ++completionValue_;
        ThrowIfFailed(queue_->Signal(completionFence_.Get(), value),
            "Signal command queue");
        if (completionFence_->GetCompletedValue() < value)
        {
            ThrowIfFailed(completionFence_->SetEventOnCompletion(value,
                completionEvent_), "SetEventOnCompletion");
            WaitForSingleObject(completionEvent_, INFINITE);
        }
    }
};
}

extern "C" __declspec(dllexport) int __cdecl RvmGpu_Create(
    const wchar_t* modelPath, int width, int height, float downsampleRatio,
    int temporal, void** handle, wchar_t* error, int errorCharacters)
{
    try
    {
        if (!modelPath || !handle || width <= 0 || height <= 0)
            throw std::invalid_argument("Invalid GPU RVM session arguments.");
        *handle = new Session(modelPath, width, height, downsampleRatio,
            temporal != 0);
        return 0;
    }
    catch (const std::exception& exception)
    {
        CopyError(exception.what(), error, errorCharacters);
        return -1;
    }
}

extern "C" __declspec(dllexport) int __cdecl RvmGpu_Run(void* handle,
    ID3D12Resource* texture, unsigned subresource, ID3D12Fence* fence,
    uint64_t fenceValue, int fullRange, int bt709, int p010,
    uint8_t* alpha, int alphaLength, int readback,
    wchar_t* error, int errorCharacters)
{
    try
    {
        if (!handle) throw std::invalid_argument("GPU RVM session is null.");
        static_cast<Session*>(handle)->Run(texture, subresource, fence,
            fenceValue, fullRange != 0, bt709 != 0, p010 != 0,
            alpha, alphaLength, readback != 0);
        return 0;
    }
    catch (const Ort::Exception& exception)
    {
        CopyError(exception.what(), error, errorCharacters);
        return -2;
    }
    catch (const std::exception& exception)
    {
        CopyError(exception.what(), error, errorCharacters);
        return -1;
    }
}

extern "C" __declspec(dllexport) void __cdecl RvmGpu_Reset(void* handle)
{
    if (handle) static_cast<Session*>(handle)->Reset();
}

extern "C" __declspec(dllexport) void __cdecl RvmGpu_Destroy(void* handle)
{
    delete static_cast<Session*>(handle);
}

extern "C" __declspec(dllexport) int __cdecl RvmGpu_CreateDisplaySharedHandle(
    void* handle, HANDLE* sharedHandle, wchar_t* error, int errorCharacters)
{
    try
    {
        if (!handle || !sharedHandle)
            throw std::invalid_argument("Invalid GPU display handle arguments.");
        *sharedHandle = static_cast<Session*>(handle)->CreateDisplayHandle();
        return 0;
    }
    catch (const std::exception& exception)
    {
        CopyError(exception.what(), error, errorCharacters);
        return -1;
    }
}

extern "C" __declspec(dllexport) int __cdecl RvmGpu_CreateAlphaSharedHandle(
    void* handle, HANDLE* sharedHandle, wchar_t* error, int errorCharacters)
{
    try
    {
        if (!handle || !sharedHandle)
            throw std::invalid_argument("Invalid GPU alpha handle arguments.");
        *sharedHandle = static_cast<Session*>(handle)->CreateAlphaHandle();
        return 0;
    }
    catch (const std::exception& exception)
    {
        CopyError(exception.what(), error, errorCharacters);
        return -1;
    }
}

extern "C" __declspec(dllexport) int __cdecl RvmGpu_VerifyD3D11Sharing(
    void* handle,
    wchar_t* error, int errorCharacters)
{
    try
    {
        if (!handle) throw std::invalid_argument("GPU RVM session is null.");
        HANDLE shared = static_cast<Session*>(handle)->CreateDisplayHandle();
        struct HandleCloser { HANDLE value; ~HandleCloser() { if (value) CloseHandle(value); } } closer{shared};
        ComPtr<ID3D11Device> baseDevice;
        ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0};
        ThrowIfFailed(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE,
            nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
            ARRAYSIZE(levels), D3D11_SDK_VERSION, &baseDevice, nullptr,
            &context), "D3D11CreateDevice");
        ComPtr<ID3D11Device1> device1;
        ComPtr<ID3D11Device3> device3;
        ThrowIfFailed(baseDevice.As(&device1), "Query ID3D11Device1");
        ThrowIfFailed(baseDevice.As(&device3), "Query ID3D11Device3");
        ComPtr<ID3D11Texture2D> texture;
        ThrowIfFailed(device1->OpenSharedResource1(shared,
            IID_PPV_ARGS(&texture)), "OpenSharedResource1");
        D3D11_TEXTURE2D_DESC textureDescription{};
        texture->GetDesc(&textureDescription);
        D3D11_SHADER_RESOURCE_VIEW_DESC1 description{};
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        description.Texture2D.MipLevels = 1;
        ComPtr<ID3D11ShaderResourceView1> y;
        ThrowIfFailed(device3->CreateShaderResourceView1(texture.Get(),
            &description, &y), "Create display shader view");
        HANDLE alphaShared = static_cast<Session*>(handle)->CreateAlphaHandle();
        HandleCloser alphaCloser{alphaShared};
        ComPtr<ID3D11Texture2D> alphaTexture;
        ThrowIfFailed(device1->OpenSharedResource1(alphaShared,
            IID_PPV_ARGS(&alphaTexture)), "Open alpha shared resource");
        description.Format = DXGI_FORMAT_R8_UNORM;
        ComPtr<ID3D11ShaderResourceView1> alphaView;
        ThrowIfFailed(device3->CreateShaderResourceView1(alphaTexture.Get(),
            &description, &alphaView), "Create alpha shader view");
        return 0;
    }
    catch (const std::exception& exception)
    {
        CopyError(exception.what(), error, errorCharacters);
        return -1;
    }
}
