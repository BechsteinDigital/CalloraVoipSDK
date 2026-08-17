using System.Net;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// The local configuration a <see cref="WebRtcPeerConnection"/> answers a remote WebRTC offer with:
/// the local media endpoint, the audio (and optional video) codec capabilities, and the DTLS identity
/// and ICE credentials. BUNDLE (RFC 8843) and rtcp-mux (RFC 8834) are always on for a WebRTC peer, so
/// they are not options here.
/// </summary>
internal sealed record WebRtcPeerOptions
{
    /// <summary>The local endpoint the shared media socket binds to and advertises.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>Local audio codec capabilities offered/accepted on the audio m-line.</summary>
    public required IReadOnlyList<SdpCodecDefinition> AudioCodecs { get; init; }

    /// <summary>
    /// The config-time video tracks the peer offers, in order (P2c). An empty list is an audio-only peer;
    /// a single entry is the historic <c>EnableVideo</c> primary video track (byte-identical to the pre-P2c
    /// single-video offer). Further tracks the app adds at runtime via
    /// <see cref="WebRtcPeerConnection.AddVideoTrack(WebRtcAddedVideoTrack)"/> are appended after these on the
    /// numeric-MID multi-track path.
    /// </summary>
    public IReadOnlyList<SdpVideoMediaOptions> VideoTracks { get; init; } = [];

    /// <summary>
    /// Whether a fixed 1+1 peer's first offer uses numeric MIDs. Runtime-added tracks always append with stable
    /// numeric MIDs and never change an existing m-line's identity/order (RFC 8829), independent of this flag.
    /// </summary>
    public bool UseStableNumericMediaIds { get; init; }

    /// <summary>
    /// Whether this peer's video frames are end-to-end encrypted before they reach the packetiser (WebRTC
    /// Encoded Transform / SFrame, RFC 9605) and their content must therefore never be read (#223, ADR-068).
    /// When set, every video track of the built session uses the opaque payload format: both halves work from
    /// the RTP framing alone, the frame travels verbatim, and no key-frame claim is derived from it.
    /// </summary>
    /// <remarks>
    /// Scoped to the peer, not the individual track: the requirement it serves (Anlage 31b BMV-Ä § 2 Abs. 3/4 —
    /// the provider must be unable to see content) is a property of the whole session, and SDP carries no
    /// per-m-line attribute for it, so a per-track switch would need a non-SDP policy channel through the
    /// session factory and the renegotiator. The opaque H.264 framing is self-consistent between two SDK
    /// endpoints; it is not what a browser emits (see ADR-068 "Interop scope").
    /// </remarks>
    public bool OpaqueVideoFrames { get; init; }

    /// <summary>Local DTLS-SRTP identity (fingerprint + setup role) signalled in the answer (RFC 5763).</summary>
    public required SdpDtlsParameters Dtls { get; init; }

    /// <summary>Local ICE credentials and candidates for the shared 5-tuple (RFC 8839).</summary>
    public required SdpIceParameters Ice { get; init; }

    /// <summary>
    /// STUN/TURN servers used to gather server-reflexive candidates (RFC 8445 §5.1.1). Empty gathers only
    /// the host candidate. STUN entries are queried through the pre-bound media socket during
    /// <see cref="WebRtcPeerConnection.GatherCandidatesAsync"/>.
    /// </summary>
    public IReadOnlyList<IceServerConfiguration> IceServers { get; init; } = [];
}
