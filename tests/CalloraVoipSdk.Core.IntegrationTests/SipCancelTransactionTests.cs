using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SipCancelTransactionTests
{
    [Fact]
    public async Task SendCancelAsync_ReusesInviteBranchAndUsesClientTransaction()
    {
        var transport = new CapturingSipTransportRuntime();
        transport.ResponseFactory = CreateOkResponse;
        var context = new AckTestSipCallSessionContext(transport)
        {
            ActiveInviteCSeq = 11,
            ActiveInviteBranch = "z9hG4bK-invite-branch"
        };
        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));

        await service.SendCancelAsync(CancellationToken.None);

        var cancel = await transport.WaitForRequestAsync("CANCEL", TimeSpan.FromSeconds(1));
        Assert.Equal(context.ActiveInviteBranch, SipProtocol.ExtractViaBranch(cancel.Headers["Via"]));
        Assert.Equal($"{context.ActiveInviteCSeq} CANCEL", cancel.Headers["CSeq"]);
        Assert.Equal(1, transport.ResponseSubscriptionsCreated);
    }

    private static SipResponse? CreateOkResponse(CapturedSipRequest request)
    {
        if (!request.Method.Equals("CANCEL", StringComparison.Ordinal))
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = request.Headers["Via"],
            ["From"] = request.Headers["From"],
            ["To"] = request.Headers["To"],
            ["Call-ID"] = request.Headers["Call-ID"],
            ["CSeq"] = request.Headers["CSeq"]
        };

        return new SipResponse(200, "OK", headers, string.Empty);
    }
}
