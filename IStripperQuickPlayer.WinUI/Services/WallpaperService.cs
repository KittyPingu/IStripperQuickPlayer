using System.Runtime.InteropServices;
using IStripperQuickPlayer.WinUI.Core;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class WallpaperService
{
    private const int SpiSetDeskWallpaper = 0x0014;
    private const int SpiGetDeskWallpaper = 0x0073;
    private const int SpifUpdateIniFile = 0x01;
    private const int SpifSendWinIniChange = 0x02;

    private readonly IstripperPaths _paths;
    private readonly CardPhotosService _photosService = new();
    private readonly HttpClient _httpClient = new();
    private string? _originalWallpaperPath;
    private bool? _originalDesktopIconsVisible;
    private bool _changedWallpaper;

    public WallpaperService(IstripperPaths paths)
    {
        _paths = paths;
    }

    public async Task SetSelectedCardWallpaperAsync(ModelCard card, AppSettings settings, CancellationToken cancellationToken = default)
    {
        CaptureOriginalDesktopState();

        string? wallpaperPath = await DownloadRandomWidescreenPhotoAsync(card.Name, cancellationToken);
        wallpaperPath ??= card.ImagePath;

        if (string.IsNullOrWhiteSpace(wallpaperPath) || !File.Exists(wallpaperPath))
        {
            throw new FileNotFoundException("No wallpaper image is available for the selected card.");
        }

        string processedPath = ProcessWallpaper(wallpaperPath, card, settings);
        if (settings.HideDesktopIcons && DesktopIconsVisible())
        {
            ToggleDesktopIcons();
        }

        SystemParametersInfo(SpiSetDeskWallpaper, 0, processedPath, SpifUpdateIniFile | SpifSendWinIniChange);
        _changedWallpaper = true;
    }

    public void RestoreOriginalDesktopState()
    {
        if (_changedWallpaper && !string.IsNullOrWhiteSpace(_originalWallpaperPath) && File.Exists(_originalWallpaperPath))
        {
            SystemParametersInfo(SpiSetDeskWallpaper, 0, _originalWallpaperPath, SpifUpdateIniFile | SpifSendWinIniChange);
        }

        if (_originalDesktopIconsVisible is { } originalIconsVisible && DesktopIconsVisible() != originalIconsVisible)
        {
            ToggleDesktopIcons();
        }
    }

    private void CaptureOriginalDesktopState()
    {
        _originalWallpaperPath ??= GetCurrentWallpaperPath();
        _originalDesktopIconsVisible ??= DesktopIconsVisible();
    }

    private static string GetCurrentWallpaperPath()
    {
        System.Text.StringBuilder path = new(1024);
        return SystemParametersInfo(SpiGetDeskWallpaper, path.Capacity, path, 0) ? path.ToString() : string.Empty;
    }

    private async Task<string?> DownloadRandomWidescreenPhotoAsync(string cardId, CancellationToken cancellationToken)
    {
        IReadOnlyList<PhotoViewItem> photos = await _photosService.LoadPhotosAsync(cardId, cancellationToken);
        if (photos.Count == 0)
        {
            return null;
        }

        PhotoViewItem? photo = photos
            .Where(p => p.Width > p.Height)
            .OrderBy(_ => Random.Shared.Next())
            .FirstOrDefault()
            ?? photos.OrderBy(_ => Random.Shared.Next()).FirstOrDefault();

        if (photo == null)
        {
            return null;
        }

        string extension = Path.GetExtension(photo.FullUrl.Split('?')[0]);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        string destination = Path.Combine(_paths.AppDataFolder, $"wallpaper-{cardId}{extension}");
        byte[] imageBytes = await _httpClient.GetByteArrayAsync(photo.FullUrl, cancellationToken);
        await File.WriteAllBytesAsync(destination, imageBytes, cancellationToken);
        return destination;
    }

    private string ProcessWallpaper(string sourcePath, ModelCard card, AppSettings settings)
    {
        bool needsProcessing = settings.WallpaperBrightness != 100 || settings.WallpaperDetails || settings.BlurWallpaper;
        if (!needsProcessing)
        {
            return sourcePath;
        }

        using System.Drawing.Bitmap source = new(sourcePath);
        using System.Drawing.Bitmap processed = new(source.Width, source.Height);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(processed);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (settings.BlurWallpaper)
        {
            using System.Drawing.Bitmap small = new(source, Math.Max(1, source.Width / 16), Math.Max(1, source.Height / 16));
            graphics.DrawImage(small, new System.Drawing.Rectangle(0, 0, source.Width, source.Height));
        }
        else if (settings.WallpaperBrightness != 100)
        {
            float brightness = (float)(settings.WallpaperBrightness / 100.0);
            using ImageAttributes attributes = new();
            attributes.SetColorMatrix(new ColorMatrix
            {
                Matrix00 = brightness,
                Matrix11 = brightness,
                Matrix22 = brightness,
                Matrix33 = 1,
                Matrix44 = 1
            });
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, System.Drawing.GraphicsUnit.Pixel, attributes);
        }
        else
        {
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, source.Width, source.Height));
        }

        if (settings.WallpaperDetails)
        {
            string details = $"{card.ModelName} - {card.Outfit}\nRating: {(card.Rating - 5m):0.##}  Res: {card.Resolution.GetDescription()}";
            using System.Drawing.Font font = new("Segoe UI", Math.Max(18, source.Width / 60), System.Drawing.FontStyle.Bold);
            using System.Drawing.SolidBrush shadow = new(System.Drawing.Color.FromArgb(190, 0, 0, 0));
            using System.Drawing.SolidBrush text = new(System.Drawing.Color.White);
            System.Drawing.PointF point = new(42, source.Height - (font.Size * 3.5f));
            graphics.DrawString(details, font, shadow, point.X + 3, point.Y + 3);
            graphics.DrawString(details, font, text, point);
        }

        string destination = Path.Combine(_paths.AppDataFolder, $"wallpaper-processed-{card.Name}.jpg");
        processed.Save(destination, ImageFormat.Jpeg);
        return destination;
    }

    private static bool DesktopIconsVisible()
    {
        nint shellView = GetDesktopShellView();
        if (shellView == 0)
        {
            return true;
        }

        nint child = GetWindow(shellView, 5);
        WINDOWINFO info = new() { cbSize = (uint)Marshal.SizeOf<WINDOWINFO>() };
        GetWindowInfo(child, ref info);
        return (info.dwStyle & 0x10000000) == 0x10000000;
    }

    private static void ToggleDesktopIcons()
    {
        nint shellView = GetDesktopShellView();
        if (shellView != 0)
        {
            SendMessage(shellView, 0x111, new nint(0x7402), 0);
        }
    }

    private static nint GetDesktopShellView()
    {
        nint progman = FindWindow("Progman", null);
        nint shellView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (shellView != 0)
        {
            return shellView;
        }

        nint desktop = GetDesktopWindow();
        nint worker = 0;
        do
        {
            worker = FindWindowEx(desktop, worker, "WorkerW", null);
            shellView = FindWindowEx(worker, 0, "SHELLDLL_DefView", null);
        }
        while (shellView == 0 && worker != 0);

        return shellView;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int action, int uParam, string vParam, int winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int action, int uParam, System.Text.StringBuilder vParam, int winIni);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint FindWindowEx(nint parentHandle, nint childAfter, string className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowInfo(nint hwnd, ref WINDOWINFO pwi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWINFO
    {
        public uint cbSize;
        public System.Drawing.Rectangle rcWindow;
        public System.Drawing.Rectangle rcClient;
        public uint dwStyle;
        public uint dwExStyle;
        public uint dwWindowStatus;
        public uint cxWindowBorders;
        public uint cyWindowBorders;
        public ushort atomWindowType;
        public ushort wCreatorVersion;
    }
}
