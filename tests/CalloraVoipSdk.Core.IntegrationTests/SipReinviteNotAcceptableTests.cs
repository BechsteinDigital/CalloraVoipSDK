using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [SIP] #13: a re-INVITE or UPDATE that carries an SDP offer we cannot answer must be rejected with 488 Not
/// Acceptable Here (RFC 3264 §6 / RFC 3311 §5.2), not answered 200 OK with a fresh offer sent as if it were the
/// answer. An offerless re-INVITE still legitimately carries our offer in the 2xx (RFC 3261 §13.2.1).
/// </summary>
public sealed class SipReinviteNotAcceptableTests
{
    private const string CallId = "call-ack-test";  // AckTestSipCallSessionContext default
    private const string LocalTag = "local-tag";
    private const string RemoteTag = "remote-tag";
    private const string OfferBody = "v=0\r\no=peer 1 1 IN IP4 192.0.2.1\r\ns=-\r\nc=IN IP4 192.0.2.1\r\nt=0 0\r\nm=audio 5000 RTP/AVP 0\r\n";

    private static SipSessionSdpProvider Unnegotiable() => new()
    {
        BuildOffer = (_, _) => "v=0\r\n",       // a fresh offer — what the buggy path used to send as the answer
        TryNegotiateAnswer = (_, _, _) => null,  // no common media → negotiation fails
        TryParseMediaParameters = (_, _) => null,
        IsRemoteHold = _ => false,
    };

    private static (SipCallSessionInboundService Service, CapturingSipServerTransactionEngine Engine)
        Build(SipSessionSdpProvider sdp)
    {
        var engine = new CapturingSipServerTransactionEngine();
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            ServerTransactions = engine,
            RemoteTag = RemoteTag,
            SdpProvider = sdp,
        };
        return (new SipCallSessionInboundService(context, new SipCallSessionHeaderService(context)), engine);
    }

    private static SipRequest InDialog(string method, string? body)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.1:5060;branch=z9hG4bK-reinvite",
            ["Max-Forwards"] = "70",
            ["From"] = $"<sip:them@example.test>;tag={RemoteTag}",
            ["To"] = $"<sip:us@example.test>;tag={LocalTag}",
            ["Call-ID"] = CallId,
            ["CSeq"] = $"3 {method}",
        };
        if (!string.IsNullOrEmpty(body))
            headers["Content-Type"] = "application/sdp";
        return new SipRequest(method, "sip:us@example.test", headers, body ?? string.Empty);
    }

    [Fact]
    public async Task A_reINVITE_offer_we_cannot_answer_is_rejected_with_488()
    {
        var (service, engine) = Build(Unnegotiable());

        await service.HandleInboundRequestAsync(
            new IPEndPoint(IPAddress.Loopback, 5060), InDialog("INVITE", OfferBody), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 488);
        Assert.DoesNotContain(engine.Responses, r => r.StatusCode == 200);
    }

    [Fact]
    public async Task An_UPDATE_offer_we_cannot_answer_is_rejected_with_488()
    {
        var (service, engine) = Build(Unnegotiable());

        await service.HandleInboundRequestAsync(
            new IPEndPoint(IPAddress.Loopback, 5060), InDialog("UPDATE", OfferBody), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 488);
        Assert.DoesNotContain(engine.Responses, r => r.StatusCode == 200);
    }

    [Fact]
    public async Task An_offerless_reINVITE_still_answers_200_and_is_not_rejected()
    {
        // Guard: an offerless re-INVITE carries our offer in the 2xx and must NOT be turned into a 488.
        var (service, engine) = Build(Unnegotiable());

        await service.HandleInboundRequestAsync(
            new IPEndPoint(IPAddress.Loopback, 5060), InDialog("INVITE", body: null), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 200);
        Assert.DoesNotContain(engine.Responses, r => r.StatusCode == 488);
    }
}
