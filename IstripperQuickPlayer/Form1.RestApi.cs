using IStripperQuickPlayer.DataModel;
using IStripperQuickPlayer.Interop;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer
{
    public partial class Form1
    {
        private const int RestApiMaxBodyLength = 16 * 1024;
        private static readonly JsonSerializerOptions RestApiJson =
            new(JsonSerializerDefaults.Web);
        private HttpListener? restApiListener;
        private CancellationTokenSource? restApiLifetime;
        private string restApiToken = "";

        private sealed record ApiResult(int StatusCode, object Body);
        private sealed record ApiQueueEntryRequest(
            string? CardTag, string? CardName, string? ClipName,
            int? ClipNumber, int? Index);
        private sealed record ApiQueueMoveRequest(int? Index);
        private sealed record ApiQueueSettingsRequest(bool? Enabled);
        private sealed record ApiSeekRequest(
            int? ElapsedMilliseconds, double? Position);
        private sealed record ApiSpeedRequest(double? Speed);
        private sealed record ApiShaderDataRequest(double[]? Values);
        private sealed record FullscreenBridgeSlot(
            int Id, string Source, string Card, string Clip);
        private sealed record FullscreenBridgeQueueEntry(
            string Card, string Clip);
        private sealed record FullscreenBridgeSnapshot(
            string[] Clips, FullscreenBridgeQueueEntry[] Queue,
            FullscreenBridgeSlot[] Slots);
        private FullscreenBridgeSnapshot fullscreenBridgeState =
            new([], [], []);
        private string fullscreenTreeJson = "[]";
        private sealed class ApiRequestException(
            int statusCode, string message) : Exception(message)
        {
            public int StatusCode { get; } = statusCode;
        }

        private static string RestApiTokenPath => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "api-token.txt");

        private void StartRestApi()
        {
#if DEBUG
            Debug.Assert(ApiPathSegments(
                "/api/v1/queue/manual/2").SequenceEqual(
                ["api", "v1", "queue", "manual", "2"]));
            Debug.Assert(IsApiPlaybackSpeedAllowed(0.25) &&
                IsApiPlaybackSpeedAllowed(4) &&
                !IsApiPlaybackSpeedAllowed(0.3));
            ModelCard apiNameCheck = new()
            {
                name = "f0001",
                modelName = "Anna Example",
                outfit = "Pool Side"
            };
            Debug.Assert(ApiCardNameMatches(
                apiNameCheck, "Anna Example - Pool Side"));
            Debug.Assert(ApiCardNameMatches(
                apiNameCheck, "Pool Side"));
            Debug.Assert(!ApiCardNameMatches(
                apiNameCheck, "Another Show"));
#endif
            try
            {
                restApiToken = LoadOrCreateRestApiToken();
                restApiLifetime = new CancellationTokenSource();
                restApiListener = new HttpListener();
                restApiListener.Prefixes.Add(
                    $"http://127.0.0.1:{restApiPort}/");
                restApiListener.Start();
                _ = Task.Run(() => RunRestApiAsync(
                    restApiListener, restApiLifetime.Token));
                Debug.WriteLine(
                    $"REST API listening on http://127.0.0.1:{restApiPort}/");
            }
            catch (Exception exception)
            {
                restApiListener?.Close();
                restApiListener = null;
                restApiLifetime?.Dispose();
                restApiLifetime = null;
                Debug.WriteLine("REST API could not start: " +
                    exception.Message);
                if (apiOnlyNotifyIcon != null)
                {
                    apiOnlyNotifyIcon.Text =
                        $"QuickPlayer API unavailable on port {restApiPort}";
                    apiOnlyNotifyIcon.ShowBalloonTip(5_000,
                        "QuickPlayer API could not start",
                        $"Port {restApiPort} is unavailable. Quit and choose another port.",
                        ToolTipIcon.Error);
                }
                else if (!apiOnlyMode)
                {
                    MessageBox.Show(this,
                        $"The REST API could not start on port {restApiPort}.\r\n" +
                        exception.Message,
                        "QuickPlayer REST API", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void StopRestApi()
        {
            restApiLifetime?.Cancel();
            restApiListener?.Close();
            restApiListener = null;
            restApiLifetime?.Dispose();
            restApiLifetime = null;
        }

        private static string LoadOrCreateRestApiToken()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                RestApiTokenPath)!);
            if (File.Exists(RestApiTokenPath))
            {
                string existing = File.ReadAllText(
                    RestApiTokenPath).Trim();
                if (existing.Length >= 32)
                    return existing;
            }

            string token = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(RestApiTokenPath, token);
            return token;
        }

        private async Task RunRestApiAsync(HttpListener listener,
            CancellationToken cancellationToken)
        {
            // ponytail: requests are serialized because every mutation
            // ultimately runs on the single WinForms UI thread.
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext context = await listener
                        .GetContextAsync().WaitAsync(cancellationToken);
                    await HandleRestApiRequestAsync(context,
                        cancellationToken);
                }
            }
            catch (Exception exception) when (
                cancellationToken.IsCancellationRequested ||
                exception is HttpListenerException or ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine("REST API stopped: " + exception);
            }
        }

        private async Task HandleRestApiRequestAsync(
            HttpListenerContext context,
            CancellationToken cancellationToken)
        {
            ApiResult result;
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "/";
                if (context.Request.HttpMethod == "GET" &&
                    path.Equals("/health",
                        StringComparison.OrdinalIgnoreCase))
                {
                    result = Ok(new { status = "ok", apiVersion = 1 });
                }
                else if (!IsRestApiAuthorized(context.Request))
                {
                    result = Error(401,
                        "Supply the API token as Authorization: Bearer <token>.");
                    context.Response.Headers[
                        HttpResponseHeader.WwwAuthenticate] = "Bearer";
                }
                else
                {
                    result = await RouteRestApiRequestAsync(
                        context.Request, path, cancellationToken);
                }
            }
            catch (ApiRequestException exception)
            {
                result = Error(exception.StatusCode, exception.Message);
            }
            catch (JsonException)
            {
                result = Error(400, "The request body is not valid JSON.");
            }
            catch (Exception exception)
            {
                Debug.WriteLine("REST API request failed: " + exception);
                result = Error(500, "The request could not be completed.");
            }

            await WriteRestApiResponseAsync(context.Response, result,
                cancellationToken);
        }

        private async Task<ApiResult> RouteRestApiRequestAsync(
            HttpListenerRequest request, string path,
            CancellationToken cancellationToken)
        {
            string method = request.HttpMethod;
            string[] parts = ApiPathSegments(path);
            if (parts.Length < 2 ||
                !parts[0].Equals("api",
                    StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("v1",
                    StringComparison.OrdinalIgnoreCase))
                return Error(404, "Endpoint not found.");

            if (method == "GET" && parts.Length == 2)
            {
                return Ok(new
                {
                    apiVersion = 1,
                    endpoints = new[]
                    {
                        "GET /api/v1/status",
                        "GET /api/v1/library?search=&limit=",
                        "GET /api/v1/queue",
                        "GET /api/v1/fullscreen",
                        "GET /api/v1/fullscreen/shader-data",
                        "PUT /api/v1/fullscreen/shader-data",
                        "POST /api/v1/fullscreen/play",
                        "POST /api/v1/fullscreen/next",
                        "POST /api/v1/fullscreen/slots/{slotId}/next",
                        "POST /api/v1/fullscreen/slots/{slotId}/play",
                        "POST /api/v1/fullscreen/source/{source}/next",
                        "POST /api/v1/fullscreen/source/{source}/play",
                        "GET /api/v1/fullscreen/queue",
                        "POST /api/v1/fullscreen/queue/insert",
                        "POST /api/v1/fullscreen/queue/add",
                        "DELETE /api/v1/fullscreen/queue",
                        "DELETE /api/v1/fullscreen/queue/{index}",
                        "PATCH /api/v1/queue",
                        "POST /api/v1/queue/manual",
                        "DELETE /api/v1/queue/manual",
                        "DELETE /api/v1/queue/manual/{index}",
                        "PATCH /api/v1/queue/manual/{index}",
                        "POST /api/v1/queue/automatic/rebuild",
                        "POST /api/v1/play",
                        "POST /api/v1/playback/seek",
                        "POST /api/v1/playback/speed",
                        "POST /api/v1/actions/{action}"
                    },
                    actions = ApiActions
                });
            }

            if (method == "GET" && Matches(parts, "status"))
                return Ok(await InvokeAsync(CreateRestApiStatus));

            if (method == "GET" && Matches(parts, "library"))
            {
                string search = request.QueryString["search"]?.Trim() ?? "";
                int limit = int.TryParse(request.QueryString["limit"],
                    out int parsedLimit)
                    ? Math.Clamp(parsedLimit, 1, 1_000) : 100;
                return Ok(await InvokeAsync(
                    () => CreateRestApiLibrary(search, limit)));
            }

            if (method == "GET" && Matches(parts, "queue"))
                return Ok(await InvokeAsync(CreateRestApiQueue));

            if (method == "GET" && Matches(parts, "fullscreen"))
                return Ok(await InvokeAsync(CreateRestApiFullscreen));

            if (method == "GET" &&
                Matches(parts, "fullscreen", "shader-data"))
            {
                return await InvokeAsync(GetRestApiFullscreenShaderData);
            }

            if (method == "PUT" &&
                Matches(parts, "fullscreen", "shader-data"))
            {
                ApiShaderDataRequest body =
                    await ReadRestApiJsonAsync<ApiShaderDataRequest>(
                        request, cancellationToken);
                return await InvokeAsync(() =>
                    SetRestApiFullscreenShaderData(body));
            }

            if (method == "GET" &&
                Matches(parts, "fullscreen", "queue"))
            {
                return await InvokeAsync(GetRestApiFullscreenQueue);
            }

            if (method == "POST" &&
                Matches(parts, "fullscreen", "play"))
            {
                ApiQueueEntryRequest body =
                    await ReadRestApiJsonAsync<ApiQueueEntryRequest>(
                        request, cancellationToken);
                return await InvokeAsync(() =>
                    PlayRestApiFullscreenEntry(body));
            }

            if (method == "POST" &&
                Matches(parts, "fullscreen", "next"))
            {
                return await InvokeAsync(() =>
                    RunFullscreenBridgeAction("IStripperFullscreenNext"));
            }

            if (method == "POST" &&
                Matches(parts, "fullscreen", "debug-tree"))
            {
                return await InvokeAsync(DumpRestApiFullscreenTree);
            }

            if (method == "POST" && parts.Length == 6 &&
                parts[2].Equals("fullscreen",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[3].Equals("slots",
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[4], out int fullscreenSlotId) &&
                parts[5].Equals("next",
                    StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeAsync(() =>
                    RunFullscreenSlotAction(fullscreenSlotId));
            }

            if (method == "POST" && parts.Length == 6 &&
                parts[2].Equals("fullscreen",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[3].Equals("slots",
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[4], out int playFullscreenSlotId) &&
                parts[5].Equals("play",
                    StringComparison.OrdinalIgnoreCase))
            {
                ApiQueueEntryRequest body =
                    await ReadRestApiJsonAsync<ApiQueueEntryRequest>(
                        request, cancellationToken);
                return await InvokeAsync(() =>
                    PlayRestApiFullscreenSlot(playFullscreenSlotId, body));
            }

            if (method == "POST" && parts.Length == 6 &&
                parts[2].Equals("fullscreen",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[3].Equals("source",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[5].Equals("next",
                    StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeAsync(() =>
                    RunFullscreenSourceAction(parts[4],
                        RunFullscreenSlotAction));
            }

            if (method == "POST" && parts.Length == 6 &&
                parts[2].Equals("fullscreen",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[3].Equals("source",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[5].Equals("play",
                    StringComparison.OrdinalIgnoreCase))
            {
                ApiQueueEntryRequest body =
                    await ReadRestApiJsonAsync<ApiQueueEntryRequest>(
                        request, cancellationToken);
                return await InvokeAsync(() =>
                    RunFullscreenSourceAction(parts[4], slotId =>
                        PlayRestApiFullscreenSlot(slotId, body)));
            }

            if (method == "DELETE" &&
                Matches(parts, "fullscreen", "queue"))
            {
                return await InvokeAsync(() =>
                    RunFullscreenBridgeAction(
                        "IStripperFullscreenClearQueue"));
            }

            if (method == "POST" &&
                (Matches(parts, "fullscreen", "queue", "insert") ||
                 Matches(parts, "fullscreen", "queue", "add")))
            {
                ApiQueueEntryRequest body =
                    await ReadRestApiJsonAsync<ApiQueueEntryRequest>(
                        request, cancellationToken);
                bool prepend = parts[4].Equals("insert",
                    StringComparison.OrdinalIgnoreCase);
                return await InvokeAsync(() =>
                    AddRestApiFullscreenQueueEntry(body, prepend));
            }

            if (method == "DELETE" && parts.Length == 5 &&
                parts[2].Equals("fullscreen",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[3].Equals("queue",
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[4], out int fullscreenQueueIndex))
            {
                return await InvokeAsync(() =>
                    RemoveRestApiFullscreenQueueEntry(fullscreenQueueIndex));
            }

            if (method == "PATCH" && Matches(parts, "queue"))
            {
                ApiQueueSettingsRequest body =
                    await ReadRestApiJsonAsync<ApiQueueSettingsRequest>(
                        request, cancellationToken);
                if (body.Enabled == null)
                    return Error(400, "'enabled' is required.");
                return await InvokeAsync(() =>
                {
                    Properties.Settings.Default.EnablePlayQueue =
                        body.Enabled.Value;
                    if (!apiOnlyMode)
                    {
                        enablePlayQueueToolStripMenuItem.Checked =
                            body.Enabled.Value;
                        RefreshPlayQueueVisibility();
                        RebuildAutomaticQueue();
                    }
                    return Ok(CreateRestApiQueue());
                });
            }

            if (method == "POST" &&
                Matches(parts, "queue", "manual"))
            {
                ApiQueueEntryRequest body =
                    await ReadRestApiJsonAsync<ApiQueueEntryRequest>(
                        request, cancellationToken);
                return await InvokeAsync(() => AddRestApiQueueEntry(body));
            }

            if (method == "DELETE" &&
                Matches(parts, "queue", "manual"))
            {
                return await InvokeAsync(() =>
                {
                    ClearManualQueue();
                    return Ok(CreateRestApiQueue());
                });
            }

            if (parts.Length == 5 &&
                parts[2].Equals("queue",
                    StringComparison.OrdinalIgnoreCase) &&
                parts[3].Equals("manual",
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[4], out int queueIndex))
            {
                if (method == "DELETE")
                    return await InvokeAsync(
                        () => RemoveRestApiQueueEntry(queueIndex));
                if (method == "PATCH")
                {
                    ApiQueueMoveRequest body =
                        await ReadRestApiJsonAsync<ApiQueueMoveRequest>(
                            request, cancellationToken);
                    return await InvokeAsync(
                        () => MoveRestApiQueueEntry(queueIndex, body));
                }
            }

            if (method == "POST" &&
                Matches(parts, "queue", "automatic", "rebuild"))
            {
                if (apiOnlyMode)
                    return Error(409,
                        "Automatic queueing is unavailable in API-only mode.");
                return await InvokeAsync(() =>
                {
                    RebuildAutomaticQueue();
                    return Ok(CreateRestApiQueue());
                });
            }

            if (method == "POST" && Matches(parts, "play"))
            {
                ApiQueueEntryRequest body =
                    await ReadRestApiJsonAsync<ApiQueueEntryRequest>(
                        request, cancellationToken);
                return await InvokeAsync(() => PlayRestApiEntry(body));
            }

            if (method == "POST" &&
                Matches(parts, "playback", "seek"))
            {
                ApiSeekRequest body =
                    await ReadRestApiJsonAsync<ApiSeekRequest>(
                        request, cancellationToken);
                return await InvokeAsync(
                    async _ => await SeekRestApiPlaybackAsync(body),
                    cancellationToken);
            }

            if (method == "POST" &&
                Matches(parts, "playback", "speed"))
            {
                ApiSpeedRequest body =
                    await ReadRestApiJsonAsync<ApiSpeedRequest>(
                        request, cancellationToken);
                return await InvokeAsync(
                    async _ => await SetRestApiPlaybackSpeedAsync(body),
                    cancellationToken);
            }

            if (method == "POST" && parts.Length == 4 &&
                parts[2].Equals("actions",
                    StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeAsync(
                    async _ => await ExecuteRestApiActionAsync(parts[3]),
                    cancellationToken);
            }

            return Error(404, "Endpoint not found.");
        }

        private object CreateRestApiStatus()
        {
            RefreshApiOnlyPlaybackState();
            string animationPath = GetCurrentAnimationPath();
            string[] animationParts = animationPath.Split('\\', 2);
            string cardTag = string.IsNullOrEmpty(animationPath) ? "" :
                GetCardTagFromAnimationPath(animationPath);
            string clipName = animationParts.Length > 1
                ? animationParts[1]
                : animationPath.StartsWith("custom:",
                    StringComparison.OrdinalIgnoreCase) ? animationPath : "";
            ModelCard? card = string.IsNullOrEmpty(cardTag)
                ? null : Datastore.findCardByTag(
                    cardTag.Split('-')[0]);
            int stateCode = playbackBridgeLoaded &&
                playbackMovieRegistered
                    ? CallPlaybackBridgeApi("IStripperGetState") : -1;
            int elapsedMilliseconds =
                playbackLastKnownElapsedMilliseconds;
            int durationMilliseconds =
                playbackTimelineDurationMilliseconds;
            if (customPlayer != null)
            {
                elapsedMilliseconds = (int)Math.Min(int.MaxValue,
                    customPlayer.CurrentSeconds * 1000);
                durationMilliseconds = (int)Math.Min(int.MaxValue,
                    customPlayer.DurationSeconds * 1000);
            }
            if (customPlayer == null && apiOnlyMode && playbackBridgeLoaded &&
                playbackMovieRegistered)
            {
                int elapsed = CallPlaybackBridgeApi(
                    "IStripperGetElapsedMilliseconds");
                int duration = CallPlaybackBridgeApi(
                    "IStripperGetTotalMilliseconds");
                if (elapsed >= 0)
                    elapsedMilliseconds = elapsed;
                if (duration >= 0)
                    durationMilliseconds = duration;
                playbackLastKnownElapsedMilliseconds =
                    elapsedMilliseconds;
                playbackTimelineDurationMilliseconds =
                    durationMilliseconds;
            }
            string state = stateCode switch
            {
                _ when string.IsNullOrEmpty(animationPath) => "stopped",
                _ when customPlayer != null && customPlayer.Paused => "paused",
                _ when customPlayer != null => "playing",
                3 => "playing",
                4 => "paused",
                _ => "unavailable"
            };

            return new
            {
                apiVersion = 1,
                playing = new
                {
                    state,
                    animationPath,
                    source = card?.IsCustom == true ? "custom" : "istripper",
                    showId = card?.customShowId,
                    cardTag,
                    cardName = card == null
                        ? null : ApiCardDisplayName(card),
                    clipName,
                    model = card?.modelName,
                    outfit = card?.outfit,
                    elapsedMilliseconds,
                    durationMilliseconds,
                    speed = requestedPlaybackSpeed
                },
                player = new
                {
                    locked = playerlocked,
                    panic = panicActive,
                    playbackControlsAvailable =
                        customPlayer != null || PlaybackControlEnabled &&
                        playbackControlsAvailableForAccount &&
                        playbackBridgeLoaded,
                    seekReady = playbackSeekReady
                },
                queue = CreateRestApiQueue()
            };
        }

        private bool OnBridgeEvent(string name, byte[] data)
        {
            if (name == "FullscreenTree")
            {
                Volatile.Write(ref fullscreenTreeJson,
                    Encoding.UTF8.GetString(data));
                return false;
            }
            if (name != "FullscreenState")
                return OnRegistryValueWrite(name, data);
            try
            {
                FullscreenBridgeSnapshot? state = JsonSerializer.Deserialize<
                    FullscreenBridgeSnapshot>(data, RestApiJson);
                if (state != null)
                    Volatile.Write(ref fullscreenBridgeState, state);
            }
            catch (JsonException) { }
            return false;
        }

        private ApiResult DumpRestApiFullscreenTree()
        {
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");
            Volatile.Write(ref fullscreenTreeJson, "[]");
            int result = playbackBridgeClient.Call(
                "IStripperDumpFullscreenTree");
            if (result < 0)
                return Error(409,
                    $"The fullscreen tree is unavailable (0x{result:X8}).");
            Stopwatch wait = Stopwatch.StartNew();
            while (Volatile.Read(ref fullscreenTreeJson) == "[]" &&
                wait.ElapsedMilliseconds < 500)
                Thread.Sleep(10);
            JsonElement nodes = JsonSerializer.Deserialize<JsonElement>(
                Volatile.Read(ref fullscreenTreeJson));
            return new ApiResult(200, new { nodes });
        }

        private object CreateRestApiFullscreen()
        {
            FullscreenBridgeSnapshot state = Volatile.Read(
                ref fullscreenBridgeState);
            FullscreenBridgeSlot[] logicalSlots = state.Slots;
            return new
            {
                active = ReadRegistryInteger(
                    @"Software\Totem\vghd\player", "playingMode") == 3,
                clips = logicalSlots.Select(slot =>
                {
                    string clipName = slot.Clip;
                    string cardTag = string.IsNullOrWhiteSpace(slot.Card)
                        ? clipName.Split('_')[0] : slot.Card;
                    ModelCard? card = Datastore.findCardByTag(cardTag);
                    return new
                    {
                        slotId = slot.Id,
                        source = slot.Source,
                        clipName = clipName.Contains('_')
                            ? clipName : null,
                        cardTag,
                        cardName = card == null ? null :
                            ApiCardDisplayName(card),
                        model = card?.modelName,
                        outfit = card?.outfit
                    };
                }).ToList(),
                slots = logicalSlots.Select(slot =>
                {
                    string clipName = slot.Clip;
                    string cardTag = string.IsNullOrWhiteSpace(slot.Card)
                        ? clipName.Split('_')[0] : slot.Card;
                    return new
                    {
                        slotId = slot.Id,
                        source = slot.Source,
                        clipName = clipName.Contains('_') ? clipName : null,
                        cardTag
                    };
                }).ToList(),
                queue = CreateRestApiFullscreenQueueEntries(state)
            };
        }

        private ApiResult GetRestApiFullscreenShaderData()
        {
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");
            int result = playbackBridgeClient.GetFullscreenShaderData(
                out float[] values, out uint sequence);
            return result < 0
                ? Error(409, "Fullscreen shader data is unavailable " +
                    $"(0x{result:X8}).")
                : new ApiResult(200, new { sequence, values });
        }

        private ApiResult SetRestApiFullscreenShaderData(
            ApiShaderDataRequest request)
        {
            if (request.Values is not { Length: 4 })
                return Error(400, "'values' must contain exactly four numbers.");
            if (request.Values.Any(value => !double.IsFinite(value) ||
                    value < -float.MaxValue || value > float.MaxValue))
                return Error(400, "Every shader value must be a finite " +
                    "32-bit floating-point number.");
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");

            float[] values = request.Values.Select(value => (float)value)
                .ToArray();
            int result = playbackBridgeClient.SetFullscreenShaderData(
                values, out uint sequence);
            return result < 0
                ? Error(409, "Fullscreen shader data could not be updated " +
                    $"(0x{result:X8}).")
                : new ApiResult(200, new { sequence, values });
        }

        private object CreateRestApiFullscreenQueue()
        {
            FullscreenBridgeSnapshot state = Volatile.Read(
                ref fullscreenBridgeState);
            object[] queue = CreateRestApiFullscreenQueueEntries(state);
            return new { count = queue.Length, queue };
        }

        private ApiResult GetRestApiFullscreenQueue()
        {
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");
            int result = playbackBridgeClient.Call(
                "IStripperRefreshFullscreenState");
            return result < 0
                ? Error(409, "The fullscreen queue is unavailable " +
                    $"(0x{result:X8}).")
                : new ApiResult(200, CreateRestApiFullscreenQueue());
        }

        private static object[] CreateRestApiFullscreenQueueEntries(
            FullscreenBridgeSnapshot state) => state.Queue.Select(
            (entry, index) =>
            {
                ModelCard? card = Datastore.findCardByTag(entry.Card);
                return new
                {
                    index,
                    cardTag = entry.Card,
                    clipName = string.IsNullOrWhiteSpace(entry.Clip)
                        ? null : Path.GetFileName(entry.Clip),
                    cardName = card == null ? null : ApiCardDisplayName(card),
                    model = card?.modelName,
                    outfit = card?.outfit
                };
            }).Cast<object>().ToArray();

        private ApiResult RunFullscreenBridgeAction(string action)
        {
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");
            int result = playbackBridgeClient.Call(action);
            if (result < 0)
                return Error(409,
                    $"The fullscreen operation is unavailable (0x{result:X8}).");
            playbackBridgeClient.Call("IStripperRefreshFullscreenState");
            return new ApiResult(202, CreateRestApiFullscreen());
        }

        private ApiResult AddRestApiFullscreenQueueEntry(
            ApiQueueEntryRequest request, bool prepend)
        {
            if (!TryCreateRestApiQueueEntry(request,
                    out PlayQueueEntry? entry, out ApiResult? error))
                return error!;
            if (!TryResolveQueueEntry(entry!, out string animationPath))
                return Error(409, "No playable clip matches the request.");
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");

            string clipName = Path.GetFileName(animationPath);
            int result = playbackBridgeClient.CallUtf8(prepend
                    ? "IStripperFullscreenQueueInsert"
                    : "IStripperFullscreenQueueAdd",
                $"{entry!.CardTag}\n{clipName}");
            if (result < 0)
                return Error(409, "The fullscreen queue entry could not be " +
                    $"added (0x{result:X8}).");
            playbackBridgeClient.Call("IStripperRefreshFullscreenState");
            return new ApiResult(201, new
            {
                accepted = true,
                position = prepend ? "start" : "end",
                cardTag = entry.CardTag,
                clipName
            });
        }

        private ApiResult RemoveRestApiFullscreenQueueEntry(int index)
        {
            if (index < 0)
                return Error(400, "The fullscreen queue index is invalid.");
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");
            int result = playbackBridgeClient.Call(
                "IStripperFullscreenQueueRemove", (ulong)index);
            if (result < 0)
            {
                int status = result == unchecked((int)0x80070490) ? 404 : 409;
                return Error(status, "The fullscreen queue entry could not " +
                    $"be removed (0x{result:X8}).");
            }
            playbackBridgeClient.Call("IStripperRefreshFullscreenState");
            return new ApiResult(202, new { accepted = true, index });
        }

        private ApiResult RunFullscreenSlotAction(int slotId)
        {
            if (slotId <= 0)
                return Error(400, "The fullscreen slot ID is invalid.");
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");
            int result = playbackBridgeClient.Call(
                "IStripperFullscreenNextSlot", (ulong)slotId);
            if (result < 0)
                return Error(404, "The fullscreen slot is no longer active.");
            return new ApiResult(202, new { accepted = true, slotId });
        }

        private ApiResult RunFullscreenSourceAction(string source,
            Func<int, ApiResult> action)
        {
            source = Uri.UnescapeDataString(source).Trim();
            if (source.Length == 0)
                return Error(400, "The fullscreen source is invalid.");
            FullscreenBridgeSlot? slot = Volatile.Read(
                    ref fullscreenBridgeState).Slots
                .FirstOrDefault(slot => slot.Source.Equals(source,
                    StringComparison.OrdinalIgnoreCase));
            return slot == null
                ? Error(404, "The fullscreen source is no longer active.")
                : action(slot.Id);
        }

        private ApiResult PlayRestApiFullscreenEntry(
            ApiQueueEntryRequest request)
        {
            if (!TryCreateRestApiQueueEntry(request,
                    out PlayQueueEntry? entry, out ApiResult? error))
                return error!;
            if (!TryResolveQueueEntry(entry!, out string animationPath))
                return Error(409, "No playable clip matches the request.");
            if (animationPath.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                if (!RequestAnimationPlayback(animationPath))
                    return Error(409, "The custom show could not be started.");
                actSetPlayerLarge(true);
                return new ApiResult(202, new { accepted = true, animationPath });
            }
            if (ReadRegistryInteger(@"Software\Totem\vghd\player",
                    "playingMode") != 3)
                return Error(409, "Fullscreen is not active.");
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Totem\vghd\parameters", true);
            if (key == null)
                return Error(503, "iStripper is not available.");
            key.SetValue("ForceAnimFs", animationPath);

            PlaybackBridgeClient client = playbackBridgeClient;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1_000);
                    client.Call("IStripperFullscreenNext");
                }
                catch { }
            });
            return new ApiResult(202, new
            {
                accepted = true,
                animationPath
            });
        }

        private ApiResult PlayRestApiFullscreenSlot(int slotId,
            ApiQueueEntryRequest request)
        {
            if (slotId <= 0)
                return Error(400, "The fullscreen slot ID is invalid.");
            if (!TryCreateRestApiQueueEntry(request,
                    out PlayQueueEntry? entry, out ApiResult? error))
                return error!;
            if (!TryResolveQueueEntry(entry!, out string animationPath))
                return Error(409, "No playable clip matches the request.");
            if (ReadRegistryInteger(@"Software\Totem\vghd\player",
                    "playingMode") != 3)
                return Error(409, "Fullscreen is not active.");
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");

            string clipName = Path.GetFileName(animationPath);
            int result = playbackBridgeClient.CallUtf8(
                "IStripperFullscreenPlaySlot",
                $"{slotId}\n{entry!.CardTag}\n{clipName}");
            if (result < 0)
                return Error(result == unchecked((int)0x80070490) ? 404 : 409,
                    "The fullscreen slot could not be replaced " +
                    $"(0x{result:X8}).");
            return new ApiResult(202, new
            {
                accepted = true,
                slotId,
                cardTag = entry.CardTag,
                clipName
            });
        }

        private object CreateRestApiLibrary(string search, int limit)
        {
            IEnumerable<ModelCard> query = Datastore.modelcards ?? [];
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(card =>
                    (card.name + " " + card.modelName + " " +
                     card.outfit).Contains(search,
                        StringComparison.OrdinalIgnoreCase));
            }

            List<ModelCard> matches = query.ToList();
            return new
            {
                total = matches.Count,
                count = Math.Min(matches.Count, limit),
                cards = matches.Take(limit).Select(card => new
                {
                    source = card.IsCustom ? "custom" : "istripper",
                    showId = card.customShowId,
                    cardTag = ApiCardTag(card),
                    cardName = ApiCardDisplayName(card),
                    model = card.modelName,
                    outfit = card.outfit,
                    description = card.description,
                    tags = card.tags,
                    releaseDate = card.dateReleased == default ? null :
                        card.dateReleased.ToString("yyyy-MM-dd"),
                    showDate = card.dateShow == default ? null :
                        card.dateShow.ToString("yyyy-MM-dd"),
                    age = card.modelAge,
                    rating = card.rating is > 0 ? card.rating - 5 : null,
                    hotness = card.hotnessLevel,
                    exclusive = card.exclusive,
                    performerCount = card.numgirls,
                    bust = card.bust,
                    waist = card.waist,
                    hips = card.hips,
                    height = card.height,
                    hair = card.hair,
                    ethnicity = card.ethnicity,
                    city = card.city,
                    country = card.country,
                    clips = (card.clips ?? []).Select(clip => new
                    {
                        clipName = clip.clipName,
                        clipNumber = clip.clipNumber,
                        type = clip.clipType,
                        hotness = clip.hotnessCode?.ToString(),
                        sizeBytes = clip.size,
                        enabled = clip.isEnabled
                    })
                })
            };
        }

        private object CreateRestApiQueue() => new
        {
            enabled = Properties.Settings.Default.EnablePlayQueue,
            active = (activeManualQueueEntry ??
                (apiOnlyMode ? null : activeAutomaticQueueEntry) ??
                activeQueuedCard) is { } active
                    ? CreateRestApiQueueEntry(active, -1) : null,
            manual = manualPlayQueue.Select(
                CreateRestApiQueueEntry).ToList(),
            automatic = apiOnlyMode
                ? []
                : automaticPlayQueue.Select(
                    CreateRestApiQueueEntry).ToList()
        };

        private static object CreateRestApiQueueEntry(
            PlayQueueEntry entry, int index)
        {
            ModelCard? card = Datastore.findCardByTag(entry.CardTag);
            return new
            {
                index,
                source = card?.IsCustom == true ? "custom" : "istripper",
                showId = card?.customShowId,
                cardTag = entry.CardTag,
                cardName = card == null
                    ? null : ApiCardDisplayName(card),
                clipName = entry.ClipName,
                model = card?.modelName,
                outfit = card?.outfit
            };
        }

        private ApiResult AddRestApiQueueEntry(
            ApiQueueEntryRequest request)
        {
            if (!TryCreateRestApiQueueEntry(request,
                    out PlayQueueEntry? entry, out ApiResult? error))
                return error!;

            int index = Math.Clamp(request.Index ??
                manualPlayQueue.Count, 0, manualPlayQueue.Count);
            manualPlayQueue.Insert(index, entry!);
            automaticPlayQueue.RemoveAll(item => string.Equals(
                item.CardTag, entry!.CardTag,
                StringComparison.OrdinalIgnoreCase));
            SavePreviousQueue();
            FillAutomaticQueue();
            RenderPlayQueues();
            return new ApiResult(201, CreateRestApiQueue());
        }

        private ApiResult RemoveRestApiQueueEntry(int index)
        {
            if (index < 0 || index >= manualPlayQueue.Count)
                return Error(404, "Manual queue entry not found.");
            manualPlayQueue.RemoveAt(index);
            SavePreviousQueue();
            FillAutomaticQueue();
            RenderPlayQueues();
            return Ok(CreateRestApiQueue());
        }

        private ApiResult MoveRestApiQueueEntry(int oldIndex,
            ApiQueueMoveRequest request)
        {
            if (oldIndex < 0 || oldIndex >= manualPlayQueue.Count)
                return Error(404, "Manual queue entry not found.");
            if (request.Index == null)
                return Error(400, "'index' is required.");

            PlayQueueEntry entry = manualPlayQueue[oldIndex];
            manualPlayQueue.RemoveAt(oldIndex);
            manualPlayQueue.Insert(Math.Clamp(request.Index.Value,
                0, manualPlayQueue.Count), entry);
            SavePreviousQueue();
            RenderPlayQueues();
            return Ok(CreateRestApiQueue());
        }

        private ApiResult PlayRestApiEntry(ApiQueueEntryRequest request)
        {
            if (!TryCreateRestApiQueueEntry(request,
                    out PlayQueueEntry? entry, out ApiResult? error))
                return error!;
            if (!TryResolveQueueEntry(entry!,
                    out string animationPath))
                return Error(409, "No playable clip matches the request.");

            ClearQueuedCardSession();
            if (!RequestAnimationPlayback(animationPath))
                return Error(503, animationPath.StartsWith("custom:",
                    StringComparison.OrdinalIgnoreCase)
                    ? "The custom show is unavailable."
                    : "iStripper is not available.");
            if (!apiOnlyMode)
            {
                SelectQueuedCard(entry!.CardTag, animationPath);
                BeginInvoke((Action)TaskbarThumbnail);
            }
            return new ApiResult(202, new
            {
                accepted = true,
                animationPath
            });
        }

        private async Task<ApiResult> SeekRestApiPlaybackAsync(
            ApiSeekRequest request)
        {
            if (!RestApiPlaybackControlAvailable(seek: true,
                    out ApiResult? error))
                return error!;
            if (request.ElapsedMilliseconds == null &&
                request.Position == null)
                return Error(400,
                    "'elapsedMilliseconds' or 'position' is required.");
            if (request.Position is < 0 or > 1)
                return Error(400, "'position' must be between 0 and 1.");

            int target = request.ElapsedMilliseconds ??
                (int)Math.Round(request.Position!.Value *
                    playbackTimelineDurationMilliseconds);
            if (target < 0)
                return Error(400,
                    "'elapsedMilliseconds' cannot be negative.");
            return await RunPlaybackOperationAsync(
                token => SeekAbsoluteAsync(target, token))
                ? Accepted()
                : Error(409, "The seek could not be completed.");
        }

        private async Task<ApiResult> SetRestApiPlaybackSpeedAsync(
            ApiSpeedRequest request)
        {
            if (!RestApiPlaybackControlAvailable(seek: false,
                    out ApiResult? error))
                return error!;
            if (request.Speed == null ||
                !IsApiPlaybackSpeedAllowed(request.Speed.Value))
                return Error(400,
                    "'speed' must be 0.25, 0.5, 1, 1.5, 2, 3 or 4.");
            if (playbackDecoderKind == 2 && request.Speed > 3)
                return Error(409,
                    "Legacy WMV playback is limited to 3x.");

            double speed = request.Speed.Value;
            requestedPlaybackSpeed = speed;
            if (!apiOnlyMode)
            {
                suppressPlaybackSpeedSelection = true;
                cmbPlaybackSpeed.SelectedItem =
                    $"{speed.ToString("0.##",
                        System.Globalization.CultureInfo.InvariantCulture)}x";
                suppressPlaybackSpeedSelection = false;
            }
            return await RunPlaybackOperationAsync(_ =>
            {
                SetPlaybackRate(speed);
                SetPlaybackStatus(
                    $"Playback speed set to {speed:0.##}x.");
                return Task.CompletedTask;
            }) ? Accepted()
                : Error(409, "The playback speed could not be changed.");
        }

        private async Task<ApiResult> ExecuteRestApiActionAsync(string action)
        {
            switch (action.ToLowerInvariant())
            {
                case "next-clip":
                    if (apiOnlyMode)
                        return PlayNextApiOnlyClip();
                    GetNextClip(useQueue: false);
                    break;
                case "next-card":
                    if (apiOnlyMode)
                        return PlayNextApiOnlyCard();
                    GetNextCard();
                    break;
                case "toggle-lock":
                    if (apiOnlyMode)
                        return ToggleApiOnlyPlayerLock();
                    lockPlayerToolStripMenuItem.Checked =
                        !lockPlayerToolStripMenuItem.Checked;
                    setPlayerLocked();
                    break;
                case "play-pause":
                    if (!RestApiPlaybackControlAvailable(false,
                            out ApiResult? playPauseError))
                        return playPauseError!;
                    if (apiOnlyMode)
                    {
                        if (!await RunPlaybackOperationAsync(_ =>
                        {
                            int state = RequirePlaybackResult(
                                "IStripperGetState");
                            RequirePlaybackResult(state == 3
                                ? "IStripperPause"
                                : state == 4
                                    ? "IStripperResume"
                                    : throw new InvalidOperationException(
                                        "There is no controllable video."));
                            return Task.CompletedTask;
                        }, prepareFastDecode: false))
                            return Error(409,
                                "Play/pause could not be completed.");
                    }
                    else
                        cmdPlayPause.PerformClick();
                    break;
                case "back-10-percent":
                    if (!RestApiPlaybackControlAvailable(true,
                            out ApiResult? rewindError))
                        return rewindError!;
                    if (apiOnlyMode)
                    {
                        if (!await RunPlaybackOperationAsync(token =>
                                SeekRelativeAsync(-0.1, token)))
                            return Error(409,
                                "The seek could not be completed.");
                    }
                    else
                        cmdRewind.PerformClick();
                    break;
                case "forward-10-percent":
                    if (!RestApiPlaybackControlAvailable(true,
                            out ApiResult? forwardError))
                        return forwardError!;
                    if (apiOnlyMode)
                    {
                        if (!await RunPlaybackOperationAsync(token =>
                                SeekRelativeAsync(0.1, token)))
                            return Error(409,
                                "The seek could not be completed.");
                    }
                    else
                        cmdFastForward.PerformClick();
                    break;
                case "restart":
                    if (!RestApiPlaybackControlAvailable(true,
                            out ApiResult? restartError))
                        return restartError!;
                    if (apiOnlyMode)
                    {
                        if (!await RunPlaybackOperationAsync(
                                token => SeekAbsoluteAsync(0, token)))
                            return Error(409,
                                "The seek could not be completed.");
                    }
                    else
                        _ = RunPlaybackOperationAsync(
                            token => SeekAbsoluteAsync(0, token));
                    break;
                case "player-large":
                    if (!playerLockBridgeLoaded)
                        return Error(409,
                            "Player sizing is not currently available.");
                    actSetPlayerLarge(true);
                    break;
                case "player-small":
                    if (!playerLockBridgeLoaded)
                        return Error(409,
                            "Player sizing is not currently available.");
                    actSetPlayerLarge(false);
                    break;
                case "now-playing-info":
                    if (apiOnlyMode)
                        return Error(409,
                            "This visual action is unavailable in API-only mode.");
                    if (string.IsNullOrEmpty(nowPlayingPath))
                        return Error(409, "Nothing is currently playing.");
                    actNowPlayingInfo();
                    break;
                case "panic":
                    actPanic();
                    break;
                default:
                    return Error(404, "Unknown action.");
            }
            return Accepted();
        }

        private ApiResult PlayNextApiOnlyClip()
        {
            string current = GetCurrentAnimationPath();
            if (current.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                GetNextClip(null, current);
                return Accepted();
            }
            string[] parts = current.Split('\\', 2);
            if (parts.Length != 2)
                return Error(409, "Nothing is currently playing.");

            ModelCard? card = Datastore.findCardByTag(
                parts[0].Split('-')[0]);
            List<ModelClip> clips = (card?.clips ?? [])
                .Where(clip => !string.IsNullOrWhiteSpace(clip.clipName))
                .OrderBy(clip => clip.clipNumber ?? int.MaxValue)
                .ToList();
            if (clips.Count == 0)
                return Error(409, "The current card has no playable clips.");

            int currentIndex = clips.FindIndex(clip => string.Equals(
                clip.clipName, parts[1], StringComparison.OrdinalIgnoreCase));
            ModelClip next = clips[(currentIndex + 1) % clips.Count];
            return ForceApiOnlyAnimation(GetAnimationPath(next));
        }

        private ApiResult PlayNextApiOnlyCard()
        {
            if (playbackBridgeClient?.IsConnected != true)
                return Error(409, "The iStripper bridge is not connected.");
            int result = playbackBridgeClient.Call("IStripperDesktopNext");
            return result < 0
                ? Error(409,
                    $"Native next card is unavailable (0x{result:X8}).")
                : Accepted();
        }

        private ApiResult ForceApiOnlyAnimation(string animationPath)
        {
            ClearQueuedCardSession();
            if (!RequestAnimationPlayback(animationPath))
                return Error(503, animationPath.StartsWith("custom:",
                    StringComparison.OrdinalIgnoreCase)
                    ? "The custom show is unavailable."
                    : "iStripper is not available.");
            return new ApiResult(202, new
            {
                accepted = true,
                animationPath
            });
        }

        private ApiResult ToggleApiOnlyPlayerLock()
        {
            if (!playerLockBridgeLoaded)
                return Error(409,
                    "Player locking is not currently available.");
            bool requested = !playerlocked;
            int result = SetVghdPlayerLocked(requested);
            if (result < 0)
                return Error(409,
                    $"Player locking is unavailable (0x{result:X8}).");
            playerlocked = requested;
            return Accepted();
        }

        private bool RestApiPlaybackControlAvailable(bool seek,
            out ApiResult? error)
        {
            RefreshApiOnlyPlaybackState();
            if (!PlaybackControlEnabled ||
                !playbackControlsAvailableForAccount ||
                !playbackBridgeLoaded)
            {
                error = Error(409,
                    "Playback control is not currently available.");
                return false;
            }
            if (seek && (!playbackMovieRegistered ||
                !playbackSeekingSupported || !playbackSeekReady))
            {
                error = Error(409, "Seeking is not currently ready.");
                return false;
            }
            error = null;
            return true;
        }

        private void RefreshApiOnlyPlaybackState()
        {
            if (!apiOnlyMode || !playbackBridgeLoaded)
                return;

            try
            {
                if (!playbackMovieRegistered)
                {
                    playbackMovieRegistered = movieCaptureHookInstalled &&
                        CallPlaybackBridgeApi(
                            "IStripperConsumeCapturedMovie") >= 0;
                    bool allowFallbackDiscovery =
                        !movieCaptureHookInstalled ||
                        playbackMovieCaptureFallbackAt ==
                            DateTime.MinValue ||
                        DateTime.UtcNow >= playbackMovieCaptureFallbackAt;
                    if (!playbackMovieRegistered && allowFallbackDiscovery)
                    {
                        DisableMovieCapture();
                        playbackMovieRegistered = CallPlaybackBridgeApi(
                            "IStripperDiscoverMovie") >= 0;
                    }
                    if (playbackMovieRegistered)
                    {
                        DisableMovieCapture();
                        playbackDecoderKind = CallPlaybackBridgeApi(
                            "IStripperGetDecoderKind");
                        playbackSeekingSupported =
                            playbackDecoderKind is 1 or 2;
                    }
                }
                if (!playbackMovieRegistered)
                    return;

                int state = CallPlaybackBridgeApi("IStripperGetState");
                if (state is not 3 and not 4)
                {
                    playbackMovieRegistered = false;
                    playbackSeekReady = false;
                    playbackLastKnownElapsedMilliseconds = 0;
                    playbackTimelineDurationMilliseconds = 0;
                    if (CallPlaybackBridgeApi(
                            "IStripperDiscoverMovie") < 0 ||
                        CallPlaybackBridgeApi(
                            "IStripperGetState") is not 3 and not 4)
                        return;
                    playbackMovieRegistered = true;
                    playbackDecoderKind = CallPlaybackBridgeApi(
                        "IStripperGetDecoderKind");
                    playbackSeekingSupported =
                        playbackDecoderKind is 1 or 2;
                }

                int elapsed = CallPlaybackBridgeApi(
                    "IStripperGetElapsedMilliseconds");
                int total = CallPlaybackBridgeApi(
                    "IStripperGetTotalMilliseconds");
                if (elapsed >= 0)
                    playbackLastKnownElapsedMilliseconds = elapsed;
                if (total >= 0)
                    playbackTimelineDurationMilliseconds = total;

                if (!playbackSeekReady && playbackSeekingSupported)
                {
                    int ready = CallPlaybackBridgeApi(
                        "IStripperIsSeekReady");
                    if (ready == 1 && playbackDecoderKind == 1)
                    {
                        int checkpoint = CallPlaybackBridgeApi(
                            "IStripperCaptureAlphaCheckpoint");
                        if (checkpoint >= 0)
                        {
                            playbackAlphaCheckpointBucket = elapsed / 5_000;
                            ready = 1;
                        }
                        else
                            ready = 0;
                    }
                    playbackSeekReady = ready == 1 || elapsed >=
                        PlaybackForcedReadyMilliseconds;
                }
                int checkpointBucket = elapsed / 5_000;
                if (playbackDecoderKind == 1 && playbackSeekReady &&
                    checkpointBucket != playbackAlphaCheckpointBucket &&
                    CallPlaybackBridgeApi(
                        "IStripperCaptureAlphaCheckpoint") >= 0)
                {
                    playbackAlphaCheckpointBucket = checkpointBucket;
                }
            }
            catch
            {
                playbackMovieRegistered = false;
                playbackSeekReady = false;
            }
        }

        private static bool TryCreateRestApiQueueEntry(
            ApiQueueEntryRequest request, out PlayQueueEntry? entry,
            out ApiResult? error)
        {
            string cardTag = request.CardTag?.Trim() ?? "";
            string cardName = request.CardName?.Trim() ?? "";
            if (!string.IsNullOrEmpty(cardTag) &&
                !string.IsNullOrEmpty(cardName))
            {
                entry = null;
                error = Error(400,
                    "Use either 'cardTag' or 'cardName', not both.");
                return false;
            }

            ModelCard? card;
            if (!string.IsNullOrEmpty(cardTag))
            {
                card = Datastore.findCardByTag(cardTag);
            }
            else if (!string.IsNullOrEmpty(cardName))
            {
                List<ModelCard> matches = (Datastore.modelcards ?? [])
                    .Where(card => ApiCardNameMatches(card, cardName))
                    .ToList();
                if (matches.Count > 1)
                {
                    entry = null;
                    error = Error(409,
                        $"'{cardName}' matches {matches.Count} cards. " +
                        "Use the full 'Model - Outfit' name or cardTag.");
                    return false;
                }
                card = matches.SingleOrDefault();
                cardTag = card == null ? "" : ApiCardTag(card);
            }
            else
            {
                entry = null;
                error = Error(400,
                    "'cardTag' or 'cardName' is required.");
                return false;
            }

            if (card == null)
            {
                entry = null;
                error = Error(404,
                    "The card is not in the local library.");
                return false;
            }

            string? clipName = string.IsNullOrWhiteSpace(request.ClipName)
                ? null : request.ClipName.Trim();
            if (clipName != null && request.ClipNumber != null)
            {
                entry = null;
                error = Error(400,
                    "Use either 'clipName' or 'clipNumber', not both.");
                return false;
            }
            if (request.ClipNumber != null)
            {
                ModelClip? clip = card.clips?.FirstOrDefault(item =>
                    item.clipNumber == request.ClipNumber);
                if (clip == null)
                {
                    entry = null;
                    error = Error(404,
                        $"Clip number {request.ClipNumber} is not available " +
                        $"for '{ApiCardDisplayName(card)}'.");
                    return false;
                }
                clipName = clip.clipName;
            }

            entry = new PlayQueueEntry(cardTag, clipName);
            if (!IsAvailableQueueEntry(entry))
            {
                error = Error(404,
                    "The card or clip is not in the local library.");
                return false;
            }
            error = null;
            return true;
        }

        private static string ApiCardTag(ModelCard card)
        {
            int separator = card.name.IndexOf('-');
            return separator < 0
                ? card.name : card.name[..separator];
        }

        private static string ApiCardDisplayName(ModelCard card) =>
            $"{card.modelName} - {card.outfit}".Trim(' ', '-');

        private static bool ApiCardNameMatches(ModelCard card,
            string requestedName)
        {
            string requested = NormalizeApiCardName(requestedName);
            return requested.Equals(
                    NormalizeApiCardName(ApiCardDisplayName(card)),
                    StringComparison.OrdinalIgnoreCase) ||
                requested.Equals(NormalizeApiCardName(card.outfit),
                    StringComparison.OrdinalIgnoreCase) ||
                requested.Equals(NormalizeApiCardName(card.name),
                    StringComparison.OrdinalIgnoreCase) ||
                requested.Equals(NormalizeApiCardName(ApiCardTag(card)),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeApiCardName(string value) =>
            string.Join(' ', value
                .Replace(':', ' ')
                .Replace('-', ' ')
                .Replace('/', ' ')
                .Replace('\\', ' ')
                .Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));

        private static readonly string[] ApiActions =
        [
            "next-clip", "next-card", "toggle-lock", "play-pause",
            "back-10-percent", "forward-10-percent", "restart",
            "player-large", "player-small", "now-playing-info", "panic"
        ];

        private static bool IsApiPlaybackSpeedAllowed(double speed) =>
            speed is 0.25 or 0.5 or 1 or 1.5 or 2 or 3 or 4;

        private static string[] ApiPathSegments(string path) =>
            path.Split('/', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        private static bool Matches(string[] parts,
            params string[] endpoint) =>
            parts.Length == endpoint.Length + 2 &&
            parts.Skip(2).SequenceEqual(endpoint,
                StringComparer.OrdinalIgnoreCase);

        private bool IsRestApiAuthorized(HttpListenerRequest request)
        {
            string header = request.Headers[
                "Authorization"] ?? "";
            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            byte[] supplied = Encoding.UTF8.GetBytes(
                header[prefix.Length..].Trim());
            byte[] expected = Encoding.UTF8.GetBytes(restApiToken);
            return supplied.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(
                    supplied, expected);
        }

        private static async Task<T> ReadRestApiJsonAsync<T>(
            HttpListenerRequest request,
            CancellationToken cancellationToken)
        {
            if (request.ContentLength64 > RestApiMaxBodyLength)
                throw new ApiRequestException(
                    413, "The request body is too large.");
            using StreamReader reader = new(request.InputStream,
                request.ContentEncoding, leaveOpen: true);
            string json = await reader.ReadToEndAsync(cancellationToken);
            if (Encoding.UTF8.GetByteCount(json) >
                RestApiMaxBodyLength)
                throw new ApiRequestException(
                    413, "The request body is too large.");
            if (string.IsNullOrWhiteSpace(json))
                throw new ApiRequestException(
                    400, "A JSON request body is required.");
            return JsonSerializer.Deserialize<T>(json, RestApiJson) ??
                throw new ApiRequestException(
                    400, "A JSON request body is required.");
        }

        private static async Task WriteRestApiResponseAsync(
            HttpListenerResponse response, ApiResult result,
            CancellationToken cancellationToken)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                result.Body, RestApiJson);
            response.StatusCode = result.StatusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = body.Length;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            try
            {
                await response.OutputStream.WriteAsync(
                    body, cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpListenerException or IOException or
                    OperationCanceledException)
            {
            }
            finally
            {
                response.Close();
            }
        }

        private static ApiResult Ok(object body) => new(200, body);

        private static ApiResult Accepted() =>
            new(202, new { accepted = true });

        private static ApiResult Error(int statusCode, string message) =>
            new(statusCode, new { error = message });
    }
}
