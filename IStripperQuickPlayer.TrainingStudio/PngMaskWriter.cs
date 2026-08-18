using System.IO.Compression;
using System.Text;

namespace IStripperQuickPlayer.TrainingStudio;

internal static class PngMaskWriter
{
    static readonly uint[] CrcTable = BuildCrcTable();

    internal static void SaveGray8(string path, int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Invalid mask size.");
        byte[] raw = new byte[height * (width + 1)];
        for (int y = 0; y < height; y++) pixels.Slice(y * width, width).CopyTo(raw.AsSpan(y * (width + 1) + 1));
        Save(path, width, height, 8, raw);
    }

    internal static void SaveGray16(string path, int width, int height, ReadOnlySpan<int> pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Invalid mask size.");
        byte[] raw = new byte[height * (width * 2 + 1)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                ushort value = checked((ushort)Math.Clamp(pixels[y * width + x], 0, ushort.MaxValue));
                int offset = y * (width * 2 + 1) + 1 + x * 2;
                raw[offset] = (byte)(value >> 8);
                raw[offset + 1] = (byte)value;
            }
        Save(path, width, height, 16, raw);
    }

    static void Save(string path, int width, int height, byte depth, byte[] raw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (FileStream output = File.Create(temporary))
        {
            output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            byte[] header = new byte[13];
            WriteBigEndian(header, 0, width); WriteBigEndian(header, 4, height);
            header[8] = depth; header[9] = 0;
            WriteChunk(output, "IHDR", header);
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, true)) zlib.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
            WriteChunk(output, "IEND", []);
        }
        File.Move(temporary, path, true);
    }

    static void WriteChunk(Stream stream, string name, byte[] data)
    {
        byte[] type = Encoding.ASCII.GetBytes(name);
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, data.Length); stream.Write(length); stream.Write(type); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (byte value in type.Concat(data)) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        Span<byte> checksum = stackalloc byte[4];
        WriteBigEndian(checksum, 0, unchecked((int)(crc ^ 0xffffffff))); stream.Write(checksum);
    }

    static void WriteBigEndian(Span<byte> value, int offset, int number)
    {
        value[offset] = (byte)(number >> 24); value[offset + 1] = (byte)(number >> 16);
        value[offset + 2] = (byte)(number >> 8); value[offset + 3] = (byte)number;
    }

    static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
