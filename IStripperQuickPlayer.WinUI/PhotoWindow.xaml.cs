using IStripperQuickPlayer.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IStripperQuickPlayer.WinUI;

public sealed partial class PhotoWindow : Window
{
    private readonly string _cardId;
    private readonly CardPhotosService _photosService = new();

    public PhotoWindow(string cardId)
    {
        _cardId = cardId;
        InitializeComponent();
        AppWindow.Title = $"Photos - {_cardId}";
        Activated += PhotoWindow_Activated;
    }

    private async void PhotoWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= PhotoWindow_Activated;
        try
        {
            IReadOnlyList<PhotoViewItem> photos = await _photosService.LoadPhotosAsync(_cardId);
            PhotosGrid.ItemsSource = photos;
            StatusText.Text = $"{photos.Count} photos";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void PhotosGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PhotoViewItem photo)
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = photo.FullUrl,
            UseShellExecute = true
        });
    }
}
