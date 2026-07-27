using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>Eine Signaling-Nachricht zwischen C#-Test und Browser (JSON über den WebSocket).</summary>
public sealed class BridgeMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("sdp")] public string? Sdp { get; set; }
    [JsonPropertyName("candidate")] public string? Candidate { get; set; }
    [JsonPropertyName("bytesReceived")] public long? BytesReceived { get; set; }
    [JsonPropertyName("packetsReceived")] public long? PacketsReceived { get; set; }
    [JsonPropertyName("framesDecoded")] public long? FramesDecoded { get; set; }
}

/// <summary>
/// In-process HTTP+WS-Signaling-Bridge: serviert die Browser-Seite unter <c>/</c> und tauscht
/// Offer/Answer/ICE/Stats über einen WebSocket unter <c>/ws</c>. Inbound (Browser→C#) landet in
/// <see cref="Inbound"/>; Outbound (C#→Browser) via <see cref="SendAsync"/>.
/// </summary>
public sealed class BrowserInteropSignalingBridge : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpListener _listener = new();
    private readonly string _htmlBody;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private WebSocket? _socket;
    private readonly TaskCompletionSource _socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _accept;

    /// <summary>Empfangene Nachrichten vom Browser (Answer/Candidate/Stats).</summary>
    public Channel<BridgeMessage> Inbound { get; } = Channel.CreateUnbounded<BridgeMessage>();

    public BrowserInteropSignalingBridge(string htmlBody)
    {
        _htmlBody = htmlBody;
        _port = FreeTcpPort();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public string BaseUri => $"http://127.0.0.1:{_port}/";
    public string WebSocketUri => $"ws://127.0.0.1:{_port}/ws";

    /// <summary>Wartet, bis der Browser den WebSocket geöffnet hat (nach StartAsync + Navigation).</summary>
    public Task WebSocketConnected => _socketReady.Task;

    public Task StartAsync()
    {
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }

            if (ctx.Request.Url?.AbsolutePath == "/ws" && ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                _socket = wsCtx.WebSocket;
                _socketReady.TrySetResult();
                _ = Task.Run(() => ReceiveLoopAsync(_socket));
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(_htmlBody);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, _cts.Token).ConfigureAwait(false); }
            catch { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            var text = Encoding.UTF8.GetString(buf, 0, r.Count);
            var msg = JsonSerializer.Deserialize<BridgeMessage>(text, Json);
            if (msg is not null) await Inbound.Writer.WriteAsync(msg).ConfigureAwait(false);
        }
        Inbound.Writer.TryComplete();
    }

    public async Task SendAsync(BridgeMessage message)
    {
        var ws = _socket ?? throw new InvalidOperationException("WebSocket noch nicht verbunden.");
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
    }

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _socket?.Dispose(); } catch { /* best effort */ }
        try { _listener.Stop(); } catch { /* best effort */ }
        if (_accept is not null) { try { await _accept.ConfigureAwait(false); } catch { /* best effort */ } }
        _cts.Dispose();
    }
}
