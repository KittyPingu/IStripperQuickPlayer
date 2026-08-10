# QuickPlayer REST API

QuickPlayer exposes a local REST API for querying and controlling playback,
the library, and play queues.

## Running the API

QuickPlayer normally starts its full interface and API together. The API port
defaults to `17871` and can be changed in either mode:

```powershell
.\IStripperQuickPlayer.exe --api-port 18000
```

Start only the API, model catalogue, and iStripper playback bridge with:

```powershell
.\IStripperQuickPlayer.exe --api-only
.\IStripperQuickPlayer.exe --api-only --api-port 18000
```

API-only mode displays no main window. Its tray icon has a **Quit** command.
Only one full or API-only QuickPlayer instance can run at a time.

API-only mode starts with an empty, process-local QuickPlayer queue. It does
not restore a previous queue or generate automatic entries. Manual queue and
native fullscreen queue operations still work when explicitly requested;
when the manual queue empties, iStripper resumes its own queueing.

## Connection and authentication

The API listens on:

```text
http://127.0.0.1:17871
```

Replace `17871` in the examples below when using `--api-port`.

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

## AutoHotkey v2

AutoHotkey can call the API directly through Windows' built-in WinHTTP COM
object. This helper reads QuickPlayer's token and throws an error for any
non-success response:

```ahk
#Requires AutoHotkey v2.0

QuickPlayerBase := "http://127.0.0.1:17871/api/v1"
QuickPlayerToken := Trim(
    FileRead(EnvGet("LOCALAPPDATA") "\IStripperQuickPlayer\api-token.txt", "UTF-8"),
    " `t`r`n"
)

QuickPlayer(method, path, body := "")
{
    global QuickPlayerBase, QuickPlayerToken

    http := ComObject("WinHttp.WinHttpRequest.5.1")
    http.Open(method, QuickPlayerBase path, false)
    http.SetRequestHeader("Authorization", "Bearer " QuickPlayerToken)

    if (body != "") {
        http.SetRequestHeader("Content-Type", "application/json")
        http.Send(body)
    } else {
        http.Send()
    }

    if (http.Status < 200 || http.Status >= 300)
        throw Error("QuickPlayer returned " http.Status ": " http.ResponseText)

    return http.ResponseText
}
```

The following hotkeys toggle play/pause, advance to the next card, display
API status, and play an exact clip:

```ahk
F8::QuickPlayer("POST", "/actions/play-pause")
F9::QuickPlayer("POST", "/actions/next-card")
F10::MsgBox QuickPlayer("GET", "/status")

