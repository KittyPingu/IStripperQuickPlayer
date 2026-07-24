# WinUI 3 Card List View Parity

This document captures the current `ImageListView`/`CardRenderer` behavior that must be preserved when porting the WinForms UI to WinUI 3.

## Current Implementation

- View control: `Manina.Windows.Forms.ImageListView` named `listModelsNew`.
- Renderer: `BLL/CardRenderer.cs`, derived from `ImageListView.ImageListViewRenderer`.
- Card source: `Datastore.modelcards`, filtered/sorted in `Form1.PopulateModelListview`.
- Item identity:
  - `ImageListViewItem.Text` is `modelName + "\r\n" + outfit`.
  - `ImageListViewItem.Tag` is the card id/name.
- Thumbnail size: `162 x 242` scaled by `Properties.Settings.Default.CardScale`.

## Visual Behavior To Preserve

- Fixed card aspect and scaled card size based on `cardScale`.
- Card image is drawn inside the item bounds while reserving roughly `34px` for the two-line label.
- Image rendering changes by scale:
  - `NearestNeighbor` at `cardScale == 1`.
  - `HighQualityBicubic` when zoomed or scaled above 1.
- Two-line centered label:
  - First line: model name.
  - Second line: outfit/show title.
  - Text shrinks until it fits the available card width.
- Selection highlight:
  - Light mode: `PaleGreen`.
  - Dark mode: `Color.FromArgb(40, 80, 100)`.
- Background/label theme:
  - Light: black labels on `WhiteSmoke`.
  - Dark: `AntiqueWhite` labels on `Color.FromArgb(40, 40, 40)`.
- Hover zoom:
  - Uses `Properties.Settings.Default.ZoomOnHover`.
  - Expands around the item and clamps inside the visible list viewport.
  - Hides the text label while zoomed.
  - Also stays zoomed while the card context menu is open.
- Favorite overlay:
  - Drawn near top-right of image.
  - Uses QuickPlayer's custom light-green heart.
- Exclusive overlay:
  - Drawn near top-left using QuickPlayer's custom yellow crown.
- Hotness overlay:
  - Uses QuickPlayer's custom sun when `hotnessLevel == "5"`.
  - Stacks below the exclusive marker when both are present.
- User rating stars:
  - Drawn only when `ShowRatingStars` is enabled.
  - Empty stars are black with alpha.
  - Filled rating is yellow with black outline.
  - Supports half-star increments from 0 to 10 internal rating units.
- Sort value badge:
  - Displays an overlay when sorted by rating, age, breast size, height, ethnicity, purchased date, release date, or user rating when star display is disabled.
  - Text shrinks to fit.
  - Height display respects current region/culture metric vs imperial formatting.
- Now-playing overlay:
  - Compares `nowPlayingTag` against `modelName + "\r\n" + outfit`.
  - Draws a green `Playing` banner over the card.

## Interaction Behavior To Preserve

- Single selection updates the clip list and model details panel.
- Double-click plays the next clip for the selected card.
- Right-click opens the card context menu for the card under the pointer.
- Context menu operations:
  - Toggle favorite.
  - Set user rating through slider/combobox.
  - Show model/outfit/rating/hotness/stats/age/hair/purchased metadata.
  - Open the card in browser.
  - Delete card files from disk.
- Star-rating click behavior:
  - Clicks outside the computed star bounds do nothing.
  - Click X coordinate maps to 10 half-star steps.
  - Value is rounded with `MidpointRounding.AwayFromZero`.
  - Value is clamped to `0..10`.
  - The stored value is the internal half-star count, not the displayed five-star count.
- Hover behavior:
  - Hover invalidates only the hovered item when zoom is enabled.
  - Leaving the list clears hover state and refreshes unless the context menu is open.
- Scroll behavior:
  - The current WinForms version listens for list scroll messages and refreshes the renderer.
- Programmatic navigation:
  - Search/filter/sort tries to preserve the previously selected item by text.
  - Now-playing navigation selects the matching text item and scrolls it into view.

## WinUI 3 Implementation Direction

The WinUI app now uses a dedicated `CardGrid`/`CardTile` component rather than a plain `GridView` template.

Current shape:

- `CardTileViewModel`
  - Wraps model card data plus user metadata from `MyData`.
  - Exposes display fields: model name, outfit, rating text, sort badge, image path/bitmap, flags, and clip count.
- `CardGrid` WinUI control
  - Hosts the filtered/sorted card source.
  - Uses a virtual owner-rendered viewport bitmap instead of per-card XAML controls.
  - Uses fixed item width/height derived from `cardScale`.
  - Draws card image, labels, favorite, exclusive, hotness, sort badge, now-playing, and rating stars directly.
  - Performs manual hit testing for selection, double-click, context menu, and star ratings.
  - Expands on hover using `ZoomOnHover`, keeps the label visible, and clamps inside the visible card-list viewport.
  - Caches resized card images for visible rendering.

If the default WinUI virtualization or hover behavior cannot match the current control, build `CardGrid` as a custom `VirtualizingLayout` plus `Canvas`/Composition overlay. The star hit testing and hover zoom should not be approximated with a loose template because both affect user actions.

## Migration Test Checklist

- Compare light and dark mode card colors against the WinForms screenshot.
- Verify card scale changes update width, height, text fitting, icon sizes, and star positions.
- Verify hover zoom at all viewport edges clamps inside the scrolled list area.
- Verify right-click context menu keeps the card zoomed until it closes.
- Verify favorite, exclusive, hotness, sort badge, stars, and now-playing overlays can coexist without overlap.
- Verify star clicks at left edge, center of each half-star, and right edge store the same values as WinForms.
- Verify filtering/sorting keeps or clears selection exactly as the current app does.
- Verify now-playing selection scrolls to the selected card.
- Verify thousands of cards remain responsive while scrolling.
