using System.Text;

namespace IStripperQuickPlayer;

internal sealed record DesktopBridgeSnapshot(ulong Sequence, ulong Instance,
    ulong CompletedInstance, string Path, int State, int Elapsed, int Duration,
    int Decoder, ulong Window, Rectangle Bounds, uint Dpi, bool Moving,
    string Monitor, int SeekReady, int ReadinessMask, ulong Request)
{
    internal const int PacketSize = 2208;
    internal static byte[] CreatePacket(int size)
    {
        byte[] packet = new byte[size];
        BitConverter.GetBytes((uint)size).CopyTo(packet, 0);
        BitConverter.GetBytes(1u).CopyTo(packet, 4);
        return packet;
    }
    internal static DesktopBridgeSnapshot Parse(byte[] packet)
    {
        if (packet.Length != PacketSize || BitConverter.ToUInt32(packet, 0) != PacketSize ||
            BitConverter.ToUInt32(packet, 4) != 1) throw new InvalidDataException("Invalid desktop snapshot.");
        string Text(int offset, int capacity) => Encoding.Unicode.GetString(packet,
            offset, capacity * 2).Split('\0')[0];
        return new(BitConverter.ToUInt64(packet, 8), BitConverter.ToUInt64(packet, 16),
            BitConverter.ToUInt64(packet, 24), Text(152, 1024),
            BitConverter.ToInt32(packet, 32), BitConverter.ToInt32(packet, 36),
            BitConverter.ToInt32(packet, 40), BitConverter.ToInt32(packet, 44),
            BitConverter.ToUInt64(packet, 56), Rectangle.FromLTRB(
                BitConverter.ToInt32(packet, 64), BitConverter.ToInt32(packet, 68),
                BitConverter.ToInt32(packet, 72), BitConverter.ToInt32(packet, 76)),
            BitConverter.ToUInt32(packet, 80), BitConverter.ToUInt32(packet, 84) != 0,
            Text(88, 32), BitConverter.ToInt32(packet, 48), BitConverter.ToInt32(packet, 52),
            BitConverter.ToUInt64(packet, 2200));
    }
    internal static byte[] Selection(ulong instance, long request, string path)
    {
        if (path.Length >= 1024 || path.Contains('\0')) throw new ArgumentOutOfRangeException(nameof(path));
        byte[] packet = CreatePacket(2072);
        BitConverter.GetBytes(instance).CopyTo(packet, 8);
        BitConverter.GetBytes(request).CopyTo(packet, 16);
        Encoding.Unicode.GetBytes(path).CopyTo(packet, 24);
        return packet;
    }
}
