namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// What one media-metrics observation means for a supervised call (#261, ADR-069).
/// </summary>
internal enum MediaSupervisionVerdict
{
    /// <summary>Nothing to report: media is flowing, or the silence has not reached a threshold yet.</summary>
    None,

    /// <summary>
    /// Inbound media has gone silent while the peer is still demonstrably alive (RTCP keeps arriving). A
    /// notification for the application, not a reason to end the call.
    /// </summary>
    MediaSilent,

    /// <summary>Inbound media resumed after a reported silence.</summary>
    MediaResumed,

    /// <summary>
    /// The peer has stopped sending everything — no RTP and no RTCP — for the configured liveness timeout.
    /// Returned at most once per call; the call is ended.
    /// </summary>
    PeerGone,
}

/// <summary>
/// The verdict of one observation together with how long inbound media had been silent when it was made.
/// </summary>
/// <param name="Verdict">What the observation means.</param>
/// <param name="SilenceDuration">
/// Length of the inbound-media silence at the moment of the verdict; on
/// <see cref="MediaSupervisionVerdict.MediaResumed"/> the length of the silence that just ended.
/// </param>
internal readonly record struct MediaSupervisionOutcome(
    MediaSupervisionVerdict Verdict,
    TimeSpan SilenceDuration)
{
    /// <summary>Nothing to report.</summary>
    public static MediaSupervisionOutcome None { get; } = new(MediaSupervisionVerdict.None, TimeSpan.Zero);
}
