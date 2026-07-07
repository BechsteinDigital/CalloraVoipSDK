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

    [Fact]
    public void RegisterPendingInviteProvisional_ProducesPositiveMonotonicRseq()
    {
        using var manager = new SipReliableProvisionalManager(NullLogger.Instance);

        var first  = manager.RegisterPendingInviteProvisional(inviteCseq: 1);
        var second = manager.RegisterPendingInviteProvisional(inviteCseq: 1);
        var third  = manager.RegisterPendingInviteProvisional(inviteCseq: 1);

        // RSeq must be a valid RFC 3262 sequence number: strictly positive and increasing.
        // Guards the thread-safe Random.Shared seeding (no 0-series from a corrupted Random).
        Assert.True(first > 0);
        Assert.Equal(first + 1, second);
        Assert.Equal(second + 1, third);
    }
}