F11::{
    body := '{"cardName":"Nikki Hill - A Dress To Seduce","clipNumber":11}'
    QuickPlayer("POST", "/play", body)
}
```

Calls are synchronous because `Open` uses `false`. Change the port in
`QuickPlayerBase` when QuickPlayer is started with `--api-port`.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Check whether the API is running. |
| `GET` | `/api/v1` | List supported endpoints and actions. |
| `GET` | `/api/v1/status` | Get playback, player, and queue state. |
| `GET` | `/api/v1/library` | Search the local card and clip library. |
| `GET` | `/api/v1/queue` | Get manual and automatic queues. |
| `GET` | `/api/v1/fullscreen` | Get active fullscreen clips and iStripper's observed next queue. |
| `GET` | `/api/v1/fullscreen/shader-data` | Read the current external fullscreen shader values and sequence. |
| `PUT` | `/api/v1/fullscreen/shader-data` | Publish four external values to fullscreen shaders. |
| `GET` | `/api/v1/fullscreen/shader-texture` | Read the current external shader texture metadata. |
| `PUT` | `/api/v1/fullscreen/shader-texture` | Publish a PNG, JPEG, or raw RGBA8 texture to fullscreen shaders. |
| `GET` | `/api/v1/fullscreen/shader-texture/shared-memory` | Discover the optional local shared-memory texture channel. |
| `GET` | `/api/v1/fullscreen/queue` | Get iStripper's native next queue. |
| `POST` | `/api/v1/fullscreen/queue/insert` | Insert a card or exact clip at the start of the native next queue. |
| `POST` | `/api/v1/fullscreen/queue/add` | Add a card or exact clip at the end of the native next queue. |
| `DELETE` | `/api/v1/fullscreen/queue` | Clear iStripper's native next queue. |
| `DELETE` | `/api/v1/fullscreen/queue/{index}` | Remove one native next-queue entry by its current index. |
| `POST` | `/api/v1/fullscreen/play` | Replace fullscreen playback with a requested card or clip. |
| `POST` | `/api/v1/fullscreen/next` | Advance fullscreen to iStripper's next queued card. |
| `POST` | `/api/v1/fullscreen/slots/{slotId}/next` | Advance one fullscreen slot. |
| `POST` | `/api/v1/fullscreen/slots/{slotId}/play` | Replace one fullscreen slot with a requested card or clip. |
| `POST` | `/api/v1/fullscreen/source/{source}/next` | Advance one fullscreen source. |
| `POST` | `/api/v1/fullscreen/source/{source}/play` | Replace one fullscreen source with a requested card or clip. |
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
Library, status, and queue entries include `source` (`istripper` or `custom`)
and nullable `showId`. A playing custom show uses
`animationPath: "custom:<32-character-show-id>"`.

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
Cards also expose the shared metadata fields used by the UI: model, outfit,
description, tags, release/show dates, age, official rating, hotness,
exclusive/performer values, measurements, hair, ethnicity, city, and country.

## Direct playback

Play a random eligible clip from a card:

```powershell
$body = @{ cardTag = "f0979" } | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

The same request can use the friendly card name:

