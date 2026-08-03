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
        private const int RestApiPort = 17871;
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
        private sealed record FullscreenBridgeSlot(
            int Id, string Source, string Card, string Clip);
        private sealed record FullscreenBridgeSnapshot(
            string[] Clips, string[] Queue, FullscreenBridgeSlot[] Slots);
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
                    $"http://127.0.0.1:{RestApiPort}/");
                restApiListener.Start();
                _ = Task.Run(() => RunRestApiAsync(
                    restApiListener, restApiLifetime.Token));
                Debug.WriteLine(
                    $"REST API listening on http://127.0.0.1:{RestApiPort}/");
            }
            catch (Exception exception)
            {
                restApiListener?.Close();
                restApiListener = null;
                restApiLifetime?.Dispose();
                restApiLifetime = null;
                Debug.WriteLine("REST API could not start: " +
                    exception.Message);
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
                        "POST /api/v1/fullscreen/play",
                        "POST /api/v1/fullscreen/next",
                        "POST /api/v1/fullscreen/slots/{slotId}/next",
                        "POST /api/v1/fullscreen/slots/{slotId}/play",
                        "DELETE /api/v1/fullscreen/queue",
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

            if (method == "DELETE" &&
                Matches(parts, "fullscreen", "queue"))
            {
                return await InvokeAsync(() =>
                    RunFullscreenBridgeAction(
                        "IStripperFullscreenClearQueue"));
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
                    enablePlayQueueToolStripMenuItem.Checked =
                        body.Enabled.Value;
                    RefreshPlayQueueVisibility();
                    RebuildAutomaticQueue();
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
                return await InvokeAsync(() => SeekRestApiPlayback(body));
            }

            if (method == "POST" &&
                Matches(parts, "playback", "speed"))
            {
                ApiSpeedRequest body =
                    await ReadRestApiJsonAsync<ApiSpeedRequest>(
                        request, cancellationToken);
                return await InvokeAsync(() => SetRestApiPlaybackSpeed(body));
            }

            if (method == "POST" && parts.Length == 4 &&
                parts[2].Equals("actions",
                    StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeAsync(
                    () => ExecuteRestApiAction(parts[3]));
            }

            return Error(404, "Endpoint not found.");
        }

        private object CreateRestApiStatus()
        {
            string animationPath = GetCurrentAnimationPath();
            string[] animationParts = animationPath.Split('\\', 2);
            string cardTag = animationParts.FirstOrDefault() ?? "";
            string clipName = animationParts.Length > 1
                ? animationParts[1] : "";
            ModelCard? card = string.IsNullOrEmpty(cardTag)
                ? null : Datastore.findCardByTag(
                    cardTag.Split('-')[0]);
            int stateCode = playbackBridgeLoaded &&
                playbackMovieRegistered
                    ? CallPlaybackBridgeApi("IStripperGetState") : -1;
            string state = stateCode switch
            {
                3 => "playing",
                4 => "paused",
                _ when string.IsNullOrEmpty(animationPath) => "stopped",
                _ => "unavailable"
            };

            return new
            {
                apiVersion = 1,
                playing = new
                {
                    state,
                    animationPath,
                    cardTag,
                    cardName = card == null
                        ? null : ApiCardDisplayName(card),
                    clipName,
                    model = card?.modelName,
                    outfit = card?.outfit,
                    elapsedMilliseconds = playbackLastKnownElapsedMilliseconds,
                    durationMilliseconds =
                        playbackTimelineDurationMilliseconds,
                    speed = requestedPlaybackSpeed
                },
                player = new
                {
                    locked = playerlocked,
                    panic = panicActive,
                    playbackControlsAvailable =
                        Properties.Settings.Default.EnablePlaybackControl &&
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
                queue = state.Queue.Select((cardTag, index) =>
                {
                    ModelCard? card = Datastore.findCardByTag(cardTag);
                    return new
                    {
                        index,
                        cardTag,
                        cardName = card == null ? null :
                            ApiCardDisplayName(card),
                        model = card?.modelName,
                        outfit = card?.outfit
                    };
                }).ToList()
            };
        }

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

        private ApiResult PlayRestApiFullscreenEntry(
            ApiQueueEntryRequest request)
        {
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
                    cardTag = ApiCardTag(card),
                    cardName = ApiCardDisplayName(card),
                    model = card.modelName,
                    outfit = card.outfit,
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
                activeAutomaticQueueEntry ?? activeQueuedCard) is { } active
                    ? CreateRestApiQueueEntry(active, -1) : null,
            manual = manualPlayQueue.Select(
                CreateRestApiQueueEntry).ToList(),
            automatic = automaticPlayQueue.Select(
                CreateRestApiQueueEntry).ToList()
        };

        private static object CreateRestApiQueueEntry(
            PlayQueueEntry entry, int index)
        {
            ModelCard? card = Datastore.findCardByTag(entry.CardTag);
            return new
            {
                index,
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

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Totem\vghd\parameters", true);
            if (key == null)
                return Error(503, "iStripper is not available.");

            ClearQueuedCardSession();
            BeginAnimationReplacement(animationPath);
            key.SetValue("ForceAnim", animationPath);
            SelectQueuedCard(entry!.CardTag, animationPath);
            BeginInvoke((Action)TaskbarThumbnail);
            return new ApiResult(202, new
            {
                accepted = true,
                animationPath
            });
        }

        private ApiResult SeekRestApiPlayback(ApiSeekRequest request)
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
            _ = RunPlaybackOperationAsync(
                token => SeekAbsoluteAsync(target, token));
            return Accepted();
        }

        private ApiResult SetRestApiPlaybackSpeed(ApiSpeedRequest request)
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
            suppressPlaybackSpeedSelection = true;
            cmbPlaybackSpeed.SelectedItem =
                $"{speed.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture)}x";
            suppressPlaybackSpeedSelection = false;
            _ = RunPlaybackOperationAsync(_ =>
            {
                SetPlaybackRate(speed);
                SetPlaybackStatus(
                    $"Playback speed set to {speed:0.##}x.");
                return Task.CompletedTask;
            });
            return Accepted();
        }

        private ApiResult ExecuteRestApiAction(string action)
        {
            switch (action.ToLowerInvariant())
            {
                case "next-clip":
                    GetNextClip(useQueue: false);
                    break;
                case "next-card":
                    GetNextCard();
                    break;
                case "toggle-lock":
                    lockPlayerToolStripMenuItem.Checked =
                        !lockPlayerToolStripMenuItem.Checked;
                    setPlayerLocked();
                    break;
                case "play-pause":
                    if (!RestApiPlaybackControlAvailable(false,
                            out ApiResult? playPauseError))
                        return playPauseError!;
                    cmdPlayPause.PerformClick();
                    break;
                case "back-10-percent":
                    if (!RestApiPlaybackControlAvailable(true,
                            out ApiResult? rewindError))
                        return rewindError!;
                    cmdRewind.PerformClick();
                    break;
                case "forward-10-percent":
                    if (!RestApiPlaybackControlAvailable(true,
                            out ApiResult? forwardError))
                        return forwardError!;
                    cmdFastForward.PerformClick();
                    break;
                case "restart":
                    if (!RestApiPlaybackControlAvailable(true,
                            out ApiResult? restartError))
                        return restartError!;
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

        private bool RestApiPlaybackControlAvailable(bool seek,
            out ApiResult? error)
        {
            if (!Properties.Settings.Default.EnablePlaybackControl ||
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
