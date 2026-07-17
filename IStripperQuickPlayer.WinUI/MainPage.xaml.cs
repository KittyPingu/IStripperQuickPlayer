using Microsoft.UI.Xaml.Controls;
using IStripperQuickPlayer.WinUI.Controls;
using IStripperQuickPlayer.WinUI.Core;
using IStripperQuickPlayer.WinUI.Services;
using IStripperQuickPlayer.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace IStripperQuickPlayer.WinUI;

/// <summary>
/// The main content page displayed inside the application window.
/// Add your UI logic, event handlers, and data binding here.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _nowPlayingTimer = new();
    private HotkeyService? _hotkeyService;
    private TrayIconService? _trayIconService;
    private TaskbarThumbnailService? _taskbarThumbnailService;
    private LockPlayerService? _lockPlayerService;

    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        IstripperPaths paths = new();
        PropertiesCatalog propertiesCatalog = new(paths);
        CardQueryService queryService = new();
        PlayerControlService playerControlService = new();
        ViewModel = new MainViewModel(
            new AppSettingsStore(paths),
            new FilterSettingsStore(paths),
            new UserDataStore(paths),
            new ModelsListLoader(paths, propertiesCatalog),
            queryService,
            playerControlService,
            new WallpaperService(paths),
            new CardDeleteService(paths));
        _lockPlayerService = new LockPlayerService(
            ViewModel.CreateFilterEnforcementState,
            queryService,
            playerControlService,
            message => DispatcherQueue.TryEnqueue(() => ViewModel.Status = message),
            (card, _) => DispatcherQueue.TryEnqueue(() => ViewModel.SelectCardById(card.Name)));

        InitializeComponent();
        DataContext = ViewModel;
        ApplyTheme();
        _nowPlayingTimer.Interval = TimeSpan.FromSeconds(2);
        _nowPlayingTimer.Tick += NowPlayingTimer_Tick;
        Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        _trayIconService ??= new TrayIconService(WindowNative.GetWindowHandle(App.MainWindow));
        _taskbarThumbnailService ??= new TaskbarThumbnailService(WindowNative.GetWindowHandle(App.MainWindow), ViewModel.PlayNextClip, ViewModel.PlayNextCard);
        _lockPlayerService?.Start(ViewModel.Settings.LockPlayer);
        RegisterHotkeys();
        _nowPlayingTimer.Start();
        Unloaded += MainPage_Unloaded;
    }

    private void MainPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.RestoreWallpaperIfChanged();
        _hotkeyService?.Dispose();
        _taskbarThumbnailService?.Dispose();
        _trayIconService?.Dispose();
        _lockPlayerService?.Dispose();
    }

    private void RegisterHotkeys()
    {
        _hotkeyService?.Dispose();
        nint hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        _hotkeyService = new HotkeyService(hwnd, DispatcherQueue);
        if (ViewModel.Settings.NextClipEnabled)
        {
            _hotkeyService.Register(ViewModel.Settings.NextClipString, ViewModel.PlayNextClip);
        }

        if (ViewModel.Settings.NextCardEnabled)
        {
            _hotkeyService.Register(ViewModel.Settings.NextCardString, ViewModel.PlayNextCard);
        }

        if (ViewModel.Settings.ToggleLockEnabled)
        {
            _hotkeyService.Register(ViewModel.Settings.ToggleLockString, TogglePlayerLock);
        }
    }

    private void NowPlayingTimer_Tick(object? sender, object e)
    {
        string? nowPlayingCardId = ViewModel.UpdateNowPlaying();
        if (!string.IsNullOrWhiteSpace(nowPlayingCardId))
        {
            CardsGrid.EnsureCardVisible(nowPlayingCardId);
        }
    }

    private void CardsGrid_RatingChanged(object sender, RatingChangedEventArgs e)
    {
        ViewModel.SetRating(e.CardId, e.Rating);
    }

    private void CardsGrid_SelectedCardChanged(object sender, CardTileViewModel? e)
    {
        ViewModel.SelectedCard = e;
    }

    private void CardsGrid_CardDoubleClicked(object sender, CardTileEventArgs e)
    {
        ViewModel.SelectedCard = e.Tile;
        ViewModel.PlayNextClip();
    }

    private void CardsGrid_CardContextRequested(object sender, CardTileContextRequestedEventArgs e)
    {
        ViewModel.SelectedCard = e.Tile;

        MenuFlyout menu = new();
        MenuFlyoutItem favourite = new() { Text = e.Tile.IsFavourite ? "Remove Favourite" : "Add Favourite" };
        favourite.Click += (_, _) => ViewModel.ToggleFavourite();
        menu.Items.Add(favourite);

        MenuFlyoutItem nextClip = new() { Text = "Next Clip" };
        nextClip.Click += (_, _) => ViewModel.PlayNextClip();
        menu.Items.Add(nextClip);

        MenuFlyoutItem photos = new() { Text = "Photos" };
        photos.Click += (_, _) => OpenPhotosWindow();
        menu.Items.Add(photos);

        MenuFlyoutItem wallpaper = new() { Text = "Set Wallpaper" };
        wallpaper.Click += async (_, _) => await ViewModel.SetWallpaperAsync();
        menu.Items.Add(wallpaper);

        MenuFlyoutItem browser = new() { Text = "Show Model In Browser" };
        browser.Click += (_, _) => ViewModel.ShowSelectedModelInBrowser();
        menu.Items.Add(browser);

        MenuFlyoutSubItem rating = new() { Text = "My Rating" };
        for (int halfStars = 0; halfStars <= 10; halfStars++)
        {
            decimal value = halfStars;
            MenuFlyoutItem item = new() { Text = halfStars == 0 ? "Clear" : $"{halfStars / 2.0:0.0} stars" };
            item.Click += (_, _) => ViewModel.SetRating(e.Tile.CardId, value);
            rating.Items.Add(item);
        }

        menu.Items.Add(rating);

        MenuFlyoutItem delete = new() { Text = "Delete Local Card Files" };
        delete.Click += async (_, _) => await ConfirmDeleteSelectedCardAsync();
        menu.Items.Add(delete);

        menu.ShowAt(e.Target, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.Position
        });
    }

    private void Reload_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.Reload();
    }

    private void SettingsChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLiveViewSettings();
        ViewModel.SaveSettings();
    }

    private void SettingsChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ApplyLiveViewSettings();
        ViewModel.SaveSettings();
    }

    private void SettingsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ApplyLiveViewSettings();
        ViewModel.SaveSettings();
    }

    private void SettingsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ApplyLiveViewSettings();
        ViewModel.SaveSettings();
    }

    private void SettingsChanged(object sender, TextChangedEventArgs e)
    {
        ApplyLiveViewSettings();
        ViewModel.SaveSettings();
    }

    private void ApplyLiveViewSettings()
    {
        if (CardsGrid == null)
        {
            return;
        }

        CardsGrid.CardScale = ViewModel.Settings.CardScale;
        CardsGrid.HoverZoomRatio = ViewModel.Settings.ZoomOnHover;
    }

    private void FilterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.ApplyFilterChanges();
    }

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.ApplyFilterChanges();
    }

    private void SaveFilter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.SaveCurrentFilter();
    }

    private async void SaveFilterAs_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TextBox nameBox = new()
        {
            Header = "Filter name",
            Text = ViewModel.SelectedFilterName == "Default" ? string.Empty : ViewModel.SelectedFilterName
        };

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Save Filter As",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
        {
            ViewModel.SaveCurrentFilter(nameBox.Text);
        }
    }

    private void DeleteFilter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.DeleteCurrentFilter();
    }

    private async void AdvancedFilter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        FilterSettings filter = ViewModel.CurrentFilter;
        NumberBox minAge = NumberBox(filter.MinAge);
        NumberBox maxAge = NumberBox(filter.MaxAge);
        NumberBox minBust = NumberBox(filter.MinBust);
        NumberBox maxBust = NumberBox(filter.MaxBust);
        NumberBox minRating = NumberBox(filter.MinRating);
        NumberBox maxRating = NumberBox(filter.MaxRating);
        NumberBox minMyRating = NumberBox(filter.MinMyRating);
        NumberBox maxMyRating = NumberBox(filter.MaxMyRating);
        CalendarDatePicker minDate = new() { Date = filter.MinDate, Header = "From" };
        CalendarDatePicker maxDate = new() { Date = filter.MaxDate, Header = "To" };

        CheckBox iStripper = CheckBox("iStripper", filter.IStripper);
        CheckBox iStripperClassic = CheckBox("iStripper Classic", filter.IStripperClassic);
        CheckBox iStripperXXX = CheckBox("iStripper XXX", filter.IStripperXXX);
        CheckBox vgClassic = CheckBox("VG Classic", filter.VGClassic);
        CheckBox deskBabes = CheckBox("Desk Babes", filter.DeskBabes);
        CheckBox virtuaGuy = CheckBox("Virtua Guy", filter.VirtuaGuy);
        CheckBox tradingCard = CheckBox("Trading Card", filter.TradingCard);
        CheckBox normal = CheckBox("Normal", filter.Normal);
        CheckBox special = CheckBox("Special", filter.Special);

        Grid grid = new()
        {
            ColumnSpacing = 10,
            RowSpacing = 8,
            MaxWidth = 720
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddLabeledPair(grid, "Age", minAge, maxAge, 0);
        AddLabeledPair(grid, "Bust", minBust, maxBust, 1);
        AddLabeledPair(grid, "Rating", minRating, maxRating, 2);
        AddLabeledPair(grid, "My Rating", minMyRating, maxMyRating, 3);

        StackPanel datePanel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        datePanel.Children.Add(minDate);
        datePanel.Children.Add(maxDate);
        Grid.SetRow(datePanel, 4);
        Grid.SetColumnSpan(datePanel, 2);
        grid.Children.Add(datePanel);

        StackPanel collections = new() { Orientation = Orientation.Vertical, Spacing = 4 };
        foreach (CheckBox checkBox in new[] { iStripper, iStripperClassic, iStripperXXX, vgClassic, deskBabes, virtuaGuy, tradingCard, normal, special })
        {
            checkBox.Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 12, 6);
            collections.Children.Add(checkBox);
        }
        Grid.SetRow(collections, 5);
        Grid.SetColumnSpan(collections, 2);
        grid.Children.Add(collections);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Filter",
            Content = grid,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        filter.MinAge = (decimal)minAge.Value;
        filter.MaxAge = (decimal)maxAge.Value;
        filter.MinBust = (decimal)minBust.Value;
        filter.MaxBust = (decimal)maxBust.Value;
        filter.MinRating = (decimal)minRating.Value;
        filter.MaxRating = (decimal)maxRating.Value;
        filter.MinMyRating = (decimal)minMyRating.Value;
        filter.MaxMyRating = (decimal)maxMyRating.Value;
        filter.MinDate = minDate.Date?.DateTime ?? filter.MinDate;
        filter.MaxDate = maxDate.Date?.DateTime ?? filter.MaxDate;
        filter.IStripper = iStripper.IsChecked == true;
        filter.IStripperClassic = iStripperClassic.IsChecked == true;
        filter.IStripperXXX = iStripperXXX.IsChecked == true;
        filter.VGClassic = vgClassic.IsChecked == true;
        filter.DeskBabes = deskBabes.IsChecked == true;
        filter.VirtuaGuy = virtuaGuy.IsChecked == true;
        filter.TradingCard = tradingCard.IsChecked == true;
        filter.Normal = normal.IsChecked == true;
        filter.Special = special.IsChecked == true;
        ViewModel.ApplyFilterChanges();
    }

    private async void ImportFilter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        FileOpenPicker picker = CreateOpenPicker(".json");
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ViewModel.ImportFilter(file.Path);
        }
    }

    private async void ExportFilter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        FileSavePicker picker = new();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("WinUI filter", [".json"]);
        picker.SuggestedFileName = ViewModel.SelectedFilterName;
        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            ViewModel.ExportCurrentFilter(file.Path);
        }
    }

    private void Favourite_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.ToggleFavourite();
    }

    private void NextClip_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.PlayNextClip();
    }

    private void NextCard_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.PlayNextCard();
    }

    private void OpenBrowser_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.ShowSelectedModelInBrowser();
    }

    private void ShowModel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        string? cardId = ViewModel.ShowNowPlayingModel();
        if (!string.IsNullOrWhiteSpace(cardId))
        {
            CardsGrid.EnsureCardVisible(cardId);
        }
    }

    private void Photos_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        OpenPhotosWindow();
    }

    private void OpenPhotosWindow()
    {
        if (ViewModel.SelectedCard?.Card == null)
        {
            return;
        }

        PhotoWindow window = new(ViewModel.SelectedCard.Card.Name);
        window.Activate();
    }

    private async void Wallpaper_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ViewModel.SetWallpaperAsync();
    }

    private async void WallpaperOptions_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CheckBox auto = CheckBox("Automatic wallpaper", ViewModel.Settings.AutoWallpaper);
        CheckBox details = CheckBox("Show details text", ViewModel.Settings.WallpaperDetails);
        CheckBox blur = CheckBox("Blur image", ViewModel.Settings.BlurWallpaper);
        CheckBox hideIcons = CheckBox("Hide desktop icons", ViewModel.Settings.HideDesktopIcons);
        NumberBox brightness = new()
        {
            Header = "Brightness %",
            Value = ViewModel.Settings.WallpaperBrightness,
            Minimum = 1,
            Maximum = 200,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        NumberBox blurRadius = new()
        {
            Header = "Blur Radius",
            Value = ViewModel.Settings.BlurRadius,
            Minimum = 1,
            Maximum = 50,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };

        StackPanel panel = new() { Spacing = 8 };
        panel.Children.Add(auto);
        panel.Children.Add(brightness);
        panel.Children.Add(details);
        panel.Children.Add(blur);
        panel.Children.Add(blurRadius);
        panel.Children.Add(hideIcons);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Wallpaper Options",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.Settings.AutoWallpaper = auto.IsChecked == true;
        ViewModel.Settings.WallpaperBrightness = brightness.Value;
        ViewModel.Settings.WallpaperDetails = details.IsChecked == true;
        ViewModel.Settings.BlurWallpaper = blur.IsChecked == true;
        ViewModel.Settings.BlurRadius = blurRadius.Value;
        ViewModel.Settings.HideDesktopIcons = hideIcons.IsChecked == true;
        ViewModel.SaveSettings();
    }

    private void DarkMode_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.SaveSettings();
        ApplyTheme();
    }

    private void Tray_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _trayIconService?.HideWindow();
    }

    private void LockPlayer_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ApplyPlayerLockSetting();
    }

    private void TogglePlayerLock()
    {
        ViewModel.Settings.LockPlayer = !ViewModel.Settings.LockPlayer;
        LockPlayerButton.IsChecked = ViewModel.Settings.LockPlayer;
        ApplyPlayerLockSetting();
    }

    private void ApplyPlayerLockSetting()
    {
        ViewModel.SaveSettings();
        _lockPlayerService?.SetPlayerLocked(ViewModel.Settings.LockPlayer);
    }

    private async void Hotkeys_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CheckBox nextClipEnabled = CheckBox("Next Clip", ViewModel.Settings.NextClipEnabled);
        TextBox nextClipText = new() { Text = ViewModel.Settings.NextClipString };
        CheckBox nextCardEnabled = CheckBox("Next Card", ViewModel.Settings.NextCardEnabled);
        TextBox nextCardText = new() { Text = ViewModel.Settings.NextCardString };
        CheckBox toggleLockEnabled = CheckBox("Toggle Lock", ViewModel.Settings.ToggleLockEnabled);
        TextBox toggleLockText = new() { Text = ViewModel.Settings.ToggleLockString };

        Grid grid = new() { ColumnSpacing = 10, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHotkeyRow(grid, nextClipEnabled, nextClipText, 0);
        AddHotkeyRow(grid, nextCardEnabled, nextCardText, 1);
        AddHotkeyRow(grid, toggleLockEnabled, toggleLockText, 2);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Hotkeys",
            Content = grid,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.Settings.NextClipEnabled = nextClipEnabled.IsChecked == true;
        ViewModel.Settings.NextClipString = nextClipText.Text;
        ViewModel.Settings.NextCardEnabled = nextCardEnabled.IsChecked == true;
        ViewModel.Settings.NextCardString = nextCardText.Text;
        ViewModel.Settings.ToggleLockEnabled = toggleLockEnabled.IsChecked == true;
        ViewModel.Settings.ToggleLockString = toggleLockText.Text;
        ViewModel.SaveSettings();
        RegisterHotkeys();
    }

    private async void LoadPlaylist_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        FileOpenPicker picker = CreateOpenPicker(".vpl");
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ViewModel.ApplyPlaylist(file.Path);
        }
    }

    private async void DeleteCard_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ConfirmDeleteSelectedCardAsync();
    }

    private async Task ConfirmDeleteSelectedCardAsync()
    {
        if (ViewModel.SelectedCard?.Card == null)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Delete local card files?",
            Content = "This deletes the selected card's local model and metadata folders. You may need to restart or synchronize iStripper afterwards.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.DeleteSelectedCardFromDisk();
        }
    }

    private static FileOpenPicker CreateOpenPicker(string extension)
    {
        FileOpenPicker picker = new();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(extension);
        return picker;
    }

    private void ApplyTheme()
    {
        RequestedTheme = ViewModel.Settings.DarkMode ? ElementTheme.Dark : ElementTheme.Light;
    }

    private static NumberBox NumberBox(decimal value)
    {
        return new NumberBox
        {
            Value = (double)value,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
    }

    private static CheckBox CheckBox(string text, bool isChecked)
    {
        return new CheckBox
        {
            Content = text,
            IsChecked = isChecked
        };
    }

    private static void AddLabeledPair(Grid grid, string label, NumberBox min, NumberBox max, int row)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
        min.Header = "Min";
        max.Header = "Max";
        panel.Children.Add(min);
        panel.Children.Add(max);
        Grid.SetRow(panel, row);
        Grid.SetColumnSpan(panel, 2);
        grid.Children.Add(panel);
    }

    private static void AddHotkeyRow(Grid grid, CheckBox enabled, TextBox textBox, int row)
    {
        Grid.SetRow(enabled, row);
        Grid.SetColumn(enabled, 0);
        Grid.SetRow(textBox, row);
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(enabled);
        grid.Children.Add(textBox);
    }
}
