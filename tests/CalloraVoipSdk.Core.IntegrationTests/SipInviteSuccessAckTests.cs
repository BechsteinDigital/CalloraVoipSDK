using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SipInviteSuccessAckTests
{
    [Fact]
    public async Task HandleInboundResponse_RetransmittedSelectedInviteSuccess_SendsAck()
    {
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport)
        {
            ActiveInviteCSeq = 7,
            ActiveInviteBranch = null,
            RemoteTag = "remote-tag"
        };
        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));
        var response = CreateInviteSuccessResponse(
            context.CallId,
            inviteCseq: context.ActiveInviteCSeq,
            remoteTag: context.RemoteTag);

        service.HandleInboundResponse(context.RemoteEndPoint, response);

        var ack = await transport.WaitForRequestAsync("ACK", TimeSpan.FromSeconds(1));
        Assert.Equal("ACK", ack.Method);
        Assert.Equal($"{context.ActiveInviteCSeq} ACK", ack.Headers["CSeq"]);
        Assert.Contains($"tag={context.RemoteTag}", ack.Headers["To"], StringComparison.Ordinal);
    }

    private static SipResponse CreateInviteSuccessResponse(
        string callId,
        int inviteCseq,
        string? remoteTag)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.10:5060;branch=z9hG4bK-test",
            ["From"] = "<sip:alice@example.test>;tag=local-tag",
            ["To"] = $"<sip:bob@example.test>;tag={remoteTag}",
            ["Call-ID"] = callId,
            ["CSeq"] = $"{inviteCseq} INVITE",
            ["Contact"] = "<sip:bob@192.0.2.10:5060>"
        };

        return new SipResponse(200, "OK", headers, string.Empty);
    }
}
