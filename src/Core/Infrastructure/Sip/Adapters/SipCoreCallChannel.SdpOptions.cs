using CalloraVoipSdk.Core.Application.Ports.Sdp;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// SDP negotiation-option assembly for <see cref="SipCoreCallChannel"/> (offer/answer,
/// hold/unhold re-offer, video). Kept in this partial file so the channel stays within
/// the per-file size limit; the methods are deeply coupled to the channel's negotiated
/// state (ICE description, SRTP/DTLS keying, video ports, origin version) and therefore
/// belong to the channel rather than a state-free collaborator.
/// </summary>
internal sealed partial class SipCoreCallChannel
{
    /// <summary>
    /// SDP options for a hold/unhold re-offer. Keeps SDES SRTP alive on a running secure
    /// call by re-advertising the live outbound key (no rekey, no downgrade to plain RTP);
    /// a plain call re-offers plain RTP unchanged.
    /// </summary>
    private SdpMediaNegotiationOptions BuildReofferSdpOptions()
    {
        // A re-offer conserves the call's established keying, never the initial-offer
        // preference: a DTLS-keyed call re-offers DTLS (fingerprint + actpass) so the
        // peer keeps the association; SDES calls re-advertise the live key instead —
        // forcing DTLS onto an established SDES/plain call would break hold/unhold
        // against non-DTLS peers.
        var srtpKey = _activeLocalSrtpKeyParams;
        var reofferDtls = _dtlsActiveOnCall;
        return BuildLocalSdpOptions(
            offerSrtpCrypto: srtpKey is not null && !reofferDtls,
            offerSrtpKeyParams: srtpKey,
            offerDtls: reofferDtls);
    }

    /// <summary>
    /// Creates optional SDP negotiation options from the currently gathered local ICE description.
    /// </summary>
    private SdpMediaNegotiationOptions? BuildSdpOptions()
    {
        if (_localIceDescription is null && _preferredCodecNames is null && !_videoEnabled)
            return null;

        return new SdpMediaNegotiationOptions
        {
            Ice = _localIceDescription is null
                ? null
                : new SdpIceNegotiationOptions
                {
                    Ufrag = _localIceDescription.Ufrag,
                    Pwd = _localIceDescription.Pwd,
                    Options = _localIceDescription.Options,
                    Candidates = _localIceDescription.Candidates
                },
            PreferredCodecNames = _preferredCodecNames,
            Video = BuildVideoOptions()
        };
    }

    /// <summary>
    /// Video negotiation parameters when video is enabled: the reserved local video port, the
    /// configured codec preference, and the live video SDES key so a re-offer re-advertises it
    /// (no rekey). <see langword="null"/> keeps the leg audio-only.
    /// </summary>
    private SdpVideoNegotiationOptions? BuildVideoOptions() =>
        _videoEnabled
            ? new SdpVideoNegotiationOptions
            {
                Port = _localVideoPort,
                PreferredCodecNames = _videoCodecNames,
                OfferSrtpKeyParams = _activeLocalVideoSrtpKeyParams,
                Candidates = _localIceDescription?.VideoCandidates ?? [],
            }
            : null;

    /// <summary>
    /// SDP options for a locally originated description (offer/answer/hold/unhold). Carries
    /// the stable per-leg origin session id and a session version incremented on every call —
    /// each represents a media change the peer must detect (RFC 4566 §5.2 / RFC 3264 §5).
    /// Retransmissions reuse the already-built SDP string and never call this, so the version
    /// stays put when nothing changed.
    /// </summary>
    private SdpMediaNegotiationOptions BuildLocalSdpOptions(
        bool offerSrtpCrypto = false,
        string? offerSrtpKeyParams = null,
        bool offerDtls = false)
    {
        var baseOptions = BuildSdpOptions();
        return new SdpMediaNegotiationOptions
        {
            Ice = baseOptions?.Ice,
            PreferredCodecNames = baseOptions?.PreferredCodecNames ?? _preferredCodecNames,
            OfferSrtpCrypto = offerSrtpCrypto,
            OfferSrtpKeyParams = offerSrtpKeyParams,
            // Identity always travels along: the answer path needs it to respond to a
            // DTLS offer even when we would not offer DTLS ourselves.
            Dtls = _dtlsOptions,
            OfferDtlsSrtp = offerDtls,
            Video = BuildVideoOptions(),
            SessionId = _sdpSessionId,
            SessionVersion = Interlocked.Increment(ref _sdpSessionVersion)
        };
    }
}
