using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// In-dialog OPTIONS handling (RFC 3261 §11) through the real inbound handler
/// (<see cref="SipCallSessionInboundService.HandleInboundRequestAsync"/>): a 200 OK that advertises the
/// methods and body types this endpoint accepts (#336).
/// </summary>
/// <remarks>
/// OPTIONS is how a peer — or a monitoring proxy — asks "are you there, and what do you speak" mid-call. The
/// answer has to carry Allow, or the asker learns the endpoint is alive but nothing about what it will accept,
/// which is the whole point of the query.
/// </remarks>
public sealed class SipOptionsResponderTests
{
    private const string RemoteTag = "remote-tag";

    [Fact]
    public async Task An_in_dialog_options_is_answered_200_with_the_allowed_methods()
    {
        var (service, engine) = BuildService();

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Options(), default);

        var response = Assert.Single(engine.DetailedResponses);
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.Headers.TryGetValue("Allow", out var allow));
        // The methods a UAS must be able to name for a caller to know what it can send next.
        foreach (var method in new[] { "INVITE", "ACK", "BYE", "CANCEL", "OPTIONS" })
            Assert.Contains(method, allow!);
    }

    [Fact]
    public async Task The_options_response_advertises_the_accepted_body_types()
    {
        var (service, engine) = BuildService();

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Options(), default);

        var response = Assert.Single(engine.DetailedResponses);
        Assert.True(response.Headers.TryGetValue("Accept", out var accept));
        Assert.Contains("application/sdp", accept!);   // otherwise a caller cannot know re-INVITE will be understood
    }

    private static (SipCallSessionInboundService Service, CapturingSipServerTransactionEngine Engine) BuildService()
    {
        var engine = new CapturingSipServerTransactionEngine();
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            ServerTransactions = engine,
            RemoteTag = RemoteTag,
        };
        return (new SipCallSessionInboundService(context, new SipCallSessionHeaderService(context)), engine);
    }

    private static SipRequest Options()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.1:5060;branch=z9hG4bK-options",
            ["Max-Forwards"] = "70",
            ["From"] = $"<sip:them@example.test>;tag={RemoteTag}",
            ["To"] = "<sip:us@example.test>;tag=local-tag",   // AckTestSipCallSessionContext default local tag
            ["Call-ID"] = "call-ack-test",
            ["CSeq"] = "2 OPTIONS",
        };
        return new SipRequest("OPTIONS", "sip:us@example.test", headers, string.Empty);
    }
}
