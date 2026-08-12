using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace IStripperQuickPlayer;

internal static class CustomMaskArchive
{
    const int Version = 2;
    const int XorDeltaVersion = 1;
    const int ChunkFrames = 300;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("IQPMASK1");

    internal sealed record Info(int Width, int Height, int FrameCount,
        int ChunkFrameCount);

    internal static Info Create(string framesFolder, string destination)
    {
        string[] paths = Directory.EnumerateFiles(framesFolder, "*.png")
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0) throw new InvalidDataException(
            "The tracked mask sequence contains no PNG frames.");
        using Bitmap first = new(paths[0]);
        int width = first.Width, height = first.Height;
        int packedLength = checked((width * height + 7) / 8);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            {
                using FileStream stream = new(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(Magic); writer.Write(Version);
                writer.Write(width); writer.Write(height); writer.Write(paths.Length);
                writer.Write(ChunkFrames);
                using Compressor compressor = new(10);
                for (int start = 0; start < paths.Length; start += ChunkFrames)
                {
                    int count = Math.Min(ChunkFrames, paths.Length - start);
                    byte[] raw = new byte[checked(count * packedLength)];
                    for (int offset = 0; offset < count; offset++)
                    {
                        using Bitmap mask = new(paths[start + offset]);
                        if (mask.Width != width || mask.Height != height)
                            throw new InvalidDataException(
                                "Tracked masks must all have the same dimensions.");
                        byte[] packed = Pack(mask);
                        Span<byte> target = raw.AsSpan(offset * packedLength, packedLength);
                        packed.CopyTo(target);
                    }
                    byte[] compressed = compressor.Wrap(raw).ToArray();
                    writer.Write(start); writer.Write(count); writer.Write(raw.Length);
                    writer.Write(compressed.Length);
                    writer.Write(SHA256.HashData(raw));
                    writer.Write(compressed);
                }
                writer.Flush(); stream.Flush(true);
            }
            Validate(temporary);
            File.Move(temporary, destination, true);
            return new(width, height, paths.Length, ChunkFrames);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    internal static Info Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        using FileStream stream = File.OpenRead(archive);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        (Info info, bool xorDeltas) = ReadHeader(reader);
        int packedLength = checked((info.Width * info.Height + 7) / 8);
        using Decompressor decompressor = new();
        int expectedStart = 0;
        while (expectedStart < info.FrameCount)
        {
            (int start, int count, byte[] raw) = ReadChunk(reader, decompressor,
                packedLength, info.FrameCount, expectedStart);
            byte[] previous = new byte[packedLength];
            for (int offset = 0; offset < count; offset++)
            {
                Span<byte> packed = raw.AsSpan(offset * packedLength, packedLength);
                if (xorDeltas && offset > 0)
                    for (int index = 0; index < packedLength; index++)
                        packed[index] ^= previous[index];
                packed.CopyTo(previous);
                SaveMask(packed, info.Width, info.Height, Path.Combine(destination,
                    $"{start + offset + 1:00000000}.png"));
            }
            expectedStart += count;
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("The mask archive has trailing data.");
        return info;
    }

    internal static Info Validate(string archive)
    {
        using FileStream stream = File.OpenRead(archive);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        (Info info, _) = ReadHeader(reader);
        int packedLength = checked((info.Width * info.Height + 7) / 8);
        using Decompressor decompressor = new();
        int expectedStart = 0;
        while (expectedStart < info.FrameCount)
        {
            (_, int count, _) = ReadChunk(reader, decompressor, packedLength,
                info.FrameCount, expectedStart);
            expectedStart += count;
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("The mask archive has trailing data.");
        return info;
    }

    static (Info Info, bool XorDeltas) ReadHeader(BinaryReader reader)
    {
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Unknown retained-mask archive format.");
        int version = reader.ReadInt32();
        if (version is not (XorDeltaVersion or Version))
            throw new InvalidDataException("Unknown retained-mask archive format.");
        int width = reader.ReadInt32(), height = reader.ReadInt32();
        int frames = reader.ReadInt32(), chunkFrames = reader.ReadInt32();
        if (width <= 0 || height <= 0 || frames <= 0 || chunkFrames <= 0 ||
            width > 16384 || height > 16384 || frames > 10_000_000)
            throw new InvalidDataException("Invalid retained-mask archive dimensions.");
        return (new(width, height, frames, chunkFrames),
            version == XorDeltaVersion);
    }

    static (int Start, int Count, byte[] Raw) ReadChunk(BinaryReader reader,
        Decompressor decompressor, int packedLength, int frameCount,
        int expectedStart)
    {
        int start = reader.ReadInt32(), count = reader.ReadInt32();
        int rawLength = reader.ReadInt32(), compressedLength = reader.ReadInt32();
        if (start != expectedStart || count <= 0 || start + count > frameCount ||
            rawLength != checked(count * packedLength) || compressedLength <= 0 ||
            compressedLength > 1_000_000_000)
            throw new InvalidDataException("Invalid retained-mask archive chunk.");
        byte[] expectedHash = reader.ReadBytes(32);
        byte[] compressed = reader.ReadBytes(compressedLength);
        if (expectedHash.Length != 32 || compressed.Length != compressedLength)
            throw new EndOfStreamException("The retained-mask archive is truncated.");
        byte[] raw = decompressor.Unwrap(compressed).ToArray();
        if (raw.Length != rawLength ||
            !CryptographicOperations.FixedTimeEquals(expectedHash,
                SHA256.HashData(raw)))
            throw new InvalidDataException("A retained-mask archive chunk is corrupt.");
        return (start, count, raw);
    }

    static unsafe byte[] Pack(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        byte[] packed = new byte[checked((width * height + 7) / 8)];
        Rectangle bounds = new(0, 0, width, height);
        PixelFormat format = bitmap.PixelFormat;
        int bits = Image.GetPixelFormatSize(format);
        if (bits is not (1 or 8 or 24 or 32))
        {
            using Bitmap converted = bitmap.Clone(bounds, PixelFormat.Format32bppArgb);
            return Pack(converted);
        }
        Color[]? palette = (format & PixelFormat.Indexed) != 0
            ? bitmap.Palette.Entries : null;
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, format);
        try
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < width; x++)
                {
                    bool foreground = bits switch
                    {
                        1 => palette![(row[x >> 3] >> (7 - (x & 7))) & 1].R >= 128,
                        8 => palette![row[x]].R >= 128,
                        24 => row[x * 3] >= 128,
                        _ => row[x * 4] >= 128
                    };
                    if (foreground)
                    {
                        int pixel = y * width + x;
                        packed[pixel >> 3] |= (byte)(1 << (7 - (pixel & 7)));
                    }
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return packed;
    }

    static unsafe void SaveMask(ReadOnlySpan<byte> packed, int width, int height,
        string path)
    {
        using Bitmap bitmap = new(width, height, PixelFormat.Format8bppIndexed);
        ColorPalette palette = bitmap.Palette;
        for (int index = 0; index < palette.Entries.Length; index++)
            palette.Entries[index] = Color.FromArgb(index, index, index);
        bitmap.Palette = palette;
        Rectangle bounds = new(0, 0, width, height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly,
            PixelFormat.Format8bppIndexed);
        try
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < width; x++)
                {
                    int pixel = y * width + x;
                    row[x] = (packed[pixel >> 3] &
                        (1 << (7 - (pixel & 7)))) != 0 ? (byte)255 : (byte)0;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(path, ImageFormat.Png);
    }

    internal static bool VerifyRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "iqp-mask-archive-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(root, "source");
            string restored = Path.Combine(root, "restored");
            Directory.CreateDirectory(source);
            for (int frame = 0; frame < 17; frame++)
            {
                using Bitmap mask = new(19, 11, PixelFormat.Format8bppIndexed);
                ColorPalette palette = mask.Palette;
                palette.Entries[0] = Color.Black; palette.Entries[1] = Color.White;
                mask.Palette = palette;
                BitmapData data = mask.LockBits(new Rectangle(0, 0, 19, 11),
                    ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                try
                {
                    byte[] row = new byte[Math.Abs(data.Stride)];
                    for (int y = 0; y < 11; y++)
                    {
                        Array.Clear(row);
                        for (int x = 0; x < 19; x++)
                            if ((x + frame) % 7 < 3 && y is > 1 and < 9) row[x] = 1;
                        Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                    }
                }
                finally { mask.UnlockBits(data); }
                mask.Save(Path.Combine(source, $"{frame + 1:00000000}.png"),
                    ImageFormat.Png);
            }
            string archive = Path.Combine(root, "masks.iqpmask");
            Info created = Create(source, archive);
            Info extracted = Extract(archive, restored);
            if (created != extracted || extracted.FrameCount != 17)
            {
                Console.Error.WriteLine("Mask archive metadata round trip failed.");
                return false;
            }
            string[] original = Directory.GetFiles(source, "*.png");
            string[] copies = Directory.GetFiles(restored, "*.png");
            for (int index = 0; index < original.Length; index++)
                using (Bitmap before = new(original[index]))
                using (Bitmap after = new(copies[index]))
                    if (!Pack(before).SequenceEqual(Pack(after)))
                    {
                        Console.Error.WriteLine($"Mask archive frame {index} differs.");
                        return false;
                    }
            if (new FileInfo(archive).Length >= original.Sum(path =>
                new FileInfo(path).Length))
            {
                Console.Error.WriteLine("Mask archive did not compress its fixture.");
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            Console.Error.WriteLine("Mask archive verification failed: " + error);
            return false;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
