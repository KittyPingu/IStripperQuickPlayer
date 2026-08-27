using System.Runtime.InteropServices;

namespace IStripperQuickPlayer;

internal static class NvidiaAiGreenScreen
{
    internal static (int Width, int Height) InferenceSize(int sourceWidth,
        int sourceHeight, string resolution)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        double scale = resolution switch
        {
            "540p" => 540d / sourceHeight,
            "1080p" => 1080d / sourceHeight,
            "source" => 1,
            _ => 720d / sourceHeight
        };
        scale = Math.Max(scale, Math.Max(512d / sourceWidth, 288d / sourceHeight));
        int width = Math.Max(512, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(288, (int)Math.Round(sourceHeight * scale));
        width = checked((width + 1) & ~1);
        height = checked((height + 1) & ~1);
        return (width, height);
    }

    internal static bool VerifyContracts() =>
        InferenceSize(1920, 1080, "720p") == (1280, 720) &&
        InferenceSize(3840, 2160, "540p") == (960, 540) &&
        InferenceSize(320, 180, "source") == (512, 288) &&
        OpaqueFallback(4, 3).All(value => value == 255) &&
        AcceptTemporalSelectorStatus(0) &&
        AcceptTemporalSelectorStatus(-5) &&
        !AcceptTemporalSelectorStatus(-2);

    internal static bool AcceptTemporalSelectorStatus(int status) =>
        status is 0 or -5;

    internal static byte[] OpaqueFallback(int width, int height)
    {
        byte[] alpha = new byte[checked(width * height)];
        Array.Fill(alpha, (byte)255);
        return alpha;
    }
}

internal sealed class NvidiaAiGreenScreenSession : IDisposable
{
    const int NvCvA = 2, NvCvBgr = 5, NvCvU8 = 1, NvCvChunky = 0,
        NvCvCpu = 0, NvCvGpu = 1;
    readonly NvidiaApi api;
    readonly IntPtr effect;
    NvCvImage gpuInput, gpuOutput;
    IntPtr state, stateArray, stream;
    bool disposed;

    internal int Width { get; }
    internal int Height { get; }

