namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Coarse, ready-to-use indicator of the media path's health, derived by the SDK from the congestion
/// signal (delay trend + loss). Meant for a simple UI hint or a plugin decision — read it instead of
/// interpreting raw metrics yourself.
/// </summary>
public enum NetworkQuality
{
    /// <summary>The path has headroom: delay is stable and loss is negligible.</summary>
    Good,

    /// <summary>The path shows mild stress: some loss or a rising delay trend, but still usable.</summary>
    Fair,

    /// <summary>The path is congested: delay is overusing or loss is high — reduce the send bitrate.</summary>
    Poor,
}
