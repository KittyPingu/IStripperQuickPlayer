using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ShaderTextureLatencyTest;

internal static class Program
{
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

            string token = File.ReadAllText(options.TokenPath).Trim();
            if (token.Length < 32)
                throw new InvalidDataException("The API token is invalid.");

            using CancellationTokenSource stopped = new();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopped.Cancel();
            };

            using SocketsHttpHandler handler = new()
            {
                UseProxy = false,
                MaxConnectionsPerServer = 1,
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                AutomaticDecompression = DecompressionMethods.None
            };
            using HttpClient client = new(handler)
            {
                BaseAddress = new Uri(options.BaseUrl),
                Timeout = TimeSpan.FromSeconds(5),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            await SetOpacityAsync(client, options.Opacity, stopped.Token);

            RgbaStopwatchRenderer[] renderers = Enumerable.Range(
                0, options.TextureCount)
                .Select(index => new RgbaStopwatchRenderer(
                    options.Width, options.Height, index))
                .ToArray();
            foreach (RgbaStopwatchRenderer renderer in renderers)
                renderer.Render(TimeSpan.Zero, 0);
            if (options.SharedMemory)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException(
                        "Shared-memory mode requires Windows.");
                SharedTexturePublisher[] publishers =
                    new SharedTexturePublisher[options.TextureCount];
                for (int index = 0; index < publishers.Length; index++)
                    publishers[index] =
                        await SharedTexturePublisher.ConnectAsync(client,
                            $"texture{index + 1}", options.Width,
                            options.Height, stopped.Token);
                Console.WriteLine("Warming the shared-memory channel...");
                for (int index = 0; index < publishers.Length; index++)
                    publishers[index].Publish(options.Width, options.Height,
                        renderers[index].Pixels, 0);
                Console.WriteLine(
                    $"Publishing {options.TextureCount} named " +
                    $"{options.Width}x{options.Height} RGBA8 textures " +
                    $"through shared memory at a " +
                    $"{options.IntervalMilliseconds:0.###} ms target interval.");
                Console.WriteLine("Press Ctrl+C to stop.\n");
                GCLatencyMode sharedPreviousLatency =
                    GCSettings.LatencyMode;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                timeBeginPeriod(1);
                try
                {
                    return RunShared(publishers, renderers, options,
                        stopped.Token);
                }
                finally
                {
                    timeEndPeriod(1);
                    GCSettings.LatencyMode = sharedPreviousLatency;
                    foreach (SharedTexturePublisher publisher in publishers)
                        publisher.Dispose();
                }
            }

            Uri[] textureUris = Enumerable.Range(1, options.TextureCount)
                .Select(index => new Uri(
                    $"fullscreen/shader-texture?width={options.Width}" +
                    $"&height={options.Height}&name=texture{index}",
                    UriKind.Relative)).ToArray();

            Console.WriteLine("Warming the API connection...");
            for (int index = 0; index < textureUris.Length; index++)
                await SendTextureAsync(client, textureUris[index],
                    renderers[index].Pixels, stopped.Token);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Console.WriteLine(
                $"Sending {options.TextureCount} named " +
                $"{options.Width}x{options.Height} RGBA8 textures at a " +
                $"{options.IntervalMilliseconds:0.###} ms target interval.");
            Console.WriteLine("Press Ctrl+C to stop.\n");

            GCLatencyMode previousLatency = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            timeBeginPeriod(1);
            try
            {
                return await RunAsync(client, textureUris, renderers,
                    options, stopped.Token);
            }
            finally
            {
                timeEndPeriod(1);
                GCSettings.LatencyMode = previousLatency;
            }
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

    [SupportedOSPlatform("windows")]
    private static int RunShared(SharedTexturePublisher[] publishers,
        RgbaStopwatchRenderer[] renderers, Options options,
        CancellationToken cancellationToken)
    {
        long intervalTicks = Math.Max(1, (long)Math.Round(
            Stopwatch.Frequency * options.IntervalMilliseconds / 1000d));
        long nextDue = Stopwatch.GetTimestamp();
        long frame = 1;
        long totalPublished = 0;
        long totalMissed = 0;
        long lastConsumed = publishers.Sum(item => item.ConsumedFrame);
        long windowPublished = 0;
        long windowMissed = 0;
        double totalPublishMilliseconds = 0;
        double totalRenderMilliseconds = 0;
        double maximumPublishMilliseconds = 0;
        List<double> windowLatencies = new(256);
        List<double> windowRenderLatencies = new(256);
        Stopwatch runtime = Stopwatch.StartNew();
        Stopwatch reportClock = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested &&
            (options.DurationSeconds <= 0 ||
             runtime.Elapsed.TotalSeconds < options.DurationSeconds))
        {
            long now = Stopwatch.GetTimestamp();
            if (now > nextDue)
            {
                long skipped = (now - nextDue) / intervalTicks;
                if (skipped > 0)
                {
                    nextDue += skipped * intervalTicks;
                    totalMissed += skipped;
                    windowMissed += skipped;
                }
            }
            WaitUntil(nextDue, cancellationToken);
            nextDue += intervalTicks;

            long renderStarted = Stopwatch.GetTimestamp();
            foreach (RgbaStopwatchRenderer renderer in renderers)
                renderer.Render(runtime.Elapsed, frame);
            double renderMilliseconds = Stopwatch.GetElapsedTime(
                renderStarted).TotalMilliseconds;
            long publishStarted = Stopwatch.GetTimestamp();
            for (int index = 0; index < publishers.Length; index++)
                publishers[index].Publish(options.Width, options.Height,
                    renderers[index].Pixels,
                    (uint)(frame & 0x00FFFFFF));
            double publishMilliseconds = Stopwatch.GetElapsedTime(
                publishStarted).TotalMilliseconds;
            frame++;
            totalPublished++;
            windowPublished++;
            totalPublishMilliseconds += publishMilliseconds;
            totalRenderMilliseconds += renderMilliseconds;
            maximumPublishMilliseconds = Math.Max(
                maximumPublishMilliseconds, publishMilliseconds);
            windowLatencies.Add(publishMilliseconds);
            windowRenderLatencies.Add(renderMilliseconds);

            now = Stopwatch.GetTimestamp();
            if (now >= nextDue)
            {
                long skipped = (now - nextDue) / intervalTicks + 1;
                nextDue += skipped * intervalTicks;
                totalMissed += skipped;
                windowMissed += skipped;
            }

            if (reportClock.ElapsedMilliseconds >= 1_000)
            {
                long consumed = publishers.Sum(item => item.ConsumedFrame);
                PrintSharedWindow(reportClock.Elapsed, windowPublished,
                    consumed - lastConsumed, windowLatencies,
                    windowRenderLatencies, windowMissed,
                    publishers.Sum(item => item.PendingFrames),
                    publishers.Length);
                lastConsumed = consumed;
                reportClock.Restart();
                windowPublished = 0;
                windowMissed = 0;
                windowLatencies.Clear();
                windowRenderLatencies.Clear();
            }
        }

        if (windowPublished > 0)
        {
            long consumed = publishers.Sum(item => item.ConsumedFrame);
            PrintSharedWindow(reportClock.Elapsed, windowPublished,
                consumed - lastConsumed, windowLatencies,
                windowRenderLatencies, windowMissed,
                publishers.Sum(item => item.PendingFrames),
                publishers.Length);
        }
        Stopwatch finalAcknowledgement = Stopwatch.StartNew();
        while (publishers.Sum(item => item.PendingFrames) > 0 &&
            finalAcknowledgement.ElapsedMilliseconds < 500)
            Thread.SpinWait(128);
        Console.WriteLine();
        Console.WriteLine(
            $"Total: {totalPublished} published in " +
            $"{runtime.Elapsed.TotalSeconds:F2}s " +
            $"({totalPublished / runtime.Elapsed.TotalSeconds:F1}/s), " +
            $"average publish {totalPublishMilliseconds /
                Math.Max(1, totalPublished):F3} ms, " +
            $"average render {totalRenderMilliseconds /
                Math.Max(1, totalPublished):F3} ms, " +
            $"maximum {maximumPublishMilliseconds:F3} ms, " +
            $"missed {totalMissed}, pending " +
            $"{publishers.Sum(item => item.PendingFrames)}.");
        return 0;
    }

    private static void PrintSharedWindow(TimeSpan elapsed,
        long published, long consumed, List<double> latencies,
        List<double> renderLatencies, long missed, long pending,
        int textureCount)
    {
        latencies.Sort();
        Console.WriteLine(
            $"Sets {published / elapsed.TotalSeconds,6:F1}/s " +
            $"({published * textureCount / elapsed.TotalSeconds,6:F1} " +
            "textures/s) | " +
            $"consumed {consumed / elapsed.TotalSeconds,6:F1}/s | " +
            $"publish ms avg {latencies.Average(),6:F3}  " +
            $"render {renderLatencies.Average(),6:F3}  " +
            $"p95 {Percentile(latencies, 0.95),6:F3}  " +
            $"max {latencies[^1],7:F3} | missed {missed}  " +
            $"pending {pending}");
    }

    private static async Task<int> RunAsync(HttpClient client,
        Uri[] textureUris, RgbaStopwatchRenderer[] renderers,
        Options options,
        CancellationToken cancellationToken)
    {
        long intervalTicks = Math.Max(1, (long)Math.Round(
            Stopwatch.Frequency * options.IntervalMilliseconds / 1000d));
        long nextDue = Stopwatch.GetTimestamp();
        long frame = 0;
        long totalSent = 0;
        long totalMissed = 0;
        double totalLatency = 0;
        double maximumLatency = 0;
        Stopwatch runtime = Stopwatch.StartNew();
        Stopwatch reportClock = Stopwatch.StartNew();
        List<double> windowLatencies = new(256);
        long windowMissed = 0;

        while (!cancellationToken.IsCancellationRequested &&
            (options.DurationSeconds <= 0 ||
             runtime.Elapsed.TotalSeconds < options.DurationSeconds))
        {
            long now = Stopwatch.GetTimestamp();
            if (now > nextDue)
            {
                long skipped = (now - nextDue) / intervalTicks;
                if (skipped > 0)
                {
                    nextDue += skipped * intervalTicks;
                    totalMissed += skipped;
                    windowMissed += skipped;
                }
            }
            WaitUntil(nextDue, cancellationToken);
            nextDue += intervalTicks;

            foreach (RgbaStopwatchRenderer renderer in renderers)
                renderer.Render(runtime.Elapsed, frame);
            long requestStart = Stopwatch.GetTimestamp();
            for (int index = 0; index < textureUris.Length; index++)
                await SendTextureAsync(client, textureUris[index],
                    renderers[index].Pixels, cancellationToken);
            double latency = Stopwatch.GetElapsedTime(requestStart)
                .TotalMilliseconds;

            frame++;
            totalSent++;
            totalLatency += latency;
            maximumLatency = Math.Max(maximumLatency, latency);
            windowLatencies.Add(latency);

            now = Stopwatch.GetTimestamp();
            if (now >= nextDue)
            {
                long skipped = (now - nextDue) / intervalTicks + 1;
                nextDue += skipped * intervalTicks;
                totalMissed += skipped;
                windowMissed += skipped;
            }

            if (reportClock.ElapsedMilliseconds >= 1_000)
            {
                PrintWindow(reportClock.Elapsed, windowLatencies,
                    windowMissed, textureUris.Length);
                reportClock.Restart();
                windowLatencies.Clear();
                windowMissed = 0;
            }
        }

        if (windowLatencies.Count > 0)
            PrintWindow(reportClock.Elapsed, windowLatencies, windowMissed,
                textureUris.Length);
        Console.WriteLine();
        Console.WriteLine(
            $"Total: {totalSent} sent in {runtime.Elapsed.TotalSeconds:F2}s " +
            $"({totalSent / runtime.Elapsed.TotalSeconds:F1}/s), " +
            $"average HTTP {totalLatency / Math.Max(1, totalSent):F3} ms, " +
            $"maximum {maximumLatency:F3} ms, missed {totalMissed}.");
        return 0;
    }

    private static void PrintWindow(TimeSpan elapsed,
        List<double> latencies, long missed, int textureCount)
    {
        latencies.Sort();
        double average = latencies.Average();
        Console.WriteLine(
            $"Sets {latencies.Count / elapsed.TotalSeconds,6:F1}/s " +
            $"({latencies.Count * textureCount /
                elapsed.TotalSeconds,6:F1} textures/s) | " +
            $"HTTP ms avg {average,6:F3}  " +
            $"p50 {Percentile(latencies, 0.50),6:F3}  " +
            $"p95 {Percentile(latencies, 0.95),6:F3}  " +
            $"p99 {Percentile(latencies, 0.99),6:F3}  " +
            $"max {latencies[^1],7:F3} | missed {missed}");
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(sorted.Count * percentile) - 1,
            0, sorted.Count - 1);
        return sorted[index];
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

    private static async Task SetOpacityAsync(HttpClient client,
        float opacity, CancellationToken cancellationToken)
    {
        string json = $"{{\"values\":[{opacity.ToString(
            System.Globalization.CultureInfo.InvariantCulture)},0,0,0]}}";
        using StringContent content = new(
            json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PutAsync(
            "fullscreen/shader-data", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task SendTextureAsync(HttpClient client,
        Uri textureUri, byte[] pixels, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Put, textureUri);
        request.Headers.ExpectContinue = false;
        request.Content = new ByteArrayContent(pixels);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(
                cancellationToken);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode}: {body}");
        }
        await response.Content.CopyToAsync(Stream.Null, cancellationToken);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}