```powershell
$body = @{ cardName = "Nikki Hill - A Dress To Seduce" } | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Play an exact clip:

```powershell
$body = @{
  cardTag = "f0979"
  clipName = "f0979_38916402.vghd"
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

An exact clip can also be selected by friendly card name and clip number:

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
Custom card tags and clip names are both `custom:<show-id>` and work with the
same play, queue, seek, speed, and action routes; no custom-only REST route is
required.

## Queue control

Get both queues:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue `
  -Headers $headers
```

Add a card or exact clip to the end of the manual queue:

```powershell
$body = @{
  cardTag = "f0979"
  clipName = "f0979_38916402.vghd"
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/queue/manual `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Friendly names and clip numbers work here too:

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
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

Automatic rebuilding returns `409 Conflict` in API-only mode because that
mode intentionally leaves automatic queueing to iStripper.

## Fullscreen playback

Fullscreen can render several clips at once. Query iStripper's logical scene
slots, source names, and current `CardSequencer` queue:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen `
  -Headers $headers
```

Advance globally, by slot ID, or by source:

```powershell
Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/next `
  -Method Post -Headers $headers

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/slots/2/next `
  -Method Post -Headers $headers

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/source/Girl03/next `
  -Method Post -Headers $headers
```

Request an exact fullscreen clip globally, by slot ID, or by source:

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/slots/2/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/source/Girl03/play `
  -Method Post -Headers $headers -ContentType application/json -Body $body
```

Replace `2` or `Girl03` with a `slotId` or `source` returned by
`GET /api/v1/fullscreen`. Source matching is case-insensitive. Only the
selected logical slot is replaced.

The targeted play endpoints create iStripper's native `PlayableCard` and give
it to the selected scene node.

### External shader data

Fullscreen shaders can declare two optional uniforms:

```glsl
uniform vec4 u_QuickPlayerData;
uniform float u_QuickPlayerSequence;
```

Publish four arbitrary values from an external application:

```powershell
$body = @{ values = @(12.5, 3, -0.75, 900) } | ConvertTo-Json

Invoke-RestMethod `
  http://127.0.0.1:17871/api/v1/fullscreen/shader-data `
  -Method Put -Headers $headers -ContentType application/json -Body $body
```

The response contains the stored values and the automatically incremented
sequence:

```json
{
  "sequence": 1,
  "values": [12.5, 3, -0.75, 900]
}
```

QuickPlayer does not assign meanings to the four values. Backstage and the
shader can use them as commands, arguments, flags, positions, scores, or any
other finite 32-bit floating-point data. Each successful `PUT` increments
`u_QuickPlayerSequence`, including when the four values are unchanged. The
sequence runs from `0` through `16777215` and then wraps to `0`, so every value
is represented exactly by a shader float.

The bridge supplies both uniforms whenever an OpenGL shader program declaring
them is bound. An update is therefore normally visible on the next rendered
frame. Programs that do not declare the uniforms are unaffected. Read the
current state without changing its sequence with:

```powershell
Invoke-RestMethod `
  http://127.0.0.1:17871/api/v1/fullscreen/shader-data `
  -Headers $headers
```

A shader can use the sequence to process an update once:

```glsl
if (u_QuickPlayerSequence != lastProcessedSequence) {
    lastProcessedSequence = u_QuickPlayerSequence;
    processExternalData(u_QuickPlayerData);
}
```

The endpoint is a latest-value register, not a queue. If several updates are
sent between rendered frames, the shader may observe only the newest one. For
guaranteed command delivery, wait for the shader to return the observed
sequence through an acknowledgment channel before sending the next command.

### External shader texture

Fullscreen shaders can display up to 16 independently updated persistent
images. Select one with the optional `name` query parameter. For example,
`name=scoreboard` deterministically creates these uniforms:

```glsl
uniform sampler2D u_QuickPlayerTexture_scoreboard;
uniform vec2 u_QuickPlayerTextureSize_scoreboard;
uniform float u_QuickPlayerTextureSequence_scoreboard;
```

Names are case-sensitive, contain 1-32 ASCII letters, digits, or underscores,
and must start with a letter. If `name` is omitted or empty, it defaults to
`texture1`.

Upload a PNG or JPEG directly. Its dimensions are read from the image:

```powershell
Invoke-RestMethod `
  "http://127.0.0.1:17871/api/v1/fullscreen/shader-texture?name=scoreboard" `
  -Method Put -Headers $headers -ContentType image/png `
  -InFile "C:\path\scoreboard.png"
```

The maximum decoded size is 1024 by 1024 pixels. PNG alpha is preserved. The
response describes the persistent RGBA8 texture and its new sequence:

```json
{
  "name": "scoreboard",
  "sequence": 1,
  "width": 320,
  "height": 180,
  "format": "rgba8",
  "byteLength": 230400,
  "uniforms": {
    "sampler": "u_QuickPlayerTexture_scoreboard",
    "size": "u_QuickPlayerTextureSize_scoreboard",
    "sequence": "u_QuickPlayerTextureSequence_scoreboard"
  }
}
```

Raw RGBA8 bytes are also accepted. Supply the dimensions in the query string;
pixels are row-major from the top-left, with four bytes per pixel in red,
green, blue, alpha order:

```powershell
Invoke-RestMethod `
  "http://127.0.0.1:17871/api/v1/fullscreen/shader-texture?name=scoreboard&width=320&height=180" `
  -Method Put -Headers $headers -ContentType application/octet-stream `
  -Body $rgbaBytes
```

Sample the uploaded image normally in GLSL. QuickPlayer converts input rows to
OpenGL's orientation, so `(0, 0)` is the image's lower-left in the shader:

```glsl
vec4 scene = texture2D(texture0, uv);
vec4 image = texture2D(u_QuickPlayerTexture_scoreboard, uv);
gl_FragColor = mix(scene, image, image.a);
```

Each successful upload increments that name's independent sequence, including
when identical pixels are uploaded. The sequence uses the same exactly
representable `0` through `16777215` range as the float data channel. Read one
name's current width, height, byte length, sequence, and generated uniform names
without returning the pixel body or changing state with:

```powershell
Invoke-RestMethod `
  "http://127.0.0.1:17871/api/v1/fullscreen/shader-texture?name=scoreboard" `
  -Headers $headers
```

The REST thread retains only the latest image for each name. The bridge creates
one OpenGL texture per name and active rendering context, and performs creation
or `glTexSubImage2D` updates on that context's render thread. Shaders that do
not declare a generated sampler uniform are unaffected. Image updates are intended for
occasional or low-rate graphics rather than full-frame-rate video.

#### Shared-memory texture channel

Local applications that publish large or frequent frames can retain the REST
endpoint for occasional uploads and use a named shared-memory channel as an
alternative transport. Discover the channel after authenticating normally:

```powershell
$channel = Invoke-RestMethod `
  "http://127.0.0.1:17871/api/v1/fullscreen/shader-texture/shared-memory?name=scoreboard" `
  -Headers $headers
```

Each texture name receives its own channel. The response includes `name`, the
generated uniform names, random process-lifetime `mappingName` and `eventName`
values, and all sizes and offsets needed to open the Windows memory-mapped file.
Each mapping has a 64-byte header followed by two 4 MiB RGBA8 slots and supports
dimensions from 1 through 1024 independently on every frame.

The channel is a single-producer, latest-frame register. Publish a frame in
this order:

1. Choose the other slot and write an odd value to `publishedGeneration` to
   mark an update in progress.
2. Write top-left-origin RGBA8 pixels at
   `pixels + publishedSlot * slotCapacity`.
3. Write `publishedSlot`, `width`, `height`, `byteLength`, and the optional
   `producerSequence`.
4. Issue a memory barrier, write the next even nonzero generation, then signal
   the named auto-reset event.

QuickPlayer copies a consistent frame, converts its row orientation, and sends
only the newest available generation to the existing shader texture bridge.
After the bridge accepts it, QuickPlayer writes that even generation to
`consumedGeneration`. A producer can observe this field for acknowledgements
and latency measurement. Event signals and intermediate frames may coalesce
when the producer is faster than the consumer; commands requiring guaranteed
delivery should continue to use a separate acknowledged channel.

Mapping and event names change whenever QuickPlayer restarts. Call the discovery
endpoint again after reconnecting. The mapping is local to the current Windows
session and disappears when QuickPlayer closes.

### Native iStripper queue

Read iStripper's native fullscreen queue, insert an entry at its start, add
one at its end, or remove an entry by its current index:

```powershell
$body = @{
  cardName = "Nikki Hill - A Dress To Seduce"
  clipNumber = 11
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/queue `
  -Headers $headers

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/queue/insert `
  -Method Post -Headers $headers -ContentType application/json -Body $body

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/queue/add `
  -Method Post -Headers $headers -ContentType application/json -Body $body

Invoke-RestMethod http://127.0.0.1:17871/api/v1/fullscreen/queue/0 `
  -Method Delete -Headers $headers
```

Insert and add accept the same `cardTag`/`cardName` and
`clipName`/`clipNumber` combinations as the play endpoints. `GET` returns
`count` and a `queue` array containing each entry's `index`, `cardTag`,
`clipName`, `cardName`, `model`, and `outfit`.

Remove an entry with the zero-based `index` returned by `GET`. Indexes can
change whenever iStripper consumes or modifies the queue. Clear every native
queue entry with:

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

In API-only mode, `next-card` calls iStripper's native desktop next-card
operation. With no explicit manual QuickPlayer entry waiting, iStripper's own
queue chooses the card; QuickPlayer does not choose a random card or write
`ForceAnim`.

`now-playing-info` returns `409 Conflict` in API-only mode because it requires
displaying an overlay. The other actions use their direct headless playback
equivalents.

Mutation actions return `202 Accepted`. API-only seek actions wait for the
playback operation and return `409 Conflict` instead if it cannot complete.

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
