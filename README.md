# IStripperQuickPlayer
Quick Player for IStripper cards

![MainWindow](https://github.com/KittyPingu/IStripperQuickPlayer/blob/master/IStripperQuickPlayer.png?raw=true)

If you are not on windows 11 you should install Segoe Fluent Icons font from https://docs.microsoft.com/en-us/windows/apps/design/downloads/#fonts - rating stars won't draw without this font

- loads models from local data
- add favourites and your own ratings and tags to a card
- filter cards on model names and tags
- filter cards on breast size/age/rating/user rating
- save/load filters
- sort by rating/age/name/breast size/purchased date/release date
- view list of clips for a card, and play any clip by clicking on it
- clicking the "Now Playing" text will zoom to that card
- button to play next clip in the cards list
- hotkeys for next clip/card
- pause/resume desktop playback, select 0.25x-4x speed, and seek backward or
  forward by 10 seconds without reloading the active clip
- optionally persist compressed modern-clip alpha checkpoints to accelerate
  later long-distance seeks
- reload models menu - use this when you have downloaded/bought new cards
- filter clip list by explicitness/size/type
- set windows wallpaper based on card now playing

## Releases

Every push to `master` and pull request targeting it builds a self-contained
Windows installer. Pushing a tag such as `v0.36.0` also creates or updates the
matching GitHub release.

On the first .NET 10 launch, legacy filters, ratings, favourites, and tags are
converted to JSON in place. The original files are retained beside them with a
`.binary-backup` suffix. The derived model cache is rebuilt automatically.

