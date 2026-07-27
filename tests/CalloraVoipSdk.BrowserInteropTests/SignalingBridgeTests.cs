using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class SignalingBridgeTests
{
    [Fact]
    public async Task Bridge_Serves_Html_And_Roundtrips_A_Ws_Message()
    {
        await using var bridge = new BrowserInteropSignalingBridge(htmlBody: "<html>hello-bridge</html>");
        await bridge.StartAsync();

        // (a) GET / liefert das HTML
        using var http = new HttpClient();
        var html = await http.GetStringAsync(bridge.BaseUri);
        Assert.Contains("hello-bridge", html);

        // (b) WS: Client verbindet, C# empfängt eine Inbound-Nachricht, sendet eine Outbound zurück
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(bridge.WebSocketUri), CancellationToken.None);
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"ready\"}");
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

        var inbound = await bridge.Inbound.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ready", inbound.Type);

        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = "v=0..." });
        var buf = new byte[4096];
        var recv = await ws.ReceiveAsync(buf, CancellationToken.None);
        var text = Encoding.UTF8.GetString(buf, 0, recv.Count);
        Assert.Contains("\"type\":\"offer\"", text);
    }
}
