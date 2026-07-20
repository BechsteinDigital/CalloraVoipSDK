namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Computes the RTCP transmission interval per RFC 3550 §6.2 / §6.3.1: a bandwidth-proportional, member- and
/// sender-scaled interval, randomised over [0.5, 1.5] of the deterministic value and divided by the
/// compensation factor <c>e − 1.5 ≈ 1.21828</c> so the long-run mean stays correct. This replaces a fixed
/// interval, whose synchronised reports across endpoints would defeat the RFC's timer-reconsideration design.
/// <para>
/// The deterministic interval is <c>max(Tmin, n · avg_rtcp_size / rtcp_bw)</c>. For a point-to-point session
/// the <c>Tmin</c> floor dominates, so the visible effect is the randomisation; for larger groups the interval
/// scales up with the member count so the aggregate RTCP traffic stays within the RTCP bandwidth share. The
/// first report uses half <c>Tmin</c> (RFC 3550 §6.2). The randomness source is injectable for deterministic
/// tests.
/// </para>
/// </summary>
internal sealed class RtcpTransmissionInterval
{
    // RFC 3550 §6.3.1: divide the randomised interval by e − 1.5 to compensate the [0.5,1.5] mean back to 1.
    private const double CompensationFactor = 1.21828;
    // §6.2: 25% of the RTCP bandwidth is reserved for active senders when they are a minority of members.
    private const double SenderBandwidthFraction = 0.25;
    // §6.3.1: the RTCP size used in the calculation includes the lower-layer transport/network headers.
    private const int LowerLayerOverheadBytes = 28; // IPv4 (20) + UDP (8)

    private readonly TimeSpan _minInterval;
    private readonly double _rtcpBandwidthBitsPerSecond;
    private readonly Func<double> _random;

    /// <summary>
    /// Creates the interval calculator.
    /// </summary>
    /// <param name="minInterval">The minimum interval <c>Tmin</c> (RFC 3550 §6.2, typically 5 s).</param>
    /// <param name="rtcpBandwidthBitsPerSecond">
    /// The session's RTCP bandwidth share in bits/second (RFC 3550 §6.2, conventionally 5% of the session
    /// bandwidth). Governs how fast the interval grows with the member count.
    /// </param>
    /// <param name="random">Returns a value in [0,1); injectable for deterministic tests. Defaults to <see cref="Random.Shared"/>.</param>
    public RtcpTransmissionInterval(TimeSpan minInterval, double rtcpBandwidthBitsPerSecond, Func<double>? random = null)
    {
        if (minInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minInterval), "The minimum RTCP interval must be positive.");
        if (rtcpBandwidthBitsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(rtcpBandwidthBitsPerSecond), "The RTCP bandwidth must be positive.");

        _minInterval = minInterval;
        _rtcpBandwidthBitsPerSecond = rtcpBandwidthBitsPerSecond;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>
    /// Computes the next randomised RTCP interval (RFC 3550 §6.3.1).
    /// </summary>
    /// <param name="members">The number of session members observed (≥ 1; self counts).</param>
    /// <param name="senders">The number of active senders observed.</param>
    /// <param name="weSent">Whether this endpoint has sent RTP since the last report (§6.2 sender sub-group).</param>
    /// <param name="averageRtcpSizeBytes">The running average RTCP compound size in bytes (payload only; the
    /// lower-layer header overhead is added internally).</param>
    /// <param name="initial">True for the very first report, which uses half <c>Tmin</c> (§6.2).</param>
    public TimeSpan Compute(int members, int senders, bool weSent, double averageRtcpSizeBytes, bool initial)
    {
        var n = Math.Max(members, 1);
        var rtcpBandwidth = _rtcpBandwidthBitsPerSecond;

        // §6.2: when senders are a minority (< 25% of members), split the bandwidth 25/75 between the sender
        // and receiver sub-groups and count only the members of our own sub-group, so senders report more often.
        if (senders > 0 && senders < members * SenderBandwidthFraction)
        {
            if (weSent)
            {
                rtcpBandwidth *= SenderBandwidthFraction;
                n = senders;
            }
            else
            {
                rtcpBandwidth *= 1 - SenderBandwidthFraction;
                n = members - senders;
            }
        }
        n = Math.Max(n, 1);

        var floor = initial ? _minInterval.TotalSeconds / 2 : _minInterval.TotalSeconds;
        var averageBits = (Math.Max(averageRtcpSizeBytes, 0) + LowerLayerOverheadBytes) * 8;
        var deterministic = Math.Max(floor, n * averageBits / rtcpBandwidth);

        // §6.3.1: randomise over [0.5, 1.5] of the deterministic interval, then compensate the mean.
        var randomised = deterministic * (0.5 + _random());
        return TimeSpan.FromSeconds(randomised / CompensationFactor);
    }
}
