using System.Text;

namespace IStripperQuickPlayer.WinUI.Services;

public static class PlaylistLoader
{
    public static List<string> LoadPlaylist(string filename)
    {
        List<string> playlist = [];
        using FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using BinaryReader reader = new(stream, Encoding.UTF8, false);

        ReadInt32(reader);
        int number = ReadInt32(reader);
        for (int i = 0; i < number; i++)
        {
            int length = ReadInt32(reader);
            playlist.Add(ReadStringUnicode(reader, length));
        }

        return playlist;
    }

    private static int ReadInt32(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3];
    }

    private static string ReadStringUnicode(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        return Encoding.Default.GetString(bytes.Where((_, index) => index % 2 == 1).ToArray());
    }
}
