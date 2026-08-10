using OpenCvSharp;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ShaderVideoStreamer;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.ShowHelp)
            {
                Options.PrintHelp();
                return 0;
            }
            if (options.VideoPath.Length == 0)
            {
                FreeConsole();
                ApplicationConfiguration.Initialize();
                Application.Run(new VideoStreamerForm(options));
                return 0;
            }

            string token = File.ReadAllText(options.TokenPath).Trim();
            if (token.Length < 32)
                throw new InvalidDataException("The API token is invalid.");

            using CancellationTokenSource stopped = new();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopped.Cancel();
            };
            if (options.DurationSeconds > 0)
                stopped.CancelAfter(TimeSpan.FromSeconds(
                    options.DurationSeconds));

            using HttpClient client = new()
            {
                BaseAddress = new Uri(options.BaseUrl),
                Timeout = TimeSpan.FromSeconds(5)
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            PlaybackCommandReader playbackCommands = new();
            playbackCommands.Start(stopped.Token);

            using VideoCapture capture = new(options.VideoPath);
            if (!capture.IsOpened())
                throw new InvalidDataException(
                    $"OpenCV could not open '{options.VideoPath}'.");

            int sourceWidth = checked((int)Math.Round(
                capture.Get(VideoCaptureProperties.FrameWidth)));
            int sourceHeight = checked((int)Math.Round(
                capture.Get(VideoCaptureProperties.FrameHeight)));
            if (sourceWidth < 1 || sourceHeight < 1)
                throw new InvalidDataException(
                    "The video does not report valid dimensions.");

            (int width, int height) = FitDimensions(
                sourceWidth, sourceHeight, options.MaximumDimension);
            double sourceFps = capture.Get(VideoCaptureProperties.Fps);
            double fps = options.FramesPerSecond > 0
                ? options.FramesPerSecond
                : double.IsFinite(sourceFps) && sourceFps > 0
                    ? sourceFps : 30;
            double frameCount = capture.Get(
                VideoCaptureProperties.FrameCount);
            double mediaDurationSeconds =
                double.IsFinite(frameCount) && frameCount > 0 &&
                double.IsFinite(sourceFps) && sourceFps > 0
                    ? frameCount / sourceFps : 0;
            using SharedTexturePublisher publisher =
                await SharedTexturePublisher.ConnectAsync(
                    client, options.TextureName, width, height,
                    stopped.Token);

            Console.WriteLine(
                $"Streaming {Path.GetFileName(options.VideoPath)}");
            Console.WriteLine(
                $"{sourceWidth}x{sourceHeight} -> {width}x{height} RGBA8, " +
                $"{fps:F3} fps, texture '{options.TextureName}'.");
            Console.WriteLine(
                $"Shader sampler: u_QuickPlayerTexture_{options.TextureName}");

            return Stream(capture, publisher, width, height,
                fps, mediaDurationSeconds, options.Loop,
                playbackCommands, stopped);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Error: " + exception.Message);
            return 1;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private static int Stream(VideoCapture capture,
        SharedTexturePublisher publisher, int width, int height,
        double fps, double mediaDurationSeconds, bool loop,
        PlaybackCommandReader playbackCommands,
        CancellationTokenSource stopped)
    {
        CancellationToken cancellationToken = stopped.Token;
        long intervalTicks = Math.Max(1,
            (long)Math.Round(Stopwatch.Frequency / fps));
        long nextDue = Stopwatch.GetTimestamp();
        long sequence = 0;
        long published = 0;
        long skipped = 0;
        long windowPublished = 0;
        long windowSkipped = 0;
        double windowConversionMilliseconds = 0;
        double windowPublishMilliseconds = 0;
        Stopwatch report = Stopwatch.StartNew();
        Stopwatch progressReport = Stopwatch.StartNew();

        using Mat decoded = new();
        using Mat resized = new();
        using Mat rgba = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (playbackCommands.TryTakeSeek(out double seekSeconds))
            {
                double clampedSeek = mediaDurationSeconds > 0
                    ? Math.Clamp(seekSeconds, 0, mediaDurationSeconds)
                    : Math.Max(0, seekSeconds);
                capture.Set(VideoCaptureProperties.PosMsec,
                    clampedSeek * 1000);
                nextDue = Stopwatch.GetTimestamp();
                WriteProgress(clampedSeek, mediaDurationSeconds);
            }

            WaitUntil(nextDue, cancellationToken);
            nextDue += intervalTicks;

            if (!capture.Read(decoded) || decoded.Empty())
            {
                if (!loop)
                    break;
                capture.Set(VideoCaptureProperties.PosFrames, 0);
                nextDue = Stopwatch.GetTimestamp();
                WriteProgress(0, mediaDurationSeconds);
                continue;
            }

            long conversionStarted = Stopwatch.GetTimestamp();
            Mat frame = decoded;
            if (decoded.Width != width || decoded.Height != height)
            {
                Cv2.Resize(decoded, resized, new OpenCvSharp.Size(
                    width, height), 0, 0, InterpolationFlags.Area);
                frame = resized;
            }
            ConvertToRgba(frame, rgba);
            double conversionMilliseconds = Stopwatch.GetElapsedTime(
                conversionStarted).TotalMilliseconds;

            long publishStarted = Stopwatch.GetTimestamp();
            publisher.Publish(width, height, rgba.Data,
                checked(width * height * 4),
                (uint)(sequence & 0x00FFFFFF));
            double publishMilliseconds = Stopwatch.GetElapsedTime(
                publishStarted).TotalMilliseconds;
            sequence++;
            published++;
            windowPublished++;
            windowConversionMilliseconds += conversionMilliseconds;
            windowPublishMilliseconds += publishMilliseconds;
            long now = Stopwatch.GetTimestamp();
            while (now >= nextDue + intervalTicks && capture.Grab())
            {
                nextDue += intervalTicks;
                skipped++;
                windowSkipped++;
            }

            if (progressReport.ElapsedMilliseconds >= 100)
            {
                double positionSeconds = capture.Get(
                    VideoCaptureProperties.PosMsec) / 1000;
                WriteProgress(positionSeconds, mediaDurationSeconds);
                progressReport.Restart();
            }

            if (report.ElapsedMilliseconds >= 1_000)
            {
                double seconds = report.Elapsed.TotalSeconds;
                Console.WriteLine(
                    $"Sent {windowPublished / seconds,6:F1}/s | " +
                    $"convert {windowConversionMilliseconds /
                        Math.Max(1, windowPublished),6:F3} ms | " +
                    $"publish {windowPublishMilliseconds /
                        Math.Max(1, windowPublished),6:F3} ms | " +
                    $"skipped {windowSkipped} | " +
                    $"pending {publisher.PendingFrames}");
                windowPublished = 0;
                windowSkipped = 0;
                windowConversionMilliseconds = 0;
                windowPublishMilliseconds = 0;
                report.Restart();
            }
        }

        Console.WriteLine(
            $"Finished: {published} frames published, {skipped} skipped.");
        return 0;
    }

    private static void WriteProgress(
        double positionSeconds, double durationSeconds)
    {
        Console.WriteLine("QP_PROGRESS|" +
            Math.Max(0, positionSeconds).ToString(
                "R", CultureInfo.InvariantCulture) + "|" +
            Math.Max(0, durationSeconds).ToString(
                "R", CultureInfo.InvariantCulture));
    }

    private static void ConvertToRgba(Mat source, Mat destination)
    {
        ColorConversionCodes conversion = source.Channels() switch
        {
            1 => ColorConversionCodes.GRAY2RGBA,
            3 => ColorConversionCodes.BGR2RGBA,
            4 => ColorConversionCodes.BGRA2RGBA,
            _ => throw new InvalidDataException(
                $"Unsupported {source.Channels()}-channel video frame.")
        };
        Cv2.CvtColor(source, destination, conversion);
        if (!destination.IsContinuous())
            throw new InvalidDataException(
                "The converted video frame is not contiguous.");
    }

    private static (int Width, int Height) FitDimensions(
        int width, int height, int maximumDimension)
    {
        double scale = Math.Min(1d,
            maximumDimension / (double)Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static void WaitUntil(long target,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = target - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return;
            double milliseconds = remaining * 1000d / Stopwatch.Frequency;
            if (milliseconds > 2)
                Thread.Sleep(Math.Max(1, (int)milliseconds - 1));
            else
                Thread.SpinWait(128);
        }
    }
}

