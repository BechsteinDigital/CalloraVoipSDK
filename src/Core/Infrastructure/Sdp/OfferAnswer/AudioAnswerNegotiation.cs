using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// The outcome of negotiating one audio answer m-line (RFC 3264 §6): the built m-line plus the fields the
/// session-level <see cref="SdpOfferAnswerResult"/> derives from the <em>primary</em> audio m-line (the
/// negotiated codecs and the resolved keying). Extracting this lets the primary audio m-line and every
/// further audio m-line under BUNDLE (multi-track) run through the same negotiation and key identically —
/// only the primary one feeds the result. A top-level type (ENGINEERING R4: no nested helper types).
/// </summary>
internal sealed record AudioAnswerNegotiation(
    SdpMediaDescription Media,
    IReadOnlyList<SdpCodecDefinition> NegotiatedCodecs,
    bool RtcpMuxNegotiated,
    SdpFingerprint? RemoteFingerprint,
    string? RemoteDtlsSetup,
    SdpCryptoAttribute? NegotiatedCrypto,
    SdpCryptoAttribute? LocalCrypto);
