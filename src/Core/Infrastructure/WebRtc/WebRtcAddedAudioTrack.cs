using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// An audio track added to a <see cref="WebRtcPeerConnection"/> before the first offer (or mid-call, pending the
/// next renegotiation cycle) via the public <c>AddAudioTrack</c> surface (4.7.0). It carries the per-track facts
/// the multi-track offer path needs beyond the primary audio anchor — the codecs to offer, the negotiated
/// direction, and the MediaStream id — so an added track can differ from the peer's implicit primary audio track
/// (the SFU pattern of one audio stream per remote participant). Adding one switches the peer to the numeric-MID
/// multi-track path (RFC 8843). Audio has no simulcast, so there is no per-layer field here.
/// </summary>
internal sealed record WebRtcAddedAudioTrack
{
    /// <summary>Audio codec capabilities to offer on this track's m-line (e.g. Opus/G.711).</summary>
    public required IReadOnlyList<SdpCodecDefinition> Codecs { get; init; }

    /// <summary>The negotiated direction of this track's m-line (RFC 3264). Defaults to <see cref="SdpMediaDirection.SendRecv"/>.</summary>
    public SdpMediaDirection Direction { get; init; } = SdpMediaDirection.SendRecv;

    /// <summary>The WebRTC MediaStream id (a=msid stream id, RFC 8830), or null for the peer's default stream.</summary>
    public string? StreamId { get; init; }
}
