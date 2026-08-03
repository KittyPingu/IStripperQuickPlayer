# QuickPlayer REST API

QuickPlayer exposes a local REST API for querying and controlling playback,
the library, and play queues.

## Connection and authentication

The API listens on:

```text
http://127.0.0.1:17871
```

It only accepts connections from the local computer. QuickPlayer creates a
bearer token when it starts:

```text
%LOCALAPPDATA%\IStripperQuickPlayer\api-token.txt
```

Send that token in the `Authorization` header:

> [!IMPORTANT]
> Run this setup block once in every new PowerShell session. Every authenticated
> example below uses the `$headers` variable it creates.
>
> ```powershell
> $token = Get-Content "$env:LOCALAPPDATA\IStripperQuickPlayer\api-token.txt"
> $headers = @{ Authorization = "Bearer $token" }
>
> Invoke-RestMethod http://127.0.0.1:17871/api/v1/status -Headers $headers
> ```

`GET /health` is the only endpoint that does not require authentication.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Check whether the API is running. |
| `GET` | `/api/v1` | List supported endpoints and actions. |
| `GET` | `/api/v1/status` | Get playback, player, and queue state. |
| `GET` | `/api/v1/library` | Search the local card and clip library. |
| `GET` | `/api/v1/queue` | Get manual and automatic queues. |
| `GET` | `/api/v1/fullscreen` | Get active fullscreen clips and iStripper's observed next queue. |
| `POST` | `/api/v1/fullscreen/play` | Replace fullscreen playback with a requested card or clip. |
| `POST` | `/api/v1/fullscreen/next` | Advance fullscreen to iStripper's next queued card. |
| `POST` | `/api/v1/fullscreen/slots/{slotId}/next` | Advance one fullscreen slot. |
| `POST` | `/api/v1/fullscreen/slots/{slotId}/play` | Replace one fullscreen slot with a requested card or clip. |
| `POST` | `/api/v1/fullscreen/source/{source}/next` | Advance one fullscreen source. |
| `POST` | `/api/v1/fullscreen/source/{source}/play` | Replace one fullscreen source with a requested card or clip. |
| `DELETE` | `/api/v1/fullscreen/queue` | Clear iStripper's next queue. |
| `PATCH` | `/api/v1/queue` | Enable or disable queue playback. |
| `POST` | `/api/v1/queue/manual` | Add a manual queue entry. |
| `DELETE` | `/api/v1/queue/manual` | Clear the manual queue. |
| `DELETE` | `/api/v1/queue/manual/{index}` | Remove a manual queue entry. |
| `PATCH` | `/api/v1/queue/manual/{index}` | Move a manual queue entry. |
| `POST` | `/api/v1/queue/automatic/rebuild` | Rebuild the automatic queue. |
| `POST` | `/api/v1/play` | Play a card or exact clip. |
| `POST` | `/api/v1/playback/seek` | Seek by time or relative position. |
| `POST` | `/api/v1/playback/speed` | Set playback speed. |
| `POST` | `/api/v1/actions/{action}` | Run a hotkey-equivalent action. |

Requests and responses use JSON.

## Status

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/status `
  -Headers $headers
```

The response contains:

- `playing`: state, animation path, card, clip, elapsed time, duration, and
  speed;
- `player`: lock, panic, playback-control availability, and seek readiness;
- `queue`: enabled state, active entry, manual entries, and automatic entries.

Playback state is `playing`, `paused`, `stopped`, or `unavailable`.

## Library

Return up to 100 cards:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/library `
  -Headers $headers
```

Search model names, card tags, and outfit names:

```powershell
Invoke-RestMethod `
  "http://127.0.0.1:17871/api/v1/library?search=anna&limit=25" `
  -Headers $headers
```

`limit` defaults to 100 and accepts values from 1 to 1000.

Each card includes both its internal `cardTag` and friendly `cardName`. The
friendly name uses `Model - Outfit`, for example
`Nikki Hill - A Dress To Seduce`.
Each clip includes its `clipName` and numeric `clipNumber`.

## Direct playback

All direct playback requests use `POST /api/v1/play`.

### Play by card tag

This selects a random eligible clip from the card:

```powershell
$body = @{ cardTag = "f0979" } | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

### Play by card title

Use the friendly `Model - Outfit` title instead of the internal tag:

```powershell
$body = @{ cardName = "Nikki Hill - A Dress To Seduce" } | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

### Play an exact clip by filename

