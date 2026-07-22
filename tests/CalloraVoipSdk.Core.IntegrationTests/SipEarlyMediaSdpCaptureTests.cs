using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// F011 slice 1 (signaling foundation for early media, RFC 3960): a provisional 180/183 response that
/// carries an SDP body has that body captured as early-media SDP — kept separate from the final 200-OK
/// answer — while a body-less provisional leaves it null. No media session is started here; the 200-OK
/// path is unaffected.
/// </summary>
public sealed class SipEarlyMediaSdpCaptureTests
{
    private const string EarlyMediaSdp =
        "v=0\r\no=- 1 1 IN IP4 192.0.2.10\r\ns=-\r\nc=IN IP4 192.0.2.10\r\nt=0 0\r\n" +
        "m=audio 40000 RTP/AVP 0\r\na=sendonly\r\n";

    private static Dictionary<string, string> Headers(CapturedSipRequest req) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Via"] = req.Headers["Via"],
        ["From"] = req.Headers["From"],
        ["To"] = req.Headers["To"],
        ["Call-ID"] = req.Headers["Call-ID"],
        ["CSeq"] = req.Headers["CSeq"],
        ["Contact"] = req.Headers.TryGetValue("Contact", out var c) ? c : "<sip:bob@192.0.2.10:5060>",
    };

    [Fact]
    public async Task Provisional_183_with_sdp_body_is_captured_as_early_media()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ProvisionalResponsesFactory = req => req.Method == "INVITE"
                ? [new SipResponse(183, "Session Progress", Headers(req), EarlyMediaSdp)]
                : Array.Empty<SipResponse>(),
            ResponseFactory = req => req.Method == "INVITE"
                ? new SipResponse(200, "OK", Headers(req), EarlyMediaSdp)
                : null,
        };
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None);

        Assert.Equal(EarlyMediaSdp, context.CapturedEarlyMediaSdp);
    }

    [Fact]
    public async Task Provisional_180_without_body_leaves_early_media_unset()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ProvisionalResponsesFactory = req => req.Method == "INVITE"
                ? [new SipResponse(180, "Ringing", Headers(req), string.Empty)]
                : Array.Empty<SipResponse>(),
            ResponseFactory = req => req.Method == "INVITE"
                ? new SipResponse(200, "OK", Headers(req), string.Empty)
                : null,
        };
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None);

        Assert.Null(context.CapturedEarlyMediaSdp);
    }
}
