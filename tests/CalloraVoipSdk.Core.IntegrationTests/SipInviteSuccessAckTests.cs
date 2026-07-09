using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SipInviteSuccessAckTests
{
    [Fact]
    public async Task ForkedInvite_ack_send_failure_is_logged_at_warning()
    {
        var logger = new CapturingLogger();
        // Fail the fork ACK send so the fire-and-forget fork handler hits its catch block.
        var transport = new CapturingSipTransportRuntime { ThrowOnSendMethod = "ACK" };
        var context = new AckTestSipCallSessionContext(transport, logger)
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

        // Fork handling is fire-and-forget; poll until the failure is recorded.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && !logger.Entries.Any(e => e.Level == LogLevel.Warning))
            await Task.Delay(20);

        var warning = Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("forked INVITE", warning.Message, StringComparison.OrdinalIgnoreCase);
        // A fork-handling failure must not be logged at the near-invisible Debug level.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("forked INVITE", StringComparison.OrdinalIgnoreCase));
    }

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
