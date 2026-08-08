using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IStripperQuickPlayer;

internal sealed class InternalAudioPlayer : IDisposable
{
    readonly FfmpegAudioProvider provider;
    readonly WasapiOut output;
    bool started;
    bool mutedForRate;
    bool disposed;

    InternalAudioPlayer(string path)
    {
        provider = new FfmpegAudioProvider(path);
        try
        {
            output = new WasapiOut(
                AudioClientShareMode.Shared, true, 80);
            output.Init(provider);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    internal string Format => provider.WaveFormat.ToString();

    internal static InternalAudioPlayer? TryOpen(string path)
    {
        try
        {
            return new InternalAudioPlayer(path);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Internal audio unavailable: {error}");
            return null;
        }
    }

    internal static bool Verify(string path)
    {
        using var source = new FfmpegAudioProvider(path);
        byte[] second = new byte[
            source.WaveFormat.AverageBytesPerSecond];
        if (source.Read(second, 0, second.Length) == 0)
            return false;
        source.SetVolume(0);
        source.Seek(0.25);
        int muted = source.Read(second, 0, second.Length);
        return muted > 0 &&
            second.AsSpan(0, muted).IndexOfAnyExcept((byte)0) < 0 &&
            !MuteForRate(1) && MuteForRate(0.5);
    }

    internal void Play(double seconds)
    {
        if (disposed)
            return;
        if (!started)
        {
            provider.Seek(seconds);
            started = true;
        }
        if (!mutedForRate)
            output.Play();
    }

    internal void Pause()
    {
        if (!disposed)
            output.Pause();
    }

    internal void Seek(double seconds, bool play)
    {
        if (disposed)
            return;
        output.Stop();
        provider.Seek(seconds);
        started = true;
        if (play && !mutedForRate)
            output.Play();
    }

    internal void SetRate(
        double rate, double seconds, bool play)
    {
        if (disposed)
            return;
        output.Stop();
        mutedForRate = MuteForRate(rate);
        provider.Seek(seconds);
        started = true;
        if (play && !mutedForRate)
            output.Play();
    }

    internal void SetVolume(int percent)
    {
        if (!disposed)
            provider.SetVolume(percent);
    }

    static bool MuteForRate(double rate) =>
        Math.Abs(rate - 1) > 0.0001;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        output.Dispose();
        provider.Dispose();
    }

    sealed unsafe class FfmpegAudioProvider :
        IWaveProvider, IDisposable
    {
        const int OutputRate = 48000;
        const int OutputChannels = 2;
        const int BytesPerSample = 2;

        readonly object sync = new();
        readonly AVFormatContext* formatContext;
        readonly AVCodecContext* codecContext;
        readonly AVStream* stream;
        readonly AVFrame* frame;
        readonly AVPacket* packet;
        readonly int streamIndex;
        readonly double timeBase;
        readonly double timestampOrigin;
        SwrContext* resampler;
        byte[] converted = [];
        int convertedOffset;
        int convertedCount;
        bool packetPending;
        bool sentEndOfStream;
        bool decoderEnded;
        bool disposed;
        float volume = 1;

        internal FfmpegAudioProvider(string path)
        {
            ffmpeg.RootPath = AppContext.BaseDirectory;
            AVFormatContext* openedFormat = null;
            AVCodecContext* openedCodec = null;
            AVFrame* openedFrame = null;
            AVPacket* openedPacket = null;
            try
            {
                Check(ffmpeg.avformat_open_input(
                    &openedFormat, path, null, null), "open audio");
                Check(ffmpeg.avformat_find_stream_info(
                    openedFormat, null),
                    "read audio stream information");

                AVCodec* codec = null;
                int foundStream = ffmpeg.av_find_best_stream(
                    openedFormat, AVMediaType.AVMEDIA_TYPE_AUDIO,
                    -1, -1, &codec, 0);
                Check(foundStream, "find audio stream");
                streamIndex = foundStream;
                stream = openedFormat->streams[streamIndex];
                openedCodec = ffmpeg.avcodec_alloc_context3(codec);
                if (openedCodec == null)
                    throw new OutOfMemoryException(
                        "Could not allocate the FFmpeg audio decoder.");
                Check(ffmpeg.avcodec_parameters_to_context(
                    openedCodec, stream->codecpar),
                    "configure audio decoder");
                openedCodec->thread_count = 1;
                Check(ffmpeg.avcodec_open2(
                    openedCodec, codec, null), "open audio decoder");
                if (openedCodec->sample_rate <= 0 ||
                    openedCodec->ch_layout.nb_channels <= 0)
                    throw new InvalidDataException(
                        "FFmpeg returned an invalid audio format.");

                openedFrame = ffmpeg.av_frame_alloc();
                openedPacket = ffmpeg.av_packet_alloc();
                if (openedFrame == null || openedPacket == null)
                    throw new OutOfMemoryException(
                        "Could not allocate FFmpeg audio buffers.");

                formatContext = openedFormat;
                codecContext = openedCodec;
                frame = openedFrame;
                packet = openedPacket;
                timeBase = ffmpeg.av_q2d(stream->time_base);
                timestampOrigin =
                    stream->start_time == ffmpeg.AV_NOPTS_VALUE
                        ? 0 : stream->start_time * timeBase;
                RebuildResampler();
            }
            catch
            {
                if (openedPacket != null)
                    ffmpeg.av_packet_free(&openedPacket);
                if (openedFrame != null)
                    ffmpeg.av_frame_free(&openedFrame);
                if (openedCodec != null)
                    ffmpeg.avcodec_free_context(&openedCodec);
                if (openedFormat != null)
                    ffmpeg.avformat_close_input(&openedFormat);
                throw;
            }
        }

        public WaveFormat WaveFormat { get; } =
            new(OutputRate, 16, OutputChannels);

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (sync)
            {
                if (disposed)
                    return 0;
                int written = 0;
                while (written < count)
                {
                    if (convertedOffset < convertedCount)
                    {
                        int copy = Math.Min(
                            count - written,
                            convertedCount - convertedOffset);
                        Buffer.BlockCopy(
                            converted, convertedOffset,
                            buffer, offset + written, copy);
                        convertedOffset += copy;
                        written += copy;
                        continue;
                    }
                    if (!DecodeNextChunk())
                        break;
                }
                Span<short> samples =
                    System.Runtime.InteropServices.MemoryMarshal.Cast<
                        byte, short>(buffer.AsSpan(offset, written));
                for (int index = 0; index < samples.Length; index++)
                    samples[index] =
                        (short)(samples[index] * volume);
                return written;
            }
        }

        internal void SetVolume(int percent)
        {
            lock (sync)
                volume = Math.Clamp(percent, 0, 100) / 100f;
        }

        internal void Seek(double seconds)
        {
            lock (sync)
                SeekLocked(seconds);
        }

        void SeekLocked(double seconds)
        {
            long timestamp = (long)Math.Round(
                (Math.Max(0, seconds) + timestampOrigin) / timeBase);
            Check(ffmpeg.av_seek_frame(
                formatContext, streamIndex, timestamp,
                ffmpeg.AVSEEK_FLAG_BACKWARD), "seek audio");
            ffmpeg.avcodec_flush_buffers(codecContext);
            ffmpeg.av_packet_unref(packet);
            ffmpeg.av_frame_unref(frame);
            packetPending = false;
            sentEndOfStream = false;
            decoderEnded = false;
            convertedOffset = convertedCount = 0;
            RebuildResampler();
        }

        void RebuildResampler()
        {
            if (resampler != null)
            {
                SwrContext* released = resampler;
                ffmpeg.swr_free(&released);
                resampler = null;
            }
            AVChannelLayout outputLayout = default;
            AVChannelLayout inputLayout =
                codecContext->ch_layout;
            SwrContext* configured = null;
            ffmpeg.av_channel_layout_default(
                &outputLayout, OutputChannels);
            try
            {
                Check(ffmpeg.swr_alloc_set_opts2(
                    &configured,
                    &outputLayout,
                    AVSampleFormat.AV_SAMPLE_FMT_S16,
                    OutputRate,
                    &inputLayout,
                    codecContext->sample_fmt,
                    codecContext->sample_rate,
                    0, null), "configure audio resampler");
                Check(ffmpeg.swr_init(configured),
                    "initialize audio resampler");
                resampler = configured;
            }
            catch
            {
                if (configured != null)
                    ffmpeg.swr_free(&configured);
                throw;
            }
            finally
            {
                ffmpeg.av_channel_layout_uninit(&outputLayout);
            }
        }

        bool DecodeNextChunk()
        {
            while (!decoderEnded)
            {
                ffmpeg.av_frame_unref(frame);
                int receive = ffmpeg.avcodec_receive_frame(
                    codecContext, frame);
                if (receive == 0)
                {
                    if (ConvertFrame(frame->extended_data,
                            frame->nb_samples))
                        return true;
                    continue;
                }
                if (receive == ffmpeg.AVERROR_EOF)
                {
                    decoderEnded = true;
                    return ConvertFrame(null, 0);
                }
                if (receive != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Check(receive, "decode audio frame");
                SendNextPacket();
            }
            return ConvertFrame(null, 0);
        }

        void SendNextPacket()
        {
            while (true)
            {
                if (!packetPending)
                {
                    ffmpeg.av_packet_unref(packet);
                    int read = ffmpeg.av_read_frame(
                        formatContext, packet);
                    if (read == ffmpeg.AVERROR_EOF)
                    {
                        if (!sentEndOfStream)
                        {
                            int flush = ffmpeg.avcodec_send_packet(
                                codecContext, null);
                            if (flush != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            {
                                Check(flush, "flush audio decoder");
                                sentEndOfStream = true;
                            }
                        }
                        return;
                    }
                    Check(read, "read audio packet");
                    if (packet->stream_index != streamIndex)
                        continue;
                    packetPending = true;
                }

                int send = ffmpeg.avcodec_send_packet(
                    codecContext, packet);
                if (send == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    return;
                Check(send, "send audio packet");
                ffmpeg.av_packet_unref(packet);
                packetPending = false;
                return;
            }
        }

        bool ConvertFrame(byte** input, int inputSamples)
        {
            int capacity = Math.Max(1,
                ffmpeg.swr_get_out_samples(
                    resampler, inputSamples));
            int byteCount = checked(
                capacity * OutputChannels * BytesPerSample);
            if (converted.Length < byteCount)
                converted = new byte[byteCount];
            fixed (byte* outputBuffer = converted)
            {
                byte* output = outputBuffer;
                int samples = ffmpeg.swr_convert(
                    resampler, &output, capacity,
                    input, inputSamples);
                Check(samples, "resample audio");
                convertedOffset = 0;
                convertedCount = checked(
                    samples * OutputChannels * BytesPerSample);
                return convertedCount > 0;
            }
        }

        static void Check(int result, string operation)
        {
            if (result >= 0)
                return;
            byte* buffer = stackalloc byte[1024];
            ffmpeg.av_strerror(result, buffer, 1024);
            string message = new((sbyte*)buffer);
            throw new InvalidOperationException(
                $"FFmpeg could not {operation}: {message}");
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                SwrContext* releasedResampler = resampler;
                AVPacket* releasedPacket = packet;
                AVFrame* releasedFrame = frame;
                AVCodecContext* releasedCodec = codecContext;
                AVFormatContext* releasedFormat = formatContext;
                if (releasedResampler != null)
                    ffmpeg.swr_free(&releasedResampler);
                if (releasedPacket != null)
                    ffmpeg.av_packet_free(&releasedPacket);
                if (releasedFrame != null)
                    ffmpeg.av_frame_free(&releasedFrame);
                if (releasedCodec != null)
                    ffmpeg.avcodec_free_context(&releasedCodec);
                if (releasedFormat != null)
                    ffmpeg.avformat_close_input(&releasedFormat);
            }
        }
    }
}