    internal NvidiaAiGreenScreenSession(string sdkRoot, int width, int height,
        CustomNvidiaSettings settings)
    {
        CustomShowStore.ValidateNvidiaSettings(settings);
        api = new NvidiaApi(sdkRoot);
        Width = width;
        Height = height;
        try
        {
            Check(api.CreateEffect("GreenScreen", out effect),
                "create Green Screen effect");
            Check(api.SetString(effect, "ModelDir", api.ModelDirectory),
                "set Green Screen model directory");
            Check(api.SetU32(effect, "Mode", (uint)settings.Mode), "set mode");
            int temporalStatus = api.SetU32(effect, "Temporal",
                settings.Temporal ? 1u : 0u);
            // Green Screen 1.2 exposes this selector in its public header but
            // returns NVCV_ERR_SELECTOR. Its state object remains the mechanism
            // that enables temporal consistency, so retain that compatibility.
            if (!NvidiaAiGreenScreen.AcceptTemporalSelectorStatus(temporalStatus))
                Check(temporalStatus, "set temporal filtering");
            Check(api.SetU32(effect, "MaxInputWidth", (uint)width),
                "set maximum input width");
            Check(api.SetU32(effect, "MaxInputHeight", (uint)height),
                "set maximum input height");
            Check(api.SetU32(effect, "MaxNumberStreams", 1),
                "set stream count");
            Check(api.CudaStreamCreate(out stream), "create CUDA stream");
            Check(api.SetCudaStream(effect, "CudaStream", stream),
                "set CUDA stream");
            Check(api.ImageAlloc(ref gpuInput, (uint)width, (uint)height,
                NvCvBgr, NvCvU8, NvCvChunky, NvCvGpu, 1), "allocate input image");
            Check(api.ImageAlloc(ref gpuOutput, (uint)width, (uint)height,
                NvCvA, NvCvU8, NvCvChunky, NvCvGpu, 1), "allocate output image");
            Check(api.SetImage(effect, "SrcImage0", ref gpuInput), "set input image");
            Check(api.SetImage(effect, "DstImage0", ref gpuOutput), "set output image");
            Check(api.Load(effect), "load Green Screen model");
            if (settings.Temporal)
            {
                Check(api.AllocateState(effect, out state), "allocate temporal state");
                stateArray = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(stateArray, state);
                Check(api.SetState(effect, "State", stateArray),
                    "set temporal state");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal unsafe void Run(byte[] bgr, byte[] alpha)
    {
        if (disposed) throw new ObjectDisposedException(nameof(NvidiaAiGreenScreenSession));
        if (bgr.Length < checked(Width * Height * 3) ||
            alpha.Length < checked(Width * Height))
            throw new ArgumentException("NVIDIA frame buffers have the wrong size.");
        fixed (byte* inputPixels = bgr)
        fixed (byte* outputPixels = alpha)
        {
            NvCvImage cpuInput = default, cpuOutput = default;
            Check(api.ImageInit(ref cpuInput, (uint)Width, (uint)Height,
                Width * 3, (IntPtr)inputPixels, NvCvBgr, NvCvU8, NvCvChunky,
                NvCvCpu), "wrap input image");
            Check(api.ImageInit(ref cpuOutput, (uint)Width, (uint)Height,
                Width, (IntPtr)outputPixels, NvCvA, NvCvU8, NvCvChunky,
                NvCvCpu), "wrap output image");
            Check(api.ImageTransfer(ref cpuInput, ref gpuInput, 1, stream,
                IntPtr.Zero), "upload input image");
            Check(api.Run(effect, 0), "run Green Screen");
            Check(api.ImageTransfer(ref gpuOutput, ref cpuOutput, 1, stream,
                IntPtr.Zero), "download alpha mask");
        }
    }

    internal void Reset()
    {
        if (state != IntPtr.Zero)
            Check(api.ResetState(effect, state), "reset temporal state");
    }

    static void Check(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException(
                $"NVIDIA Video Effects SDK could not {operation} (status {status}).");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (state != IntPtr.Zero && effect != IntPtr.Zero)
            api.DeallocateState(effect, state);
        if (gpuInput.DeletePtr != IntPtr.Zero) api.ImageDealloc(ref gpuInput);
        if (gpuOutput.DeletePtr != IntPtr.Zero) api.ImageDealloc(ref gpuOutput);
        if (effect != IntPtr.Zero) api.DestroyEffect(effect);
        if (stateArray != IntPtr.Zero) Marshal.FreeHGlobal(stateArray);
        if (stream != IntPtr.Zero) api.CudaStreamDestroy(stream);
        api.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvCvImage
    {
        internal uint Width, Height;
        internal int Pitch, PixelFormat, ComponentType;
        internal byte PixelBytes, ComponentBytes, NumComponents, Planar, GpuMem,
            Colorspace;
        internal ushort Reserved;
        internal IntPtr Pixels, DeletePtr, DeleteProc;
        internal ulong BufferBytes;
    }

    sealed class NvidiaApi : IDisposable
    {
        readonly IntPtr imageLibrary, effectsLibrary;
        readonly DllSearchPathLease searchPath;
        internal string ModelDirectory { get; }
        internal readonly CreateEffectDelegate CreateEffect;
        internal readonly DestroyEffectDelegate DestroyEffect;
        internal readonly SetU32Delegate SetU32;
        internal readonly SetStringDelegate SetString;
        internal readonly SetCudaStreamDelegate SetCudaStream;
        internal readonly CudaStreamCreateDelegate CudaStreamCreate;
        internal readonly CudaStreamDestroyDelegate CudaStreamDestroy;
        internal readonly SetImageDelegate SetImage;
        internal readonly LoadDelegate Load;
        internal readonly RunDelegate Run;
        internal readonly AllocateStateDelegate AllocateState;
        internal readonly SetStateDelegate SetState;
        internal readonly DeallocateStateDelegate DeallocateState;
        internal readonly ResetStateDelegate ResetState;
        internal readonly ImageInitDelegate ImageInit;
        internal readonly ImageAllocDelegate ImageAlloc;
        internal readonly ImageTransferDelegate ImageTransfer;
        internal readonly ImageDeallocDelegate ImageDealloc;

        internal NvidiaApi(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    "NVIDIA Video Effects SDK is not installed or configured.");
            string image = Find(root, "NVCVImage.dll");
            string effects = Find(root, "NVVideoEffects.dll");
            ModelDirectory = Directory.EnumerateDirectories(root, "models",
                    SearchOption.AllDirectories)
                .OrderByDescending(path => Path.GetDirectoryName(path)?.EndsWith(
                    "bin", StringComparison.OrdinalIgnoreCase) == true)
                .FirstOrDefault() ?? throw new DirectoryNotFoundException(
                    "NVIDIA AI Green Screen model directory is missing.");
            string[] libraryDirectories = Directory.EnumerateFiles(root, "*.dll",
                    SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            searchPath = new DllSearchPathLease(libraryDirectories);
            try { imageLibrary = NativeLibrary.Load(image); }
            catch { searchPath.Dispose(); throw; }
            try { effectsLibrary = NativeLibrary.Load(effects); }
            catch
            {
                NativeLibrary.Free(imageLibrary);
                searchPath.Dispose();
                throw;
            }
            try
            {
                CreateEffect = Get<CreateEffectDelegate>(effectsLibrary, "NvVFX_CreateEffect");
                DestroyEffect = Get<DestroyEffectDelegate>(effectsLibrary, "NvVFX_DestroyEffect");
                SetU32 = Get<SetU32Delegate>(effectsLibrary, "NvVFX_SetU32");
                SetString = Get<SetStringDelegate>(effectsLibrary, "NvVFX_SetString");
                SetCudaStream = Get<SetCudaStreamDelegate>(effectsLibrary,
                    "NvVFX_SetCudaStream");
                CudaStreamCreate = Get<CudaStreamCreateDelegate>(effectsLibrary,
                    "NvVFX_CudaStreamCreate");
                CudaStreamDestroy = Get<CudaStreamDestroyDelegate>(effectsLibrary,
                    "NvVFX_CudaStreamDestroy");
                SetImage = Get<SetImageDelegate>(effectsLibrary, "NvVFX_SetImage");
                Load = Get<LoadDelegate>(effectsLibrary, "NvVFX_Load");
                Run = Get<RunDelegate>(effectsLibrary, "NvVFX_Run");
                AllocateState = Get<AllocateStateDelegate>(effectsLibrary, "NvVFX_AllocateState");
                SetState = Get<SetStateDelegate>(effectsLibrary,
                    "NvVFX_SetStateObjectHandleArray");
                DeallocateState = Get<DeallocateStateDelegate>(effectsLibrary,
                    "NvVFX_DeallocateState");
                ResetState = Get<ResetStateDelegate>(effectsLibrary, "NvVFX_ResetState");
                ImageInit = Get<ImageInitDelegate>(imageLibrary, "NvCVImage_Init");
                ImageAlloc = Get<ImageAllocDelegate>(imageLibrary, "NvCVImage_Alloc");
                ImageTransfer = Get<ImageTransferDelegate>(imageLibrary, "NvCVImage_Transfer");
                ImageDealloc = Get<ImageDeallocDelegate>(imageLibrary, "NvCVImage_Dealloc");
            }
            catch
            {
                NativeLibrary.Free(effectsLibrary);
                NativeLibrary.Free(imageLibrary);
                searchPath.Dispose();
                throw;
            }
        }

        static string Find(string root, string name) =>
            Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .FirstOrDefault() ?? throw new FileNotFoundException(
                    $"NVIDIA SDK export library {name} is missing.", root);
        static T Get<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(
                NativeLibrary.GetExport(library, name));
        public void Dispose()
        {
            NativeLibrary.Free(effectsLibrary);
            NativeLibrary.Free(imageLibrary);
            searchPath.Dispose();
        }
    }

    sealed class DllSearchPathLease : IDisposable
    {
        static readonly object Sync = new();
        static readonly Dictionary<string, int> Directories =
            new(StringComparer.OrdinalIgnoreCase);
        static string? originalPath;
        readonly string[] directories;
        bool disposed;

        internal DllSearchPathLease(IEnumerable<string> directories)
        {
            this.directories = directories.Distinct(
                StringComparer.OrdinalIgnoreCase).ToArray();
            lock (Sync)
            {
                if (Directories.Count == 0)
                    originalPath = Environment.GetEnvironmentVariable("PATH",
                        EnvironmentVariableTarget.Process) ?? "";
                foreach (string directory in this.directories)
                    Directories[directory] = Directories.GetValueOrDefault(directory) + 1;
                Apply();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (Sync)
            {
                foreach (string directory in directories)
                {
                    int count = Directories.GetValueOrDefault(directory) - 1;
                    if (count <= 0) Directories.Remove(directory);
                    else Directories[directory] = count;
                }
                Apply();
                if (Directories.Count == 0) originalPath = null;
            }
        }

        static void Apply()
        {
            string value = string.Join(Path.PathSeparator,
                Directories.Keys.Append(originalPath ?? ""));
            Environment.SetEnvironmentVariable("PATH", value,
                EnvironmentVariableTarget.Process);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CreateEffectDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string code,
        out IntPtr effect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyEffectDelegate(IntPtr effect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetU32Delegate(IntPtr effect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetStringDelegate(IntPtr effect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetCudaStreamDelegate(IntPtr effect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CudaStreamCreateDelegate(
        out IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CudaStreamDestroyDelegate(
        IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetImageDelegate(IntPtr effect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref NvCvImage image);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadDelegate(IntPtr effect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RunDelegate(IntPtr effect, int async);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AllocateStateDelegate(IntPtr effect, out IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetStateDelegate(IntPtr effect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr states);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeallocateStateDelegate(IntPtr effect, IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ResetStateDelegate(IntPtr effect, IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ImageInitDelegate(ref NvCvImage image,
        uint width, uint height, int pitch, IntPtr pixels, int format, int type,
        uint layout, uint memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ImageAllocDelegate(ref NvCvImage image,
        uint width, uint height, int format, int type, uint layout, uint memory,
        uint alignment);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ImageTransferDelegate(ref NvCvImage source,
        ref NvCvImage destination, float scale, IntPtr stream, IntPtr temporary);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ImageDeallocDelegate(ref NvCvImage image);
}
