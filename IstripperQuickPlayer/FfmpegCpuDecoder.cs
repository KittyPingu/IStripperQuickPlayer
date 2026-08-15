using FFmpeg.AutoGen;

namespace IStripperQuickPlayer;

internal sealed unsafe class FfmpegCpuDecoder : IDisposable
{
    internal static bool VerifyRuntime()
    {
        ffmpeg.RootPath = AppContext.BaseDirectory;
        return ffmpeg.avcodec_version() >> 16 == 62;
    }

    readonly AVFormatContext* formatContext;
    readonly AVCodecContext* codecContext;
    readonly AVStream* stream;
    readonly AVFrame* frame;
    readonly AVPacket* packet;
    readonly int streamIndex;
    readonly double timeBase;
    readonly double timestampOrigin;
    readonly double frameDuration;
    bool sentEndOfStream;
    double discardBefore;
    bool disposed;

    internal int Width { get; }
    internal int Height { get; }
    internal double Duration { get; }
    internal double FrameDuration => frameDuration;
    internal string FrameRate { get; }
    internal bool Ended { get; private set; }

    internal FfmpegCpuDecoder(
        string path, bool fastDecode = true)
    {
        ffmpeg.RootPath = AppContext.BaseDirectory;
        uint codecVersion = ffmpeg.avcodec_version();
        if (codecVersion >> 16 != 62)
            throw new NotSupportedException(
                $"FFmpeg libavcodec 62 is required; found " +
                $"{codecVersion >> 16}.{(codecVersion >> 8) & 255}." +
                $"{codecVersion & 255}.");

        AVFormatContext* openedFormat = null;
        AVCodecContext* openedCodec = null;
        AVFrame* openedFrame = null;
        AVPacket* openedPacket = null;
        try
        {
            Check(ffmpeg.avformat_open_input(
                &openedFormat, path, null, null), "open video");
            Check(ffmpeg.avformat_find_stream_info(
                openedFormat, null), "read video stream information");

            AVCodec* codec = null;
            int foundStream = ffmpeg.av_find_best_stream(
                openedFormat, AVMediaType.AVMEDIA_TYPE_VIDEO,
                -1, -1, &codec, 0);
            Check(foundStream, "find video stream");
            streamIndex = foundStream;
            stream = openedFormat->streams[streamIndex];

            openedCodec = ffmpeg.avcodec_alloc_context3(codec);
            if (openedCodec == null)
                throw new OutOfMemoryException(
                    "Could not allocate the FFmpeg decoder.");
            Check(ffmpeg.avcodec_parameters_to_context(
                openedCodec, stream->codecpar),
                "configure video decoder");
            // Let FFmpeg select the codec's optimal frame/slice threading.
            // A forced single thread cannot sustain real-time playback for a
            // 4K H.264 foreground paired with a full-resolution alpha stream.
            openedCodec->thread_count = 0;
            if (fastDecode)
                openedCodec->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            Check(ffmpeg.avcodec_open2(
                openedCodec, codec, null), "open video decoder");

            openedFrame = ffmpeg.av_frame_alloc();
            openedPacket = ffmpeg.av_packet_alloc();
            if (openedFrame == null || openedPacket == null)
                throw new OutOfMemoryException(
                    "Could not allocate FFmpeg video buffers.");

            formatContext = openedFormat;
            codecContext = openedCodec;
            frame = openedFrame;
            packet = openedPacket;
            Width = openedCodec->width;
            Height = openedCodec->height;
            if (Width <= 0 || Height <= 0)
                throw new InvalidDataException(
                    "FFmpeg returned an invalid video size.");

            timeBase = ffmpeg.av_q2d(stream->time_base);
            timestampOrigin =
                stream->start_time == ffmpeg.AV_NOPTS_VALUE
                    ? 0 : stream->start_time * timeBase;
            AVRational rate = ffmpeg.av_guess_frame_rate(
                openedFormat, stream, null);
            FrameRate = rate.num > 0 && rate.den > 0
                ? $"{rate.num}/{rate.den}" : "25/1";
            double framesPerSecond = rate.num > 0 && rate.den > 0
                ? ffmpeg.av_q2d(rate) : 25;
            frameDuration = 1 / Math.Max(1, framesPerSecond);
            Duration = stream->duration != ffmpeg.AV_NOPTS_VALUE &&
                stream->duration > 0
                    ? stream->duration * timeBase
                    : openedFormat->duration > 0
                        ? openedFormat->duration /
                            (double)ffmpeg.AV_TIME_BASE
                        : 0;
            if (!double.IsFinite(Duration) || Duration <= 0)
                throw new InvalidDataException(
                    "FFmpeg returned an invalid video duration.");
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

    internal bool DecodeNext(out long presentationTime)
    {
        presentationTime = 0;
        while (!Ended)
        {
            ffmpeg.av_frame_unref(frame);
            int receive = ffmpeg.avcodec_receive_frame(
                codecContext, frame);
            if (receive == 0)
            {
                long timestamp = frame->best_effort_timestamp;
                double seconds = timestamp == ffmpeg.AV_NOPTS_VALUE
                    ? 0 : timestamp * timeBase - timestampOrigin;
                if (seconds + frameDuration / 2 < discardBefore)
                    continue;
                discardBefore = 0;
                presentationTime = (long)Math.Round(
                    Math.Max(0, seconds) * 10_000_000);
                return true;
            }
            if (receive != ffmpeg.AVERROR(ffmpeg.EAGAIN) &&
                receive != ffmpeg.AVERROR_EOF)
                Check(receive, "decode video frame");
            if (receive == ffmpeg.AVERROR_EOF)
            {
                Ended = true;
                return false;
            }

            bool packetSent = false;
            while (!packetSent)
            {
                ffmpeg.av_packet_unref(packet);
                int read = ffmpeg.av_read_frame(
                    formatContext, packet);
                if (read == ffmpeg.AVERROR_EOF)
                {
                    if (!sentEndOfStream)
                    {
                        Check(ffmpeg.avcodec_send_packet(
                            codecContext, null),
                            "flush video decoder");
                        sentEndOfStream = true;
                    }
                    packetSent = true;
                    continue;
                }
                Check(read, "read video packet");
                if (packet->stream_index != streamIndex)
                    continue;
                int send = ffmpeg.avcodec_send_packet(
                    codecContext, packet);
                if (send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Check(send, "send video packet");
                packetSent = true;
            }
        }
        return false;
    }

    internal void Seek(double seconds)
    {
        long timestamp = (long)Math.Round(
            (Math.Clamp(seconds, 0, Duration) + timestampOrigin) /
                timeBase);
        Check(ffmpeg.av_seek_frame(
            formatContext, streamIndex, timestamp,
            ffmpeg.AVSEEK_FLAG_BACKWARD), "seek video");
        ffmpeg.avcodec_flush_buffers(codecContext);
        ffmpeg.av_packet_unref(packet);
        ffmpeg.av_frame_unref(frame);
        sentEndOfStream = false;
        Ended = false;
        discardBefore = Math.Clamp(seconds, 0, Duration);
    }

    internal AVPixelFormat PixelFormat =>
        (AVPixelFormat)frame->format;

    internal bool FullRange =>
        frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ||
        PixelFormat == AVPixelFormat.AV_PIX_FMT_YUVJ420P;

    internal bool Bt709 =>
        frame->colorspace == AVColorSpace.AVCOL_SPC_BT709 ||
        frame->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED &&
        Width >= 1280;

    internal (IntPtr Data, uint RowPitch) Plane(int index)
    {
        if (index is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(index));
        uint plane = (uint)index;
        if (frame->data[plane] == null ||
            frame->linesize[plane] <= 0)
            throw new InvalidDataException(
                "FFmpeg returned an invalid YUV video plane.");
        return ((IntPtr)frame->data[plane],
            checked((uint)frame->linesize[plane]));
    }

    internal void ValidateGpuFrame()
    {
        if (PixelFormat is not AVPixelFormat.AV_PIX_FMT_YUV420P and
            not AVPixelFormat.AV_PIX_FMT_YUVJ420P)
            throw new NotSupportedException(
                $"GPU playback does not yet support FFmpeg pixel format " +
                $"{PixelFormat}.");
        _ = Plane(0);
        _ = Plane(1);
        _ = Plane(2);
    }

    internal (IntPtr Data, uint RowPitch) GrayPlane()
    {
        if (PixelFormat is not AVPixelFormat.AV_PIX_FMT_GRAY8 and
            not AVPixelFormat.AV_PIX_FMT_GRAY16LE and
            not AVPixelFormat.AV_PIX_FMT_YUV420P and
            not AVPixelFormat.AV_PIX_FMT_YUVJ420P)
            throw new NotSupportedException(
                $"Alpha playback requires GRAY8, GRAY16LE, or YUV420P; found {PixelFormat}.");
        if (frame->data[0] == null || frame->linesize[0] <= 0)
            throw new InvalidDataException("FFmpeg returned an invalid alpha plane.");
        return ((IntPtr)frame->data[0], checked((uint)frame->linesize[0]));
    }

    internal bool IsGray16 => PixelFormat == AVPixelFormat.AV_PIX_FMT_GRAY16LE ||
        codecContext->pix_fmt == AVPixelFormat.AV_PIX_FMT_GRAY16LE ||
        (AVPixelFormat)stream->codecpar->format == AVPixelFormat.AV_PIX_FMT_GRAY16LE;

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
        if (disposed)
            return;
        disposed = true;
        AVPacket* releasedPacket = packet;
        AVFrame* releasedFrame = frame;
        AVCodecContext* releasedCodec = codecContext;
        AVFormatContext* releasedFormat = formatContext;
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
