using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-2: an inbound request over a connection-oriented transport must be answered back over the
/// exact accepted connection it arrived on, and the transport must be taken from that accepted connection —
/// never reconstructed from the peer-controlled Via. Locks in two properties an attacker/misconfigured peer
/// must not be able to violate: (a) a TCP response goes back down the same accepted stream, not out to the
/// peer's ephemeral source port where no server listens; (b) a request that forges a "UDP" Via while
/// arriving over TCP is answered over the real TCP transport and its accepted connection.
/// </summary>
public sealed class SipInboundResponseTransportTests
{
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_stream_request_is_answered_on_the_same_accepted_connection()
    {
        using var runtime = new SipTransportRuntime(NullLoggerFactory.Instance);
        var tcpPort = runtime.GetLocalEndPoint(SipTransportProtocol.Tcp).Port;

        SipTransportProtocol? seenTransport = null;
        int? seenConnectionId = null;
        using var subscription = runtime.SubscribeRequests((context, request) =>
        {
            seenTransport = context.Transport;
            seenConnectionId = context.ConnectionId;

            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Via"] = request.Header("Via") ?? string.Empty,
                ["Content-Length"] = "0",
            };
            // Answer using the transport + accepted connection reported for this request.
            _ = runtime.SendResponseAsync(
                200, "OK", responseHeaders, body: null,
                context.RemoteEndPoint, context.Transport, context.ConnectionId, CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tcpPort);
        var stream = client.GetStream();

        var request = Encoding.UTF8.GetBytes(
            "MESSAGE sip:bob@example.test SIP/2.0\r\n" +
            "Via: SIP/2.0/TCP 192.0.2.1:5060;branch=z9hG4bK-wire-1\r\n" +
            "Max-Forwards: 70\r\n" +
            "From: <sip:alice@example.test>;tag=w1\r\n" +
            "To: <sip:bob@example.test>\r\n" +
            "Call-ID: wire-call-1@example.test\r\n" +
            "CSeq: 1 MESSAGE\r\n" +
            "Content-Length: 0\r\n" +
            "\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        // Read the response back on the SAME client connection. Before this fix the runtime routed the TCP
        // response through the outbound pool, dialling our ephemeral source port (no listener) — nothing
        // would ever arrive here and the read would time out.
        var buffer = new byte[4096];
        using var readCts = new CancellationTokenSource(ReadWait);
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, readCts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("No SIP response arrived on the accepted inbound connection within the timeout.");
            return;
        }

        var responseText = Encoding.UTF8.GetString(buffer, 0, read);
        Assert.Contains("SIP/2.0 200", responseText);
        Assert.Equal(SipTransportProtocol.Tcp, seenTransport);
        Assert.NotNull(seenConnectionId);
    }

    [Fact]
    public async Task A_forged_UDP_Via_over_a_TCP_connection_is_answered_over_the_real_TCP_transport()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        const int connectionId = 42;
        var remote = new IPEndPoint(IPAddress.Loopback, 5060);

        // The Via advertises UDP, but the request actually arrives over the accepted TCP connection 42.
        transport.DeliverInboundRequest(remote, ForgedUdpViaMessage(), SipTransportProtocol.Tcp, connectionId);

        (int StatusCode, IReadOnlyDictionary<string, string> Headers, IPEndPoint RemoteEndPoint, SipTransportProtocol Transport, int? InboundConnectionId) response = default;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var snapshot = transport.SnapshotResponses();
            if (snapshot.Count > 0 && snapshot.Any(r => r.StatusCode == 200))
            {
                response = snapshot.First(r => r.StatusCode == 200);
                break;
            }

            await Task.Delay(10);
        }

        Assert.Equal(200, response.StatusCode);
        // The real transport of the accepted connection wins over the peer-controlled Via: the response is
        // sent over TCP, routed back onto the accepted connection — a "UDP" Via cannot steer it.
        Assert.Equal(SipTransportProtocol.Tcp, response.Transport);
        Assert.Equal(connectionId, response.InboundConnectionId);
    }

    private static SipRequest ForgedUdpViaMessage()
    {
        const string body = "ping";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 203.0.113.9:5060;branch=z9hG4bK-spoof-1",
            ["Max-Forwards"] = "70",
            ["From"] = "<sip:alice@example.test>;tag=from-tag",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = "spoof-call-1@example.test",
            ["CSeq"] = "1 MESSAGE",
            ["Content-Type"] = "text/plain",
            ["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture),
        };
        return new SipRequest("MESSAGE", "sip:bob@example.test", headers, body);
    }
}
