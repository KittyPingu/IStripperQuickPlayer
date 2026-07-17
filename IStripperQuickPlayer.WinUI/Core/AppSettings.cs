namespace IStripperQuickPlayer.WinUI.Core;

public sealed class AppSettings
{
    public string SortBy { get; set; } = "Model Name";

    public string SortDirection { get; set; } = "Ascending";

    public bool FavouritesFilter { get; set; }

    public bool ShowRatingStars { get; set; } = true;

    public bool ShowDescInSearch { get; set; } = true;

    public bool ShowOutfitInSearch { get; set; } = true;

    public bool EnforceCardFilter { get; set; } = true;

    public bool Randomize { get; set; } = true;

    public bool NextClipEnabled { get; set; } = true;

    public string NextClipString { get; set; } = "Control+Alt+N";

    public bool NextCardEnabled { get; set; } = true;

    public string NextCardString { get; set; } = "Control+Alt+C";

    public bool ToggleLockEnabled { get; set; } = true;

    public string ToggleLockString { get; set; } = "Control+Alt+L";

    public bool LockPlayer { get; set; }

    public double MinSizeMB { get; set; } = 25;

    public double CardScale { get; set; } = 1;

    public double ZoomOnHover { get; set; } = 0.1;

    public bool DarkMode { get; set; }

    public bool MinimizeToTray { get; set; }

    public bool AutoWallpaper { get; set; } = true;

    public double WallpaperBrightness { get; set; } = 60;

    public bool WallpaperDetails { get; set; }

    public bool BlurWallpaper { get; set; }

    public double BlurRadius { get; set; } = 15;

    public bool HideDesktopIcons { get; set; } = true;

    public ClipFilterSettings ClipFilter { get; set; } = new();
}