[SupportedOSPlatform("windows")]
internal sealed class SharedTexturePublisher : IDisposable
{
    private const uint ExpectedMagic = 0x4D545051;
    private readonly SharedTextureDescription description;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle updatedEvent;
    private nint pointer;
    private long nextGeneration;
    private bool disposed;

    private SharedTexturePublisher(
        SharedTextureDescription description)
    {
        this.description = description;
        mapping = MemoryMappedFile.OpenExisting(
            description.MappingName, MemoryMappedFileRights.ReadWrite);
        view = mapping.CreateViewAccessor(0, description.MappingLength,
            MemoryMappedFileAccess.ReadWrite);
        unsafe
        {
            byte* acquired = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref acquired);
            pointer = (nint)(acquired + view.PointerOffset);
        }
        updatedEvent = EventWaitHandle.OpenExisting(
            description.EventName);

        if (ReadUInt32(description.Offsets.Magic) != ExpectedMagic ||
            ReadUInt32(description.Offsets.Version) !=
                description.ProtocolVersion ||
            ReadUInt32(description.Offsets.HeaderLength) !=
                description.HeaderLength ||
            ReadUInt32(description.Offsets.SlotCount) !=
                description.SlotCount ||
            ReadUInt32(description.Offsets.SlotCapacity) !=
                description.SlotCapacity)
        {
            throw new InvalidDataException(
                "The shared texture header is not compatible.");
        }

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
            await JsonSerializer.DeserializeAsync<
                SharedTextureDescription>(stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken) ??
            throw new InvalidDataException(
                "The shared texture description is missing.");
        return new SharedTexturePublisher(description);
    }

    public long ConsumedFrame => Math.Max(0,
        Volatile.Read(ref Int64At(
            description.Offsets.ConsumedGeneration)) / 2);

    public long PublishedFrame => Math.Max(0,
        Volatile.Read(ref Int64At(
            description.Offsets.PublishedGeneration)) / 2);

    public long PendingFrames => Math.Max(
        0, PublishedFrame - ConsumedFrame);

    public unsafe void Publish(int width, int height,
        byte[] rgba, uint producerSequence)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int byteLength = checked(width * height * 4);
        if (width is < 1 || height is < 1 ||
            width > description.MaximumWidth ||
            height > description.MaximumHeight ||
            byteLength > description.SlotCapacity ||
            rgba.Length != byteLength)
        {
            throw new ArgumentException(
                "The frame does not fit the shared texture channel.",
                nameof(rgba));
        }

        long publishedGeneration = checked(nextGeneration + 2);
        long writingGeneration = publishedGeneration - 1;
        Volatile.Write(ref Int64At(
            description.Offsets.PublishedGeneration), writingGeneration);

        uint slot = (uint)((publishedGeneration / 2) %
            description.SlotCount);
        byte* destination = (byte*)pointer + description.Offsets.Pixels +
            slot * description.SlotCapacity;
        fixed (byte* source = rgba)
        {
            Buffer.MemoryCopy(source, destination,
                description.SlotCapacity, byteLength);
        }
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
        *(uint*)((byte*)pointer + offset);

    private unsafe void WriteUInt32(int offset, uint value) =>
        *(uint*)((byte*)pointer + offset) = value;

    private unsafe ref long Int64At(int offset) =>
        ref *(long*)((byte*)pointer + offset);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        updatedEvent.Dispose();
        if (pointer != 0)
        {
            pointer = 0;
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        view.Dispose();
        mapping.Dispose();
    }
}