internal sealed class PlaybackCommandReader
{
    private long pendingSeekBits = BitConverter.DoubleToInt64Bits(double.NaN);

    public void Start(CancellationToken cancellationToken)
    {
        if (!Console.IsInputRedirected)
            return;
        _ = Task.Run(() => ReadCommands(cancellationToken),
            CancellationToken.None);
    }

    public bool TryTakeSeek(out double seconds)
    {
        long bits = Interlocked.Exchange(ref pendingSeekBits,
            BitConverter.DoubleToInt64Bits(double.NaN));
        seconds = BitConverter.Int64BitsToDouble(bits);
        return double.IsFinite(seconds) && seconds >= 0;
    }

    private void ReadCommands(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = Console.ReadLine();
            if (line == null)
                return;
            line = line.TrimStart('\uFEFF');
            int commandStart = line.IndexOf(
                "SEEK ", StringComparison.OrdinalIgnoreCase);
            if (commandStart < 0)
                continue;
            if (double.TryParse(line.AsSpan(commandStart + 5),
                NumberStyles.Float, CultureInfo.InvariantCulture,
                out double seconds) &&
                double.IsFinite(seconds) && seconds >= 0)
            {
                Interlocked.Exchange(ref pendingSeekBits,
                    BitConverter.DoubleToInt64Bits(seconds));
            }
        }
    }
}

