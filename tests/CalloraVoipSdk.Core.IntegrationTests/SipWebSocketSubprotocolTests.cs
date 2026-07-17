using System.Net.WebSockets;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SIP-over-WebSocket subprotocol enforcement (HARD-E6, RFC 7118 §5). The server's inbound WebSocket
/// upgrade path must reject a client that does not offer the <c>sip</c> subprotocol, instead of
/// accepting a WebSocket with no negotiated subprotocol that could not carry SIP framing. Drives the
/// real transport-runtime accept loop over a loopback WebSocket handshake.
/// </summary>
public sealed class SipWebSocketSubprotocolTests
{
    [Fact]
    public async Task Inbound_websocket_upgrade_requires_the_sip_subprotocol()
    {
        using var runtime = new SipTransportRuntime(NullLoggerFactory.Instance);
        var wsPort = runtime.GetLocalEndPoint(SipTransportProtocol.Ws).Port;
        var uri = new Uri($"ws://localhost:{wsPort}/");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // A client that does NOT offer "sip" must be rejected (pre-fix it was accepted subprotocol-less).
        using (var plainClient = new ClientWebSocket())
        {
            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => plainClient.ConnectAsync(uri, timeout.Token));
        }

        // A client that offers "sip" is accepted and the subprotocol is negotiated back.
        using (var sipClient = new ClientWebSocket())
        {
            sipClient.Options.AddSubProtocol("sip");
            await sipClient.ConnectAsync(uri, timeout.Token);
            Assert.Equal("sip", sipClient.SubProtocol);
        }
    }
}
