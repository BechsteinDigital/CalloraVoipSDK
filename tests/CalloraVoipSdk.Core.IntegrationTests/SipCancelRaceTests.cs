using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SipCancelRaceTests
{
    [Fact]
    public async Task SendInviteTransactionAsync_WhenCancelRequestedButInviteSucceeds_SendsAckThenBye()
    {
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));

        var cancelSent = false;
        transport.ResponseFactory = request =>
        {
            if (request.Method.Equals("INVITE", StringComparison.Ordinal) && !cancelSent)
            {
                cancelSent = true;
                service.SendCancelAsync(CancellationToken.None).GetAwaiter().GetResult();
                return CreateResponse(request, 200, "OK");
            }

            if (request.Method.Equals("CANCEL", StringComparison.Ordinal)
                || request.Method.Equals("BYE", StringComparison.Ordinal))
            {
                return CreateResponse(request, 200, "OK");
            }

            return null;
        };

        await service.SendInviteTransactionAsync(
            body: "v=0\r\n",
            allowRingingTransition: true,
            successState: SipDialogState.Established,
            CancellationToken.None);

        var methods = transport.SnapshotRequests().Select(r => r.Method).ToArray();
        Assert.Equal(["INVITE", "CANCEL", "ACK", "BYE"], methods);
    }

    private static SipResponse CreateResponse(
        CapturedSipRequest request,
        int statusCode,
        string reasonPhrase)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = request.Headers["Via"],
            ["From"] = request.Headers["From"],
            ["To"] = EnsureRemoteTag(request.Headers["To"]),
            ["Call-ID"] = request.Headers["Call-ID"],
            ["CSeq"] = request.Headers["CSeq"],
            ["Contact"] = "<sip:bob@192.0.2.10:5060>"
        };

        return new SipResponse(statusCode, reasonPhrase, headers, string.Empty);
    }

    private static string EnsureRemoteTag(string toHeader)
    {
        if (SipProtocol.ExtractTag(toHeader) is not null)
            return toHeader;

        return $"{toHeader};tag=remote-tag";
    }
}