internal sealed record Options(
    string VideoPath,
    string TextureName,
    string BaseUrl,
    string TokenPath,
    int MaximumDimension,
    double FramesPerSecond,
    double DurationSeconds,
    bool Loop,
    bool ShowHelp)
{
    public static Options Parse(string[] args)
    {
        string videoPath = "";
        string textureName = "video1";
        string baseUrl = "http://127.0.0.1:17871/api/v1/";
        string tokenPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "api-token.txt");
        int maximumDimension = 4096;
        double framesPerSecond = 0;
        double durationSeconds = 0;
        bool loop = true;
        bool help = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            string Value() => index + 1 < args.Length
                ? args[++index]
                : throw new ArgumentException(
                    $"A value is required after {argument}.");
            switch (argument.ToLowerInvariant())
            {
                case "-h":
                case "--help": help = true; break;
                case "--name": textureName = Value(); break;
                case "--base-url": baseUrl = Value(); break;
                case "--token": tokenPath = Value(); break;
                case "--max-dimension":
                    maximumDimension = int.Parse(Value()); break;
                case "--fps": framesPerSecond = double.Parse(Value(),
                    System.Globalization.CultureInfo.InvariantCulture); break;
                case "--duration": durationSeconds = double.Parse(Value(),
                    System.Globalization.CultureInfo.InvariantCulture); break;
                case "--no-loop": loop = false; break;
                default:
                    if (argument.StartsWith('-'))
                        throw new ArgumentException(
                            $"Unknown argument '{argument}'.");
                    if (videoPath.Length != 0)
                        throw new ArgumentException(
                            "Only one video path may be supplied.");
                    videoPath = argument;
                    break;
            }
        }

        if (videoPath.Length != 0)
            videoPath = Path.GetFullPath(videoPath);
        if (videoPath.Length != 0 && !File.Exists(videoPath))
            throw new FileNotFoundException("The video was not found.",
                videoPath);
        ValidateTextureName(textureName);
        if (maximumDimension is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        if (!double.IsFinite(framesPerSecond) ||
            framesPerSecond < 0 || framesPerSecond > 240)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (!baseUrl.EndsWith('/'))
            baseUrl += '/';

        return new(videoPath, textureName, baseUrl, tokenPath,
            maximumDimension, framesPerSecond, durationSeconds,
            loop, help);
    }

    private static void ValidateTextureName(string name)
    {
        bool Letter(char value) =>
            value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        if (name.Length is < 1 or > 32 || !Letter(name[0]) ||
            name.Skip(1).Any(value =>
                !Letter(value) && !char.IsAsciiDigit(value) && value != '_'))
            throw new ArgumentException("Texture name must be a 1-32 " +
                "character shader identifier starting with a letter.");
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Stream a video into a QuickPlayer fullscreen shader texture.

            Usage:
              ShaderVideoStreamer.exe                 Open the Windows UI
              ShaderVideoStreamer.exe <video> [options]

            Options:
              --name <name>          Texture name (default video1)
              --max-dimension <px>   Resize limit, 1..4096 (default 4096)
              --fps <rate>           Override source FPS (maximum 240)
              --duration <seconds>   Stop automatically; 0 means Ctrl+C
              --no-loop              Stop at the end instead of looping
              --base-url <url>       QuickPlayer API v1 base URL
              --token <path>         QuickPlayer API token file
              -h, --help             Show this help
            """);
    }
}

internal sealed class SharedTexturePublisher : IDisposable
{
    private const uint ExpectedMagic = 0x4D545051;
    private readonly SharedTextureDescription description;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle updatedEvent;
    private unsafe byte* pointer;
    private long nextGeneration;
    private bool disposed;

    private unsafe SharedTexturePublisher(
        SharedTextureDescription description)
    {
        this.description = description;
        mapping = MemoryMappedFile.OpenExisting(
            description.MappingName, MemoryMappedFileRights.ReadWrite);
        view = mapping.CreateViewAccessor(0, description.MappingLength,
            MemoryMappedFileAccess.ReadWrite);
        byte* acquired = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref acquired);
        pointer = acquired + view.PointerOffset;
        updatedEvent = EventWaitHandle.OpenExisting(description.EventName);

        if (ReadUInt32(description.Offsets.Magic) != ExpectedMagic ||
            ReadUInt32(description.Offsets.Version) !=
                description.ProtocolVersion ||
            ReadUInt32(description.Offsets.HeaderLength) !=
                description.HeaderLength ||
            ReadUInt32(description.Offsets.SlotCount) !=
                description.SlotCount ||
            ReadUInt32(description.Offsets.SlotCapacity) !=
                description.SlotCapacity)
            throw new InvalidDataException(
                "The shared texture header is incompatible.");

        long published = Volatile.Read(
            ref Int64At(description.Offsets.PublishedGeneration));
        nextGeneration = Math.Max(0, published & ~1L);
    }

    public static async Task<SharedTexturePublisher> ConnectAsync(
        HttpClient client, string textureName, int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "fullscreen/shader-texture/shared-memory?name=" +
                Uri.EscapeDataString(textureName) +
                "&maxWidth=" + maximumWidth +
                "&maxHeight=" + maximumHeight,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        SharedTextureDescription description =
            await JsonSerializer.DeserializeAsync<SharedTextureDescription>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken) ??
            throw new InvalidDataException(
                "The shared texture description is missing.");
        if (!StringComparer.Ordinal.Equals(description.Name, textureName))
            throw new InvalidDataException(
                "QuickPlayer returned a different texture channel.");
        return new SharedTexturePublisher(description);
    }

    public long PublishedFrames => Math.Max(0,
        Volatile.Read(ref Int64At(
            description.Offsets.PublishedGeneration)) / 2);

    public long ConsumedFrames => Math.Max(0,
        Volatile.Read(ref Int64At(
            description.Offsets.ConsumedGeneration)) / 2);

    public long PendingFrames => Math.Max(0,
        PublishedFrames - ConsumedFrames);

    public unsafe void Publish(int width, int height, nint rgba,
        int byteLength, uint producerSequence)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (width is < 1 || height is < 1 ||
            width > description.MaximumWidth ||
            height > description.MaximumHeight ||
            byteLength != checked(width * height * 4) ||
            byteLength > description.SlotCapacity || rgba == 0)
            throw new ArgumentException(
                "The frame does not fit the shared texture channel.");

        long publishedGeneration = checked(nextGeneration + 2);
        Volatile.Write(ref Int64At(
            description.Offsets.PublishedGeneration),
            publishedGeneration - 1);

        uint slot = (uint)((publishedGeneration / 2) %
            description.SlotCount);
        byte* destination = pointer + description.Offsets.Pixels +
            slot * description.SlotCapacity;
        Buffer.MemoryCopy((void*)rgba, destination,
            description.SlotCapacity, byteLength);
        WriteUInt32(description.Offsets.PublishedSlot, slot);
        WriteUInt32(description.Offsets.Width, (uint)width);
        WriteUInt32(description.Offsets.Height, (uint)height);
        WriteUInt32(description.Offsets.ByteLength, (uint)byteLength);
        WriteUInt32(description.Offsets.ProducerSequence,
            producerSequence);
        Thread.MemoryBarrier();
        Volatile.Write(ref Int64At(
            description.Offsets.PublishedGeneration),
            publishedGeneration);
        nextGeneration = publishedGeneration;
        updatedEvent.Set();
    }

    private unsafe uint ReadUInt32(int offset) =>
        *(uint*)(pointer + offset);

    private unsafe void WriteUInt32(int offset, uint value) =>
        *(uint*)(pointer + offset) = value;

    private unsafe ref long Int64At(int offset) =>
        ref *(long*)(pointer + offset);

    public unsafe void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        updatedEvent.Dispose();
        if (pointer != null)
        {
            pointer = null;
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        view.Dispose();
        mapping.Dispose();
    }
}

internal sealed record SharedTextureDescription(
    string Name,
    uint ProtocolVersion,
    string MappingName,
    string EventName,
    long MappingLength,
    int HeaderLength,
    int SlotCount,
    int SlotCapacity,
    int MaximumWidth,
    int MaximumHeight,
    string Format,
    string Orientation,
    SharedTextureOffsets Offsets);

internal sealed record SharedTextureOffsets(
    int Magic,
    int Version,
    int HeaderLength,
    int SlotCount,
    int SlotCapacity,
    int PublishedSlot,
    int Width,
    int Height,
    int ByteLength,
    int ProducerSequence,
    int PublishedGeneration,
    int ConsumedGeneration,
    int Pixels);
