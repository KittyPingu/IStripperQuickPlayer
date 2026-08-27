using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace IStripperQuickPlayer;

internal static class RvmOnnxSupport
{
    internal const string Algorithm = "rvm-onnx";
    internal const string MobileNetV3 = "mobilenetv3";
    internal const string ResNet50 = "resnet50";
    internal const string FullResolution = "full-resolution";
    internal const string ModelFileName = "rvm_mobilenetv3_fp32.onnx";
    internal const string ModelSha256 =
        "88D4531297118F595BF2FD60F6F566AEC2E559393802D1F436C380F0CBBD2828";
    internal const string ModelUrl =
        "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/" +
        ModelFileName;
    internal const string ResNet50FileName = "rvm_resnet50_fp32.onnx";
    internal const string ResNet50Sha256 =
        "25DB300FCB6EE27F941A1B52C97856E8D1F13C7F35817F81A612F89AF0E8A85C";
    internal const long ResNet50Length = 107479165;
    internal const string ResNet50Url =
        "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/" +
        ResNet50FileName;

    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "rvm-onnx", "v1");
    internal static string ModelPath => Path.Combine(Root, ModelFileName);
    internal static string ModelPathFor(string model) => Path.Combine(Root,
        model == ResNet50 ? ResNet50FileName : ModelFileName);

    internal static bool IsInstalled(string model = MobileNetV3) =>
        VerifyModel(ModelPathFor(model), model);

    internal static bool VerifyModel(string path, string model = MobileNetV3)
    {
        try
        {
            long length = model == ResNet50 ? ResNet50Length : 14975696;
            string hash = model == ResNet50 ? ResNet50Sha256 : ModelSha256;
            if (new FileInfo(path) is not { Exists: true } file ||
                file.Length != length)
                return false;
            using FileStream input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)).Equals(
                hash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static async Task InstallAsync(IProgress<string>? progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(Root);
        foreach ((string model, string label, string url) in new[]
        {
            (MobileNetV3, "MobileNetV3", ModelUrl),
            (ResNet50, "ResNet50", ResNet50Url)
        })
        {
            if (IsInstalled(model))
            {
                progress?.Report($"RVM ONNX {label} model is already installed.");
                continue;
            }
            string staging = Path.Combine(Root,
                ".download-" + Guid.NewGuid().ToString("N"));
            try
            {
                progress?.Report($"Downloading the official RVM {label} ONNX model...");
                using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
                using HttpResponseMessage response = await client.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using (Stream source = await response.Content.ReadAsStreamAsync(token))
                await using (FileStream destination = new(staging, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await source.CopyToAsync(destination, token);
                if (!VerifyModel(staging, model))
                    throw new InvalidDataException(
                        $"The downloaded RVM ONNX {label} model failed its SHA-256 validation.");
                File.Move(staging, ModelPathFor(model), true);
                progress?.Report($"RVM ONNX {label} model installed successfully.");
            }
            finally
            {
                try { File.Delete(staging); } catch { }
            }
        }
    }

    internal static float DownsampleRatio(int width, int height, string quality)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        int target = quality switch
        {
            "fast" => 256,
            "quality" or FullResolution => 512,
            _ => 384
        };
        return Math.Clamp(target / (float)Math.Min(width, height), .1f, 1f);
    }

    internal static (int Width, int Height) InferenceSize(int sourceWidth,
        int sourceHeight, string quality)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (quality == FullResolution)
            return (checked((sourceWidth + 1) & ~1),
                checked((sourceHeight + 1) & ~1));
        int target = quality switch
        {
            "fast" => 256,
            "quality" => 512,
            _ => 384
        };
        double scale = Math.Min(1, target / (double)Math.Min(sourceWidth,
            sourceHeight));
        int width = Math.Max(2, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(2, (int)Math.Round(sourceHeight * scale));
        return (checked((width + 1) & ~1), checked((height + 1) & ~1));
    }

    internal static int QualityIndex(string quality) => quality switch
    {
        "fast" => 0,
        "quality" => 2,
        FullResolution => 3,
        _ => 1
    };

    internal static string QualityFromIndex(int index) => index switch
    {
        0 => "fast",
        2 => "quality",
        3 => FullResolution,
        _ => "balanced"
    };

    internal static bool VerifyContracts() =>
        Math.Abs(DownsampleRatio(1920, 1080, "fast") - 256f / 1080) < .0001 &&
        Math.Abs(DownsampleRatio(1920, 1080, "balanced") - 384f / 1080) < .0001 &&
        Math.Abs(DownsampleRatio(1920, 1080, "quality") - 512f / 1080) < .0001 &&
        Math.Abs(DownsampleRatio(1920, 1080, FullResolution) -
            512f / 1080) < .0001 &&
        DownsampleRatio(320, 180, "quality") == 1 &&
        ModelPathFor(ResNet50).EndsWith(ResNet50FileName,
            StringComparison.OrdinalIgnoreCase) &&
        InferenceSize(1280, 720, "fast") == (456, 256) &&
        InferenceSize(1280, 720, "balanced") == (684, 384) &&
        InferenceSize(720, 1280, "quality") == (512, 910) &&
        InferenceSize(1920, 1080, FullResolution) == (1920, 1080) &&
        QualityIndex(FullResolution) == 3 &&
        QualityFromIndex(3) == FullResolution &&
        InferenceSize(320, 180, "quality") == (320, 180);
}

internal sealed class RvmOnnxSession : IDisposable
{
    static readonly string[] InputNames =
        ["src", "r1i", "r2i", "r3i", "r4i", "downsample_ratio"];
    static readonly string[] OutputNames =
        ["pha", "r1o", "r2o", "r3o", "r4o"];
    readonly InferenceSession session;
    readonly float downsampleRatio;
    readonly bool temporal;
    readonly float[] source;
    readonly float[] ratio;
    readonly RunOptions runOptions = new();
    readonly OrtValue sourceValue;
    readonly OrtValue ratioValue;
    readonly OrtValue[] initialStateValues;
    readonly OrtValue[] inputValues = new OrtValue[6];
    readonly OrtValue[] outputValues = new OrtValue[5];
    float[]? matte;
    OrtValue? matteValue;
    OrtValue[]? stateAValues;
    OrtValue[]? stateBValues;
    bool stateBuffersReady;
    bool hasCurrentState;
    bool currentStateIsA;
    bool disposed;

    internal int Width { get; }
    internal int Height { get; }

    internal RvmOnnxSession(string modelPath, int width, int height,
        CustomRvmOnnxSettings settings)
    {
        CustomShowStore.ValidateRvmOnnxSettings(settings);
        if (!RvmOnnxSupport.VerifyModel(modelPath, settings.Model))
            throw new FileNotFoundException(
                "The verified RVM ONNX real-time model is not installed.", modelPath);
        Width = width;
        Height = height;
        downsampleRatio = RvmOnnxSupport.DownsampleRatio(width, height,
            settings.Quality);
        temporal = settings.Temporal;
        source = GC.AllocateUninitializedArray<float>(checked(width * height * 3));
        ratio = [downsampleRatio];
        SessionOptions options = new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false
        };
        try
        {
            options.AppendExecutionProvider_DML(0);
            session = new InferenceSession(modelPath, options);
        }
        catch
        {
            options.Dispose();
            throw;
        }
        options.Dispose();
        OrtValue? preparedSource = null;
        OrtValue? preparedRatio = null;
        List<OrtValue> preparedInitialStates = [];
        try
        {
            preparedSource = OrtValue.CreateTensorValueFromMemory(source,
                [1, 3, Height, Width]);
            preparedRatio = OrtValue.CreateTensorValueFromMemory(ratio, [1]);
            for (int index = 0; index < 4; index++)
                preparedInitialStates.Add(OrtValue.CreateTensorValueFromMemory(
                    new float[1], [1, 1, 1, 1]));
            sourceValue = preparedSource;
            ratioValue = preparedRatio;
            initialStateValues = preparedInitialStates.ToArray();
        }
        catch
        {
            preparedSource?.Dispose();
            preparedRatio?.Dispose();
            foreach (OrtValue value in preparedInitialStates) value.Dispose();
            session.Dispose();
            runOptions.Dispose();
            throw;
        }
    }

    internal void Run(byte[] bgr, byte[] alpha)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int pixels = checked(Width * Height);
        if (bgr.Length < pixels * 3 || alpha.Length < pixels)
            throw new ArgumentException("RVM frame buffers have the wrong size.");
        if (!temporal) Reset();
        for (int index = 0; index < pixels; index++)
        {
            int bgrOffset = index * 3;
            source[index] = bgr[bgrOffset + 2] / 255f;
            source[pixels + index] = bgr[bgrOffset + 1] / 255f;
            source[pixels * 2 + index] = bgr[bgrOffset] / 255f;
        }
        RunPrepared(alpha);
    }

    internal void Run(FfmpegCpuDecoder frame, byte[] alpha)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (alpha.Length < checked(Width * Height))
            throw new ArgumentException("The RVM alpha buffer is too small.");
        if (!temporal) Reset();
        frame.CopyRgbPlanarFloat(Width, Height, source);
        RunPrepared(alpha);
    }

    void RunPrepared(byte[] alpha)
    {
        int pixels = checked(Width * Height);
        if (!stateBuffersReady)
        {
            inputValues[0] = sourceValue;
            for (int index = 0; index < 4; index++)
                inputValues[index + 1] = initialStateValues[index];
            inputValues[5] = ratioValue;
            using IDisposableReadOnlyCollection<OrtValue> results = session.Run(
                runOptions, InputNames, inputValues, OutputNames);
            OrtValue[] first = results.ToArray();
            if (first.Length != OutputNames.Length)
                throw new InvalidDataException(
                    "RVM returned an unexpected number of outputs.");
            CopyAlpha(first[0].GetTensorDataAsSpan<float>(), alpha, pixels);
            InitializeStateBuffers(first);
            hasCurrentState = temporal;
            currentStateIsA = true;
            return;
        }

        OrtValue[] inputStates = hasCurrentState
            ? currentStateIsA ? stateAValues! : stateBValues!
            : initialStateValues;
        OrtValue[] outputStates = !hasCurrentState || !currentStateIsA
            ? stateAValues! : stateBValues!;
        inputValues[0] = sourceValue;
        for (int index = 0; index < 4; index++)
        {
            inputValues[index + 1] = inputStates[index];
            outputValues[index + 1] = outputStates[index];
        }
        inputValues[5] = ratioValue;
        outputValues[0] = matteValue!;
        session.Run(runOptions, InputNames, inputValues, OutputNames,
            outputValues);
        CopyAlpha(matte!, alpha, pixels);
        if (temporal)
        {
            hasCurrentState = true;
            currentStateIsA = ReferenceEquals(outputStates, stateAValues);
        }
    }

    void InitializeStateBuffers(OrtValue[] first)
    {
        int pixels = checked(Width * Height);
        matte = GC.AllocateUninitializedArray<float>(pixels);
        matteValue = OrtValue.CreateTensorValueFromMemory(matte,
            [1, 1, Height, Width]);
        stateAValues = new OrtValue[4];
        stateBValues = new OrtValue[4];
        try
        {
            for (int index = 0; index < 4; index++)
            {
                OrtValue result = first[index + 1];
                OrtTensorTypeAndShapeInfo info = result.GetTensorTypeAndShape();
                long[] shape = info.Shape;
                float[] stateA = result.GetTensorDataAsSpan<float>().ToArray();
                float[] stateB =
                    GC.AllocateUninitializedArray<float>(stateA.Length);
                stateAValues[index] = OrtValue.CreateTensorValueFromMemory(
                    stateA, shape);
                stateBValues[index] = OrtValue.CreateTensorValueFromMemory(
                    stateB, shape);
            }
            stateBuffersReady = true;
        }
        catch
        {
            DisposeValues(stateAValues);
            DisposeValues(stateBValues);
            matteValue?.Dispose();
            matteValue = null;
            throw;
        }
    }

    static void CopyAlpha(ReadOnlySpan<float> matte, byte[] alpha, int pixels)
    {
        if (matte.Length != pixels)
            throw new InvalidDataException(
                "RVM returned an unexpected alpha-mask size.");
        int output = 0;
        foreach (float value in matte)
            alpha[output++] = (byte)Math.Clamp(
                (int)(value * 255f + .5f), 0, 255);
    }

    internal void Reset() => hasCurrentState = false;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DisposeValues(stateAValues);
        DisposeValues(stateBValues);
        DisposeValues(initialStateValues);
        matteValue?.Dispose();
        sourceValue.Dispose();
        ratioValue.Dispose();
        runOptions.Dispose();
        session.Dispose();
    }

    static void DisposeValues(OrtValue[]? values)
    {
        if (values == null) return;
        foreach (OrtValue? value in values) value?.Dispose();
    }
}

