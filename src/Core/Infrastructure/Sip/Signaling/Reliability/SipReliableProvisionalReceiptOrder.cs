namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Enforces the RFC 3262 §4 in-order receipt rule for reliable provisional INVITE responses on the UAC side
/// (CF-044). A UAC acknowledges (PRACKs) a reliable provisional only when its <c>RSeq</c> is the next one in
/// sequence: the first reliable provisional of a dialog is accepted with any RSeq and sets the expected value,
/// and thereafter only the exact next RSeq is accepted. A gap (an RSeq higher than expected) must not be
/// acknowledged — the UAC waits for the retransmission of the missing response — and a duplicate or an older
/// RSeq is likewise not re-acknowledged. This prevents PRACKing out of order, which the earlier
/// "acknowledge any strictly higher RSeq" logic did for a gap.
/// </summary>
internal sealed class SipReliableProvisionalReceiptOrder
{
    private readonly object _sync = new();
    private int? _nextExpectedRseq;

    /// <summary>
    /// Decides whether a reliable provisional response with the given <paramref name="rseq"/> is the next one in
    /// order and should be acknowledged with PRACK. Returns <see langword="true"/> for the first reliable
    /// provisional (any positive RSeq) or the exact next expected RSeq, advancing the expected value; returns
    /// <see langword="false"/> for a non-positive RSeq, a gap (higher than expected), or a duplicate/older RSeq.
    /// Thread-safe.
    /// </summary>
    /// <param name="rseq">The RSeq of the received reliable provisional response.</param>
    public bool TryAcceptInOrder(int rseq)
    {
        if (rseq <= 0)
            return false;

        lock (_sync)
        {
            if (_nextExpectedRseq is not { } expected)
            {
                // RFC 3262 §4: the first reliable provisional of a dialog may carry any RSeq; adopt it and expect
                // the next one to be one higher.
                _nextExpectedRseq = unchecked(rseq + 1);
                return true;
            }

            if (rseq != expected)
                return false; // gap (higher) or duplicate/older (lower) — must not be acknowledged.

            _nextExpectedRseq = unchecked(rseq + 1);
            return true;
        }
    }
}
