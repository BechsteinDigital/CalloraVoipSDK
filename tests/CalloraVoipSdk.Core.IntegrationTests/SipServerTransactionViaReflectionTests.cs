using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-040: the server transaction engine reflects received=/rport= into the outgoing response Via centrally
/// (RFC 3261 §18.2.1 / RFC 3581 §4), so a response whose header builder did not reflect — e.g. an ingress
/// OPTIONS 200 — still carries the NAT source, and a Via already reflected upstream is left unchanged.
/// </summary>
public sealed class SipServerTransactionViaReflectionTests
{
    private static SipRequest OptionsWith(string via) =>
        new("OPTIONS", "sip:us@example.test", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = via,
            ["From"] = "<sip:them@example.test>;tag=from-tag",
            ["To"] = "<sip:us@example.test>",
            ["Call-ID"] = "cf040-call",
            ["CSeq"] = "1 OPTIONS",
        }, string.Empty);

    [Fact]
    public async Task Response_via_is_reflected_against_the_packet_source_when_the_builder_did_not()
    {
        var transport = new CapturingSipTransportRuntime();
        using var engine = new SipServerTransactionEngine(transport, NullLogger.Instance);
        var source = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);

        // Unreflected response headers with a bare ;rport, as an ingress OPTIONS 200 builds them.
        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK-cf040;rport";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Via"] = via };

        await engine.SendResponseAsync(
            OptionsWith(via), source, SipTransportProtocol.Udp, 200, "OK", headers, body: null);

        var sent = Assert.Single(transport.SnapshotResponses());
        Assert.Contains(";rport=40000", sent.Headers["Via"]);
        Assert.Contains(";received=203.0.113.7", sent.Headers["Via"]);
        // The DATAGRAM must go to the actual source port (RFC 3581), not the sent-by port 5060: the destination
        // is derived from the reflected Via, so a bare ;rport routes to the real source port, not 5060.
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000), sent.RemoteEndPoint);
    }

    [Fact]
    public async Task An_already_reflected_response_via_is_left_unchanged()
    {
        var transport = new CapturingSipTransportRuntime();
        using var engine = new SipServerTransactionEngine(transport, NullLogger.Instance);
        var source = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);

        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK-cf040;received=203.0.113.7;rport=40000";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Via"] = via };

        await engine.SendResponseAsync(
            OptionsWith(via), source, SipTransportProtocol.Udp, 200, "OK", headers, body: null);

        var sent = Assert.Single(transport.SnapshotResponses());
        Assert.Equal(via, sent.Headers["Via"]);
    }

    [Fact]
    public async Task Reflection_also_applies_on_the_direct_send_path_without_a_transaction_key()
    {
        var transport = new CapturingSipTransportRuntime();
        using var engine = new SipServerTransactionEngine(transport, NullLogger.Instance);
        var source = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);

        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK-cf040;rport";
        // No CSeq → SipServerTransactionKey.TryFromRequest fails → the engine takes the direct send path.
        var keyless = new SipRequest("OPTIONS", "sip:us@example.test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Via"] = via,
                ["From"] = "<sip:them@example.test>;tag=from-tag",
                ["To"] = "<sip:us@example.test>",
                ["Call-ID"] = "cf040-keyless",
            }, string.Empty);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Via"] = via };

        await engine.SendResponseAsync(keyless, source, SipTransportProtocol.Udp, 200, "OK", headers, body: null);

        var sent = Assert.Single(transport.SnapshotResponses());
        Assert.Contains(";rport=40000", sent.Headers["Via"]);
        Assert.Contains(";received=203.0.113.7", sent.Headers["Via"]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000), sent.RemoteEndPoint);
    }
}