internal sealed class RvmGpuSession : IDisposable
{
    const string Bridge = "IStripperRvmGpuBridge64.dll";
    IntPtr handle;

    [DllImport(Bridge, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern int RvmGpu_Create(string modelPath, int width, int height,
        float downsampleRatio, int temporal, out IntPtr handle,
        StringBuilder error, int errorCharacters);

    [DllImport(Bridge, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern int RvmGpu_Run(IntPtr handle, IntPtr texture,
        uint subresource, IntPtr fence, ulong fenceValue, int fullRange,
        int bt709, int p010, byte[] alpha, int alphaLength,
        int readback,
        StringBuilder error, int errorCharacters);

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)]
    static extern void RvmGpu_Reset(IntPtr handle);

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)]
    static extern void RvmGpu_Destroy(IntPtr handle);

    [DllImport(Bridge, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern int RvmGpu_CreateDisplaySharedHandle(IntPtr handle,
        out IntPtr sharedHandle, StringBuilder error, int errorCharacters);

    [DllImport(Bridge, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern int RvmGpu_CreateAlphaSharedHandle(IntPtr handle,
        out IntPtr sharedHandle, StringBuilder error, int errorCharacters);

    [DllImport(Bridge, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern int RvmGpu_VerifyD3D11Sharing(IntPtr handle,
        StringBuilder error, int errorCharacters);

    internal RvmGpuSession(string modelPath, int width, int height,
        CustomRvmOnnxSettings settings)
    {
        CustomShowStore.ValidateRvmOnnxSettings(settings);
        if (!RvmOnnxSupport.VerifyModel(modelPath, settings.Model))
            throw new FileNotFoundException(
                "The verified RVM ONNX real-time model is not installed.", modelPath);
        StringBuilder error = new(1024);
        int status = RvmGpu_Create(modelPath, width, height,
            RvmOnnxSupport.DownsampleRatio(width, height, settings.Quality),
            settings.Temporal ? 1 : 0, out handle, error, error.Capacity);
        if (status != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(
                error.ToString()) ? "The GPU RVM bridge could not initialize." :
                error.ToString());
    }

    internal void Run(FfmpegCpuDecoder decoder, byte[] alpha,
        bool readback = false)
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        FfmpegCpuDecoder.HardwareFrame frame = decoder.GetHardwareFrame();
        StringBuilder error = new(1024);
        int status = RvmGpu_Run(handle, frame.Texture,
            checked((uint)frame.SubresourceIndex), frame.Fence,
            frame.FenceValue, frame.FullRange ? 1 : 0, frame.Bt709 ? 1 : 0,
            frame.P010 ? 1 : 0, alpha, alpha.Length, readback ? 1 : 0,
            error, error.Capacity);
        if (status != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(
                error.ToString()) ? "DirectML could not process the GPU video frame." :
                error.ToString());
    }

    internal void VerifyD3D11Sharing()
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        StringBuilder error = new(1024);
        int status = RvmGpu_VerifyD3D11Sharing(handle, error, error.Capacity);
        if (status != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(
                error.ToString()) ? "D3D11 could not share the decoded frame." :
                error.ToString());
    }

    internal IntPtr CreateDisplaySharedHandle()
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        StringBuilder error = new(1024);
        int status = RvmGpu_CreateDisplaySharedHandle(handle,
            out IntPtr shared, error, error.Capacity);
        if (status != 0 || shared == IntPtr.Zero)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(
                error.ToString()) ? "The GPU display surface could not be shared." :
                error.ToString());
        return shared;
    }

    internal IntPtr CreateAlphaSharedHandle()
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        StringBuilder error = new(1024);
        int status = RvmGpu_CreateAlphaSharedHandle(handle,
            out IntPtr shared, error, error.Capacity);
        if (status != 0 || shared == IntPtr.Zero)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(
                error.ToString()) ? "The GPU alpha surface could not be shared." :
                error.ToString());
        return shared;
    }

