using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound out-of-dialog SIP MESSAGE (RFC 3428, CF-066a). The ingress handler answers a pager-mode
/// MESSAGE with 200 OK — not the pre-CF-066a 501 Not Implemented — advertises MESSAGE in Allow, and adds
/// a To-tag to the response (RFC 3261 §8.2.6.2) without creating a dialog.
/// </summary>
public sealed class SipMessageIngressTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 5060);

    private static SipRequest InboundMessage(string body = "Hello SIP!", string? contentType = "text/plain")
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.1:5060;branch=z9hG4bK-msg-1",
            ["Max-Forwards"] = "70",
            ["From"] = "<sip:alice@example.test>;tag=from-tag",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = "msg-call-1@example.test",
            ["CSeq"] = "1 MESSAGE",
            ["Content-Length"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (contentType is not null)
            headers["Content-Type"] = contentType;
        return new SipRequest("MESSAGE", "sip:bob@example.test", headers, body);
    }

    private static SipCallSignalingService Build(CapturingSipTransportRuntime transport) =>
        new(transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

    private static async Task<IReadOnlyList<(int StatusCode, IReadOnlyDictionary<string, string> Headers, IPEndPoint RemoteEndPoint)>>
        WaitForResponsesAsync(CapturingSipTransportRuntime transport)
    {
        // The ingress entry is fire-and-forget (void); the response is sent on a background task.
        for (var i = 0; i < 50 && transport.SnapshotResponses().Count == 0; i++)
            await Task.Delay(10);
        return transport.SnapshotResponses();
    }

    [Fact]
    public async Task An_out_of_dialog_MESSAGE_is_answered_200_OK()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = Build(transport);

        transport.DeliverInboundRequest(Remote, InboundMessage());

        var responses = await WaitForResponsesAsync(transport);
        Assert.Contains(responses, r => r.StatusCode == 200);
        // MESSAGE must no longer fall through to the 501 Not Implemented fallback.
        Assert.DoesNotContain(responses, r => r.StatusCode == 501);
    }

    [Fact]
    public async Task The_200_OK_carries_a_generated_To_tag_and_echoes_the_request_CSeq()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = Build(transport);

        transport.DeliverInboundRequest(Remote, InboundMessage());

        var ok = (await WaitForResponsesAsync(transport)).Single(r => r.StatusCode == 200);
        // RFC 3261 §8.2.6.2: a non-100 UAS response gets a To-tag (even though MESSAGE opens no dialog).
        Assert.Contains("tag=", ok.Headers["To"]);
        // CSeq is echoed verbatim — method stays MESSAGE.
        Assert.Equal("1 MESSAGE", ok.Headers["CSeq"]);
    }

    [Fact]
    public void An_inbound_MESSAGE_raises_IncomingMessage_with_the_parsed_content()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = Build(transport);

        SipIncomingMessageEventArgs? received = null;
        service.IncomingMessage += (_, e) => received = e; // raised synchronously by the ingress handler

        transport.DeliverInboundRequest(Remote, InboundMessage(body: "Hello SIP!", contentType: "text/plain"));

        Assert.NotNull(received);
        Assert.Equal("Hello SIP!", received!.Body);
        Assert.Equal("text/plain", received.ContentType);
        Assert.Contains("alice@example.test", received.From);
        Assert.Contains("bob@example.test", received.To);
        Assert.Equal("msg-call-1@example.test", received.CallId);
    }
}
