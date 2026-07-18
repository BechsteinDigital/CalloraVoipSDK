using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

// Simplest-form two-person video call: a minimal WebSocket signalling relay + a static browser page that
// uses native WebRTC (getUserMedia + RTCPeerConnection). Open the page in two tabs — the two browsers
// connect peer-to-peer and exchange video; this host only forwards SDP/ICE between them.
//
// NOTE (architecture): the CalloraVoipSdk WebRTC media peer is NOT in the media path here — this is the
// signalling layer of the WebRTC story. Putting the SDK peer in the media path (browser <-> SDK <->
// browser) is the browser-interop milestone (needs validation against real browsers).

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

// One global two-peer room. Every message from one peer is relayed verbatim to the other.
var peers = new ConcurrentDictionary<Guid, WebSocket>();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var id = Guid.NewGuid();
    peers[id] = socket;

    // The second peer to join initiates the offer (keeps the handshake deterministic).
    if (peers.Count == 2)
    {
        await SendAsync(socket, "{\"type\":\"start\"}");
    }

    var buffer = new byte[64 * 1024];   // SDP/ICE messages are small; one frame is enough for this demo
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            foreach (var (otherId, other) in peers)
            {
                if (otherId != id && other.State == WebSocketState.Open)
                {
                    await SendAsync(other, text);
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Host shutdown / request aborted — expected, end the loop.
    }
    catch (WebSocketException)
    {
        // The peer dropped abruptly — expected for a browser tab close; do not tear down the relay.
    }
    finally
    {
        peers.TryRemove(id, out _);
    }
});

app.Run();

static Task SendAsync(WebSocket socket, string message)
    => socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
