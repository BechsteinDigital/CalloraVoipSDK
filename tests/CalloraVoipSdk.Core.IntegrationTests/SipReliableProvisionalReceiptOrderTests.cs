using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 3262 §4 in-order receipt of reliable provisional responses on the UAC side (CF-044): the first is
/// accepted with any RSeq, thereafter only the exact next RSeq; a gap waits for the missing response and a
/// duplicate/older RSeq is not re-acknowledged.
/// </summary>
public sealed class SipReliableProvisionalReceiptOrderTests
{
    [Fact]
    public void First_reliable_provisional_is_accepted_with_any_rseq()
    {
        Assert.True(new SipReliableProvisionalReceiptOrder().TryAcceptInOrder(1000));
    }

    [Fact]
    public void The_next_consecutive_rseqs_are_accepted()
    {
        var order = new SipReliableProvisionalReceiptOrder();

        Assert.True(order.TryAcceptInOrder(1000));
        Assert.True(order.TryAcceptInOrder(1001));
        Assert.True(order.TryAcceptInOrder(1002));
    }

    [Fact]
    public void A_gap_is_not_acknowledged_until_the_missing_rseq_arrives()
    {
        var order = new SipReliableProvisionalReceiptOrder();

        Assert.True(order.TryAcceptInOrder(1000));   // first — sets the expected sequence
        Assert.False(order.TryAcceptInOrder(1002));  // gap (expected 1001) → must not be acknowledged
        Assert.True(order.TryAcceptInOrder(1001));   // the retransmitted missing response → accept
        Assert.True(order.TryAcceptInOrder(1002));   // now back in order
    }

    [Fact]
    public void A_duplicate_or_older_rseq_is_rejected()
    {
        var order = new SipReliableProvisionalReceiptOrder();

        Assert.True(order.TryAcceptInOrder(1000));
        Assert.True(order.TryAcceptInOrder(1001));
        Assert.False(order.TryAcceptInOrder(1001)); // duplicate of the last acknowledged
        Assert.False(order.TryAcceptInOrder(1000)); // older than the last acknowledged
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_rseq_is_rejected(int rseq)
    {
        Assert.False(new SipReliableProvisionalReceiptOrder().TryAcceptInOrder(rseq));
    }
}
