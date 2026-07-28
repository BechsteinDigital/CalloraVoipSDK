using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// A video track added to a <see cref="WebRtcPeerConnection"/> at runtime before the first offer (P2c: the
/// public <c>AddVideoTrack</c> surface). It carries the per-track facts the multi-track offer path needs
/// beyond the config-time <see cref="Sdp.OfferAnswer.SdpVideoMediaOptions"/> — the negotiated direction and
/// the MediaStream id — so an added track can differ from the primary <c>EnableVideo</c> track. Adding one
/// switches the peer to the numeric-MID multi-track path (RFC 8843).
/// </summary>
internal sealed record WebRtcAddedVideoTrack
{
    /// <summary>Video codec capabilities to offer on this track's m-line (e.g. VP8/H264 at 90 kHz).</summary>
    public required IReadOnlyList<SdpCodecDefinition> Codecs { get; init; }

    /// <summary>The negotiated direction of this track's m-line (RFC 3264). Defaults to <see cref="SdpMediaDirection.SendRecv"/>.</summary>
    public SdpMediaDirection Direction { get; init; } = SdpMediaDirection.SendRecv;

    /// <summary>Send-side simulcast layer ids (RFC 8853); empty offers a single video stream.</summary>
    public IReadOnlyList<string> SimulcastSendRids { get; init; } = [];

    /// <summary>The WebRTC MediaStream id (a=msid stream id, RFC 8830), or null for the peer's default stream.</summary>
    public string? StreamId { get; init; }
}
