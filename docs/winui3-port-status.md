# WinUI 3 Port Status

The WinForms project is intentionally left as the working reference app. The WinUI 3 project is being built side by side for comparison.

## Ported Into WinUI

- Direct `models.lst` loading from the iStripper registry `DataPath`.
- Static and dynamic properties XML loading.
- Card image path discovery.
- Card grid shell with fixed card sizing based on the WinForms `162 x 242` base.
- Card overlays for:
  - user rating stars,
  - favourite,
  - exclusive,
  - max hotness,
  - now playing.
- Hover zoom driven by `ZoomOnHover`, with labels kept visible while expanded and clamped to the visible card-list viewport.
- Half-star rating hit testing compatible with the WinForms renderer.
- Card search with `and`, `or`, and `!` handling.
- Card sorting by model name, user rating, rating, age, breast size, ethnicity, height, purchased date, and release date.
- Card scale, favourite-only filter, and show-rating-stars setting.
- Clip list filtering by hotness, demo, minimum size, and clip type search.
- Selecting a clip sets `ForceAnim` in `HKCU\Software\Totem\vghd\parameters`.
- Next clip and next card actions.
- Now-playing polling from `CurrentAnim`.
- User tags, user ratings, and favourites persisted to a WinUI sidecar JSON store.
- Open selected model in browser.
- Photo viewer using the same `photos.json` endpoint and private-photo registry keys.
- Manual wallpaper action for selected cards.
- Saved filter selection, edit, save, save-as, delete, import, and export.
- Playlist `.vpl` import, mapped to the same `or` search expression used by the WinForms app.
- Delete selected card's local model and metadata folders.
- Card double-click to play the next clip.
- Card right-click context menu for favourite, next clip, photos, wallpaper, browser, user rating, and delete.
- Global hotkeys for next clip and next card using native `RegisterHotKey`.
- Hotkey configuration UI for next clip, next card, and toggle-lock shortcut strings.
- Lock-player Deviare integration:
  - attaches to `vghd.exe`,
  - blocks player dragging while locked by intercepting `CallWindowProcW`,
  - enforces the visible card/clip filter by intercepting `RegSetValueExW` for `CurrentAnim`.
- Minimize-to-tray behavior using native `Shell_NotifyIcon`.
- Automatic wallpaper on now-playing card changes, plus brightness, blur, detail text, and desktop-icon options.
- Wallpaper restore on normal app shutdown after the WinUI app changes the desktop wallpaper.
- Taskbar thumbnail buttons for next clip and next card where the Windows taskbar COM interface is available.
- Dark mode toggle.
- Custom WinUI app icon, taskbar icon, tile logos, and splash/logo assets.
- Card-list performance improvements:
  - owner-rendered virtual card surface using one viewport bitmap instead of one XAML tree per card,
  - stable full card source to avoid `ItemsRepeater` layout gaps,
  - resized card-image cache for visible rendering,
  - solid window background instead of live Mica backdrop,
  - now-playing updates touch only the previous/current cards instead of every card,
  - delayed hover zoom to avoid firing transforms while wheel-scrolling through cards.

## Still To Port

- Multi-monitor wallpaper target selection.
- Visual parity tuning for text fitting and exact overlay positioning.
- Keep hover expansion active while the card context menu is open.
- Runtime side-by-side comparison pass against the existing WinForms app.

## Data Safety

The WinUI app does not write the WinForms `mydata.bin`. Its ratings, favourites, tags, and settings are stored under `%LOCALAPPDATA%\IStripperQuickPlayer.WinUI` to avoid breaking the existing WinForms app's data files.