```powershell
$body = @{
  cardTag = "f0979"
  clipName = "f0979_38916402.vghd"
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

### Play an exact clip by card title and clip number

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

`cardName` also accepts a unique outfit name such as `A Dress To Seduce`. If
an outfit name matches multiple cards, the API returns `409` rather than
choosing one. Use the full `Model - Outfit` name in that case. Use either
`cardTag` or `cardName`, and either `clipName` or `clipNumber`.

## Queue control

### Get the queues

Return the manual and automatic queues:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue `
  -Headers $headers
```

### Add an exact clip to the manual queue

```powershell
$body = @{
  cardTag = "f0979"
  clipName = "f0979_38916402.vghd"
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

### Add by card title and clip number

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Include `index` in either add request to insert at a particular zero-based
position.

### Move a manual queue entry

Move entry `0` to position `2`:

```powershell
$body = @{ index = 2 } | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual/0 `
  -Method Patch -Headers $headers -ContentType application/json -Body $body
```

### Remove a manual queue entry

Remove entry `0`:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual/0 `
  -Method Delete -Headers $headers
```

### Clear the manual queue

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual `
  -Method Delete -Headers $headers
```

### Enable or disable queue playback

```powershell
$body = @{ enabled = $true } | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue `
  -Method Patch -Headers $headers -ContentType application/json -Body $body
```

### Rebuild the automatic queue

```powershell
Invoke-RestMethod `
  http://127.0.0.1:17871/api/v1/queue/automatic/rebuild `
  -Method Post -Headers $headers
```

## Fullscreen playback

Fullscreen can render several logical slots at once. Each slot has both a
numeric `slotId` and a source name such as `Girl03`.

### Get fullscreen state

Return the active clips, logical slots, source names, and observed
`CardSequencer` queue:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen `
  -Headers $headers
```

Use the returned `slotId` or `source` to target one logical slot.

### Advance fullscreen globally

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/next `
  -Method Post -Headers $headers
```

### Advance one slot by ID

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/slots/2/next `
  -Method Post -Headers $headers
```

Replace `2` with a `slotId` returned by `GET /api/v1/fullscreen`.

### Advance one slot by source

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/source/Girl03/next `
  -Method Post -Headers $headers
```

Replace `Girl03` with a `source` returned by `GET /api/v1/fullscreen`.

### Request an exact fullscreen clip globally

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

### Play an exact clip in one slot by ID

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/slots/2/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Only the selected logical slot is replaced. Replace `2` with a returned
`slotId`.

### Play an exact clip in one slot by source

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/source/Girl03/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Only the matching logical source is replaced. Source matching is
case-insensitive.

The targeted play endpoints create iStripper's native `PlayableCard` and give
it to the selected scene node. Desktop `ForceAnim` remains separate.

### Clear iStripper's fullscreen queue

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/queue `
  -Method Delete -Headers $headers
```

## Seeking and speed

Seek to an elapsed time in milliseconds:

```powershell
$body = @{ elapsedMilliseconds = 30000 } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17871/api/v1/playback/seek `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Seek to a relative position from 0 to 1:

```powershell
$body = @{ position = 0.5 } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17871/api/v1/playback/seek `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Set playback speed:

```powershell
$body = @{ speed = 1.5 } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17871/api/v1/playback/speed `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Allowed speeds are `0.25`, `0.5`, `1`, `1.5`, `2`, `3`, and `4`. Legacy WMV
playback is limited to 3x.

## Actions

Invoke an action with:

```powershell
Invoke-RestMethod `
  http://127.0.0.1:17871/api/v1/actions/next-clip `
  -Method Post -Headers $headers
```

Supported actions:

| Action | Effect |
| --- | --- |
| `next-clip` | Play the next clip. |
| `next-card` | Play the next card or queued entry. |
| `toggle-lock` | Toggle the player lock. |
| `play-pause` | Pause or resume playback. |
| `back-10-percent` | Move backward by 10%. |
| `forward-10-percent` | Move forward by 10%. |
| `restart` | Restart the current clip. |
| `player-large` | Switch to the large player size. |
| `player-small` | Switch to the small player size. |
| `now-playing-info` | Show the current card overlay. |
| `panic` | Hide or restore playback and QuickPlayer desktop changes. |

Playback operations return `202 Accepted` because they continue
asynchronously on QuickPlayer's UI thread.

## Errors

Errors use this shape:

```json
{
  "error": "Description of the problem."
}
```

Common status codes:

| Status | Meaning |
| --- | --- |
| `400` | Missing or invalid request data. |
| `401` | Missing or incorrect bearer token. |
| `404` | Unknown endpoint, action, card, clip, or queue index. |
| `409` | The requested playback feature is not currently available. |
| `413` | Request body exceeds 16 KB. |
| `500` | Unexpected QuickPlayer error. |
| `503` | iStripper is not available. |
