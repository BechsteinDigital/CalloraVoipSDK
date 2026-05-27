using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SipReliableProvisionalManagerTests
{
    [Fact]
    public async Task WaitForPrackAsync_ReturnsTrueWhenPrackArrivedBeforeWaitStarts()
    {
        using var manager = new SipReliableProvisionalManager(NullLogger.Instance);
        var rseq = manager.RegisterPendingInviteProvisional(inviteCseq: 42);

        var acknowledged = manager.TryAcknowledge(
            $"{rseq} 42 INVITE",
            out var rejectionStatusCode,
            out var rejectionReasonPhrase);

        var waitResult = await manager.WaitForPrackAsync(
            rseq,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(acknowledged);
        Assert.Equal(0, rejectionStatusCode);
        Assert.Equal(string.Empty, rejectionReasonPhrase);
        Assert.True(waitResult);
    }
}
