namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Runtime supervision thresholds for active media sessions (#261, ADR-069). Two stages: media silence is
/// reported to the application, and only a total loss of peer liveness — no RTP <em>and</em> no RTCP — ends
/// the call. A caller may tune both via <c>VoipConfiguration</c>.
/// </summary>
internal sealed record MediaSupervisionOptions
{
    /// <summary>Default supervision options.</summary>
    public static MediaSupervisionOptions Default { get; } = new();

    /// <summary>
    /// Hang up a connected call that has shown no sign of life this long — neither inbound RTP nor inbound
    /// RTCP. Behind NAT a far-end BYE may never reach us (it targets our in-dialog Contact) and everything
    /// simply stops; this bounds how long the agent keeps talking to a dead line.
    /// <see cref="TimeSpan.Zero"/> or negative disables the hangup. Default: 30 seconds.
    /// </summary>
    /// <remarks>
    /// RTCP counts as liveness on purpose (#261): a peer that is alive but sending no media — silence
    /// suppression (RFC 3389), hold, a bridge switch mid-transfer — keeps reporting on the RFC 3550 §6.2
    /// interval. Supervising RTP alone hung such calls up while the peer was demonstrably reachable. 30 s
    /// matches SIPSorcery's <c>NoActivityTimeout</c>; Asterisk (<c>rtp_timeout</c>) and FreeSWITCH
    /// (<c>media_timeout</c>) default the equivalent to off, and pjsip has no built-in detection at all.
    /// </remarks>
    public TimeSpan InboundMediaTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Report inbound media silence to the application after this long without inbound RTP, through
    /// <c>ICall.MediaFlowChanged</c> — a notification, never a teardown. It fires while the peer is still
    /// demonstrably alive, so an application can play a prompt, escalate, or end the call on its own policy
    /// long before <see cref="InboundMediaTimeout"/> would.
    /// <see cref="TimeSpan.Zero"/> or negative disables the notification. Default: 15 seconds.
    /// </summary>
    public TimeSpan MediaSilenceNotifyAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether the liveness timeout also tears down a call that is on hold. A held call legitimately carries
    /// no inbound media, so the default leaves held calls untouched (matching Asterisk's separate
    /// <c>rtp_timeout_hold</c> and SIPSorcery, which skips both local and remote hold).
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool HangupHeldCallOnSilence { get; init; }
}
