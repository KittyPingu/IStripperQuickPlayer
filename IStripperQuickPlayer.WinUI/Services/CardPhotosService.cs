using System.Text.Json;
using IStripperQuickPlayer.WinUI.Core;
using Microsoft.Win32;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class CardPhotosService
{
    private readonly HttpClient _httpClient = new();

    public async Task<IReadOnlyList<PhotoViewItem>> LoadPhotosAsync(string cardId, CancellationToken cancellationToken = default)
    {
        string baseCardId = cardId.Split('-').First();
        string url = $"https://www.istripper.com/free/sets/{baseCardId}/photos/photos.json";
        string json = await _httpClient.GetStringAsync(url, cancellationToken);
        RootPhotos? data = JsonSerializer.Deserialize<RootPhotos>(json);
        if (data?.Photos == null)
        {
            return [];
        }

        return data.Photos.Select(CreatePhotoViewItem).ToList();
    }

    private PhotoViewItem CreatePhotoViewItem(Photo photo)
    {
        string thumbnailUrl = "http://www.istripper.com/" + photo.Files.Mini;
        string fullUrl;
        if (photo.Access == "public")
        {
            fullUrl = "http://www.istripper.com/" + photo.Files.Full;
        }
        else
        {
            string userKey = GetDlmValue("key");
            string username = GetDlmValue("username");
            fullUrl = "http://www.istripper.com" + photo.Files.Full
                + "?filename=" + photo.Name
                + "&private=yes&ui=" + username
                + "&uk=" + userKey
                + "&explicit=1&language=en";
        }

        return new PhotoViewItem(photo.Name, thumbnailUrl, fullUrl, photo.Size.Width, photo.Size.Height);
    }

    private static string GetDlmValue(string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\DLM", false);
        return key?.GetValue(valueName, string.Empty)?.ToString() ?? string.Empty;
    }
}

public sealed class PhotoViewItem
{
    public PhotoViewItem(string name, string thumbnailUrl, string fullUrl, int width, int height)
    {
        Name = name;
        ThumbnailUrl = thumbnailUrl;
        FullUrl = fullUrl;
        Width = width;
        Height = height;
    }

    public string Name { get; }

    public string ThumbnailUrl { get; }

    public string FullUrl { get; }

    public int Width { get; }

    public int Height { get; }
}