    internal void Reset()
    {
        if (handle != IntPtr.Zero) RvmGpu_Reset(handle);
    }

    public void Dispose()
    {
        IntPtr released = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (released != IntPtr.Zero) RvmGpu_Destroy(released);
    }
}

internal sealed class RvmOnnxSetupForm : Form
{
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 38,
        Text = "Cancel" };
    readonly CancellationTokenSource cancellation = new();
    bool complete;

    internal RvmOnnxSetupForm()
    {
        Text = "Install RVM ONNX Real-Time Playback";
        ClientSize = new Size(720, 300);
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(output);
        Controls.Add(close);
        close.Click += (_, _) =>
        {
            if (!complete) cancellation.Cancel();
            Close();
        };
        FormClosing += (_, _) => { if (!complete) cancellation.Cancel(); };
        Shown += async (_, _) => await InstallAsync();
        AppTheme.Apply(this);
    }

    async Task InstallAsync()
    {
        try
        {
            Progress<string> progress = new(message =>
                output.AppendText(message + Environment.NewLine));
            await RvmOnnxSupport.InstallAsync(progress, cancellation.Token);
            foreach (string model in new[] { RvmOnnxSupport.MobileNetV3,
                RvmOnnxSupport.ResNet50 })
            {
                using RvmOnnxSession test = new(RvmOnnxSupport.ModelPathFor(model),
                    512, 288, new CustomRvmOnnxSettings
                    { Model = model, Quality = "fast", Temporal = true });
                byte[] input = new byte[512 * 288 * 3];
                byte[] alpha = new byte[512 * 288];
                test.Run(input, alpha);
            }
            output.AppendText("DirectML model load/run succeeded." +
                Environment.NewLine);
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            DialogResult = DialogResult.Cancel;
        }
        catch (Exception error)
        {
            output.AppendText("Setup failed: " + error.Message +
                Environment.NewLine);
            DialogResult = DialogResult.Abort;
        }
        finally
        {
            complete = true;
            close.Text = "Close";
        }
    }
}
