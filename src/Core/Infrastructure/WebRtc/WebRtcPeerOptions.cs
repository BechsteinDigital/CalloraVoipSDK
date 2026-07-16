using System.Net;
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

    /// <summary>Local video media capabilities, or null for an audio-only peer.</summary>
    public SdpVideoMediaOptions? Video { get; init; }

    /// <summary>Local DTLS-SRTP identity (fingerprint + setup role) signalled in the answer (RFC 5763).</summary>
    public required SdpDtlsParameters Dtls { get; init; }

    /// <summary>Local ICE credentials and candidates for the shared 5-tuple (RFC 8839).</summary>
    public required SdpIceParameters Ice { get; init; }
}
