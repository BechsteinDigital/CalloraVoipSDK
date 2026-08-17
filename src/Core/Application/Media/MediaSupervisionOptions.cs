namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Runtime supervision thresholds for active media sessions (#261, ADR-069). Media silence is reported to
/// the application; ending the call on a total loss of peer liveness — no RTP <em>and</em> no RTCP — is
/// available but off by default. A caller may tune both via <c>VoipConfiguration</c>.
/// </summary>
internal sealed record MediaSupervisionOptions
{
    /// <summary>Default supervision options.</summary>
    public static MediaSupervisionOptions Default { get; } = new();

    /// <summary>
    /// Hang up a connected call that has shown no sign of life this long — neither inbound RTP nor inbound
    /// RTCP. <see cref="TimeSpan.Zero"/> or negative disables the hangup, which is the
    /// <b>default</b>: media silence is reported through <c>ICall.MediaFlowChanged</c> and the application
    /// decides. 30 seconds is the recommended value for a deployment that wants the teardown.
    /// </summary>
    /// <remarks>
    /// Off by default because a media-silence teardown is a heuristic without a reliable counter-signal, and
    /// measurement says so (#261, ADR-069): against a real PBX in the media path — Asterisk and FreeSWITCH
    /// both, verified in the interop suite — inbound RTCP stops together with the media, so nothing
    /// distinguishes "the peer went quiet" from "the peer went away". Both of those PBXes ship their own
    /// equivalent (<c>rtp_timeout</c>, <c>media_timeout</c>) disabled for the same reason, and pjsip has no
    /// detection at all. A peer that is genuinely gone is still caught by the RFC 4028 session timer.
    /// When enabled, RTCP counts as liveness: it can only ever extend the deadline, never shorten it.
    /// </remarks>
    public TimeSpan InboundMediaTimeout { get; init; } = TimeSpan.Zero;

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
