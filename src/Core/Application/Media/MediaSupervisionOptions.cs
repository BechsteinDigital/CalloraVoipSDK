namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Runtime supervision thresholds for active media sessions. Defaults preserve the
/// established behavior; a caller may tune them via <c>VoipConfiguration</c>.
/// </summary>
internal sealed record MediaSupervisionOptions
{
    /// <summary>Default supervision options.</summary>
    public static MediaSupervisionOptions Default { get; } = new();

    /// <summary>
    /// Hang up a connected call whose inbound RTP has been silent this long. Behind NAT a
    /// far-end BYE may never reach us (it targets our in-dialog Contact) and the media simply
    /// stops; this bounds how long the agent keeps talking to a dead line.
    /// <see cref="TimeSpan.Zero"/> or negative disables the check. Default: 15 seconds.
    /// </summary>
    public TimeSpan InboundMediaTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether the inbound-media timeout also tears down a call that is on hold. A held call
    /// legitimately carries no inbound RTP, so the default leaves held calls untouched.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool HangupHeldCallOnSilence { get; init; }
}
