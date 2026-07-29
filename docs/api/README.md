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
friendly name uses `Model - Outfit`, for example `Anna Example - Pool Side`.
Each clip includes its `clipName` and numeric `clipNumber`.

## Direct playback

Play a random eligible clip from a card:

```powershell
$body = @{ cardTag = "f0960" } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

The same request can use the friendly card name:

```powershell
$body = @{ cardName = "Anna Example - Pool Side" } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Play an exact clip:

```powershell
$body = @{
  cardTag = "f0960"
  clipName = "f0960_6144401.vghd"
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

An exact clip can also be selected by friendly card name and clip number:

```powershell
$body = @{
  cardName = "Anna Example - Pool Side"
  clipNumber = 4
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

`cardName` also accepts a unique outfit name such as `Pool Side`. If an outfit
name matches multiple cards, the API returns `409` rather than choosing one.
Use the full `Model - Outfit` name in that case. Use either `cardTag` or
`cardName`, and either `clipName` or `clipNumber`.

## Queue control

Get both queues:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue `
  -Headers $headers
```

Add a card or exact clip to the end of the manual queue:

```powershell
$body = @{
  cardTag = "f0960"
  clipName = "f0960_6144401.vghd"
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Friendly names and clip numbers work here too:

```powershell
$body = @{
  cardName = "Anna Example - Pool Side"
  clipNumber = 4
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Include `index` in the request body to insert at a particular zero-based
position.

Move entry 0 to position 2:

```powershell
$body = @{ index = 2 } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual/0 `
  -Method Patch -Headers $headers -ContentType application/json -Body $body
```

Remove entry 0:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual/0 `
  -Method Delete -Headers $headers
```

Clear the manual queue:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual `
  -Method Delete -Headers $headers
```

Enable or disable queue playback:

```powershell
$body = @{ enabled = $true } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue `
  -Method Patch -Headers $headers -ContentType application/json -Body $body
```

Rebuild the automatic queue:

```powershell
Invoke-RestMethod `
  http://127.0.0.1:17871/api/v1/queue/automatic/rebuild `
  -Method Post -Headers $headers
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
