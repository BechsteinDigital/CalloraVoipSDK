using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end proof of the CF-013 dialog-identity gate through the real inbound handler
/// (<see cref="SipCallSessionInboundService.HandleInboundRequestAsync"/>): a mid-dialog request whose To/From
/// tags do not match the dialog is rejected with 481 (ACK is dropped without a response), while a request with
/// the correct tags passes the gate and is handled normally (RFC 3261 §12.2.2).
/// </summary>
public sealed class SipDialogIdentityInboundGateTests
{
    private const string LocalTag = "local-tag";   // AckTestSipCallSessionContext default
    private const string RemoteTag = "remote-tag";
    private const string CallId = "call-ack-test";  // AckTestSipCallSessionContext default

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

    private static SipRequest InDialogRequest(string method, string toTag, string fromTag)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.1:5060;branch=z9hG4bK-cf013",
            ["Max-Forwards"] = "70",
            ["From"] = $"<sip:them@example.test>;tag={fromTag}",
            ["To"] = $"<sip:us@example.test>;tag={toTag}",
            ["Call-ID"] = CallId,
            ["CSeq"] = $"2 {method}",
        };
        return new SipRequest(method, "sip:us@example.test", headers, string.Empty);
    }

    [Fact]
    public async Task A_bye_with_a_foreign_to_tag_is_rejected_with_481()
    {
        var (service, engine) = BuildService();
        var bye = InDialogRequest("BYE", toTag: "not-our-tag", fromTag: RemoteTag);

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), bye, default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 481);
        Assert.DoesNotContain(engine.Responses, r => r.StatusCode == 200);
    }

    [Fact]
    public async Task A_bye_with_a_foreign_from_tag_is_rejected_with_481()
    {
        var (service, engine) = BuildService();
        var bye = InDialogRequest("BYE", toTag: LocalTag, fromTag: "not-their-tag");

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), bye, default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 481);
    }

    [Fact]
    public async Task A_bye_with_the_matching_dialog_tags_passes_the_gate_and_is_answered_200()
    {
        var (service, engine) = BuildService();
        var bye = InDialogRequest("BYE", toTag: LocalTag, fromTag: RemoteTag);

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), bye, default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 200);
        Assert.DoesNotContain(engine.Responses, r => r.StatusCode == 481);
    }

    [Fact]
    public async Task An_ack_with_a_foreign_tag_is_dropped_without_a_response()
    {
        var (service, engine) = BuildService();
        var ack = InDialogRequest("ACK", toTag: "not-our-tag", fromTag: RemoteTag);

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), ack, default);

        Assert.Empty(engine.Responses); // ACK gets no response; the gate simply drops the foreign request.
    }
}
