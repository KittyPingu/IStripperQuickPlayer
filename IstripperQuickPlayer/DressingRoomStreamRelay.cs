using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IStripperQuickPlayer;

internal sealed class DressingRoomStreamRelay : IDisposable
{
    private const string UserAgent =
        "Vghd/2.4.0 - Build 1015 (Windows 11 (10.0.26200) x64) - Branch beta";
    private readonly TcpListener listener;
    private readonly HttpClient client = new();
    private readonly string sourceUrl;
    private readonly CancellationTokenSource cancellation = new();

    private DressingRoomStreamRelay(string sourceUrl)
    {
        this.sourceUrl = sourceUrl;
        listener = new TcpListener(IPAddress.Loopback, 0);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public string StreamUrl { get; private set; } = "";

    public static Task<DressingRoomStreamRelay> StartAsync(string sourceUrl)
    {
        DressingRoomStreamRelay relay = new(sourceUrl);
        relay.listener.Start();
        int port = ((IPEndPoint)relay.listener.LocalEndpoint).Port;
        relay.StreamUrl = $"http://127.0.0.1:{port}/dressing-room.mp4";
        _ = relay.AcceptLoopAsync();
        return Task.FromResult(relay);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient incoming = await listener.AcceptTcpClientAsync(
                    cancellation.Token);
                _ = RelayAsync(incoming);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RelayAsync(TcpClient incoming)
    {
        using (incoming)
        {
            NetworkStream output = incoming.GetStream();
            using StreamReader reader = new(output, Encoding.ASCII, false,
                4096, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync();
            if (requestLine == null)
                return;
            bool head = requestLine.StartsWith("HEAD ",
                StringComparison.OrdinalIgnoreCase);
            string? range = null;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    range = line[6..].Trim();
            }

            using HttpRequestMessage request = new(
                head ? HttpMethod.Head : HttpMethod.Get, sourceUrl);
            if (range != null)
                request.Headers.TryAddWithoutValidation("Range", range);
            using HttpResponseMessage response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            StringBuilder headers = new();
            headers.Append($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n");
            headers.Append("Connection: close\r\nAccept-Ranges: bytes\r\n");
            if (response.Content.Headers.ContentLength is long length)
                headers.Append($"Content-Length: {length}\r\n");
            if (response.Content.Headers.ContentType != null)
                headers.Append($"Content-Type: {response.Content.Headers.ContentType}\r\n");
            if (response.Content.Headers.ContentRange != null)
                headers.Append($"Content-Range: {response.Content.Headers.ContentRange}\r\n");
            headers.Append("\r\n");
            await output.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()),
                cancellation.Token);
            if (!head)
            {
                await using Stream input = await response.Content.ReadAsStreamAsync(
                    cancellation.Token);
                await input.CopyToAsync(output, cancellation.Token);
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener.Stop();
        client.Dispose();
        cancellation.Dispose();
    }
}