internal sealed record SharedTextureDescription(
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

internal sealed class RgbaStopwatchRenderer
{
    private static readonly byte[][] Glyphs =
    [
        [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
        [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        [0b11111, 0b10000, 0b10000, 0b11110, 0b00001, 0b00001, 0b11110],
        [0b01110, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110]
    ];
    private static readonly byte[] Colon =
        [0, 0b00100, 0b00100, 0, 0b00100, 0b00100, 0];
    private static readonly byte[] Period =
        [0, 0, 0, 0, 0, 0b00100, 0b00100];

    private readonly int width;
    private readonly int height;
    private readonly byte[] background;
    private readonly byte foregroundRed;
    private readonly byte foregroundGreen;
    private readonly byte foregroundBlue;
    private readonly byte accentRed;
    private readonly byte accentGreen;
    private readonly byte accentBlue;

    public RgbaStopwatchRenderer(int width, int height, int palette)
    {
        this.width = width;
        this.height = height;
        (foregroundRed, foregroundGreen, foregroundBlue,
            accentRed, accentGreen, accentBlue) = (palette % 3) switch
        {
            0 => ((byte)70, (byte)225, (byte)255,
                (byte)255, (byte)90, (byte)155),
            1 => ((byte)255, (byte)205, (byte)70,
                (byte)90, (byte)255, (byte)150),
            _ => ((byte)205, (byte)125, (byte)255,
                (byte)255, (byte)145, (byte)55)
        };
        Pixels = new byte[checked(width * height * 4)];
        background = new byte[Pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte shade = (byte)(((x / 16 + y / 16) & 1) == 0 ? 8 : 13);
                background[offset] = shade;
                background[offset + 1] = shade;
                background[offset + 2] = (byte)(shade + 5);
                background[offset + 3] = 255;
            }
        }
    }

    public byte[] Pixels { get; }

    public void Render(TimeSpan elapsed, long frame)
    {
        background.AsSpan().CopyTo(Pixels);
        long totalMilliseconds = (long)elapsed.TotalMilliseconds;
        int minutes = (int)(totalMilliseconds / 60_000) % 100;
        int seconds = (int)(totalMilliseconds / 1_000) % 60;
        int milliseconds = (int)(totalMilliseconds % 1_000);
        string time = $"{minutes:00}:{seconds:00}.{milliseconds:000}";
        int timeScale = Math.Max(1, Math.Min(
            (width - 12) / (time.Length * 6 - 1),
            Math.Max(1, (height / 2 - 8) / 7)));
        int timeWidth = (time.Length * 6 - 1) * timeScale;
        DrawText(time, (width - timeWidth) / 2, 10,
            timeScale, foregroundRed, foregroundGreen, foregroundBlue);

        string frameText = (frame % 100_000_000).ToString("D8");
        int frameScale = Math.Max(1, Math.Min(
            (width - 12) / (frameText.Length * 6 - 1),
            Math.Max(1, (height / 3 - 6) / 7)));
        int frameWidth = (frameText.Length * 6 - 1) * frameScale;
        DrawText(frameText, (width - frameWidth) / 2,
            Math.Max(10 + 7 * timeScale + 8,
                height - 7 * frameScale - 14),
            frameScale, accentRed, accentGreen, accentBlue);

        int progress = (int)((long)(width - 4) * milliseconds / 999);
        FillRectangle(2, height - 5, progress, 3,
            foregroundRed, foregroundGreen, foregroundBlue);
    }

    private void DrawText(string text, int x, int y, int scale,
        byte red, byte green, byte blue)
    {
        foreach (char character in text)
        {
            byte[] glyph = character switch
            {
                >= '0' and <= '9' => Glyphs[character - '0'],
                ':' => Colon,
                '.' => Period,
                _ => throw new InvalidOperationException(
                    $"Unsupported glyph '{character}'.")
            };
            for (int row = 0; row < 7; row++)
            {
                for (int column = 0; column < 5; column++)
                {
                    if ((glyph[row] & (1 << (4 - column))) != 0)
                    {
                        FillRectangle(x + column * scale,
                            y + row * scale, scale, scale,
                            red, green, blue);
                    }
                }
            }
            x += 6 * scale;
        }
    }

    private void FillRectangle(int x, int y, int rectangleWidth,
        int rectangleHeight, byte red, byte green, byte blue)
    {
        int right = Math.Min(width, x + rectangleWidth);
        int bottom = Math.Min(height, y + rectangleHeight);
        for (int row = Math.Max(0, y); row < bottom; row++)
        {
            for (int column = Math.Max(0, x); column < right; column++)
            {
                int offset = (row * width + column) * 4;
                Pixels[offset] = red;
                Pixels[offset + 1] = green;
                Pixels[offset + 2] = blue;
                Pixels[offset + 3] = 255;
            }
        }
    }
}

internal sealed record Options(
    string BaseUrl,
    string TokenPath,
    double IntervalMilliseconds,
    int Width,
    int Height,
    double DurationSeconds,
    float Opacity,
    bool SharedMemory,
    int TextureCount,
    bool ShowHelp)
{
    public static Options Parse(string[] args)
    {
        string baseUrl = "http://127.0.0.1:17871/api/v1/";
        string tokenPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "api-token.txt");
        double interval = 10;
        int width = 1024;
        int height = 512;
        double duration = 0;
        float opacity = 1;
        bool sharedMemory = false;
        int textureCount = 3;
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
                case "--base-url": baseUrl = Value(); break;
                case "--token": tokenPath = Value(); break;
                case "--interval-ms": interval = double.Parse(Value(),
                    System.Globalization.CultureInfo.InvariantCulture); break;
                case "--width": width = int.Parse(Value()); break;
                case "--height": height = int.Parse(Value()); break;
                case "--duration": duration = double.Parse(Value(),
                    System.Globalization.CultureInfo.InvariantCulture); break;
                case "--opacity": opacity = float.Parse(Value(),
                    System.Globalization.CultureInfo.InvariantCulture); break;
                case "--shared-memory": sharedMemory = true; break;
                case "--textures": textureCount = int.Parse(Value()); break;
                default: throw new ArgumentException(
                    $"Unknown argument '{argument}'.");
            }
        }

        if (!baseUrl.EndsWith('/')) baseUrl += '/';
        if (interval <= 0 || interval > 60_000)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (width is < 32 or > 4096 || height is < 32 or > 4096)
            throw new ArgumentOutOfRangeException(
                "width/height", "Dimensions must be 32 through 4096.");
        if (duration < 0)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (!float.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        if (textureCount is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(textureCount));

        return new(baseUrl, tokenPath, interval, width, height,
            duration, opacity, sharedMemory, textureCount, help);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            QuickPlayer shader texture latency test

            Options:
              --interval-ms <ms>  Target interval (default 10)
              --width <pixels>    Texture width (default 1024)
              --height <pixels>   Texture height (default 512)
              --textures <count>  Named textures texture1...N (default 3)
              --duration <sec>    Stop automatically; 0 means Ctrl+C
              --opacity <0..1>    u_QuickPlayerData.x (default 1)
              --shared-memory     Use the shared-memory texture channel
              --base-url <url>    QuickPlayer API v1 base URL
              --token <path>      API token file
              -h, --help          Show this help
            """);
    }
}
