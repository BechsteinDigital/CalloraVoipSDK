using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Stamps SRTP policy metadata and recovers SDES key material onto negotiated media
/// parameters for public consumption. Extracted from <see cref="SipCoreCallChannel"/> so the
/// channel stays focused; kept a pure function (its only inputs are the SDP strings that went
/// over the wire plus the applied policy).
/// </summary>
internal static class CallMediaParametersSrtpEnricher
{
    /// <summary>
    /// Clones <paramref name="parameters"/> and stamps SRTP metadata. SDES key material is
    /// recovered from the SDP that actually went over the wire: the peer's SDP carries their
    /// key (inbound decrypt), our own local description (<paramref name="localSdp"/> — the
    /// answer we sent on the inbound leg or the offer on the outbound leg) carries the key we
    /// generated (outbound encrypt). Encryption only engages when both are present and agree on
    /// the suite — never half-encrypted.
    /// </summary>
    public static CallMediaParameters Enrich(
        CallMediaParameters parameters,
        string reasonCode,
        string remoteSdp,
        string? localSdp,
        SrtpPolicy appliedPolicy)
    {
        var remoteCrypto = SdpUtilities.TryExtractAudioCrypto(remoteSdp);
        var localCrypto = SdpUtilities.TryExtractAudioCrypto(localSdp);
        var sdesUsable = remoteCrypto is not null
            && localCrypto is not null
            && string.Equals(remoteCrypto.CryptoSuite, localCrypto.CryptoSuite, StringComparison.Ordinal);

        var video = EnrichVideo(parameters.Video, remoteSdp, localSdp);

        return new()
        {
            LocalEndPoint = parameters.LocalEndPoint,
            RemoteEndPoint = parameters.RemoteEndPoint,
            RtcpMux = parameters.RtcpMux,
            LocalRtcpEndPoint = parameters.LocalRtcpEndPoint,
            RemoteRtcpEndPoint = parameters.RemoteRtcpEndPoint,
            PayloadType = parameters.PayloadType,
            CodecName = parameters.CodecName,
            PayloadTypeCodecMap = parameters.PayloadTypeCodecMap,
            TelephoneEventPayloadType = parameters.TelephoneEventPayloadType,
            ClockRate = parameters.ClockRate,
            SamplesPerPacket = parameters.SamplesPerPacket,
            MediaProfile = parameters.MediaProfile,
            IsSrtpNegotiated = parameters.IsSrtpNegotiated,
            IceEnabled = parameters.IceEnabled,
            LocalIceUfrag = parameters.LocalIceUfrag,
            LocalIcePwd = parameters.LocalIcePwd,
            LocalIceOptions = parameters.LocalIceOptions,
            LocalIceCandidates = parameters.LocalIceCandidates,
            RemoteIceUfrag = parameters.RemoteIceUfrag,
            RemoteIcePwd = parameters.RemoteIcePwd,
            RemoteIceOptions = parameters.RemoteIceOptions,
            RemoteIceCandidates = parameters.RemoteIceCandidates,
            RemoteIceEndOfCandidates = parameters.RemoteIceEndOfCandidates,
            Video = video,
            AppliedSrtpPolicy = appliedPolicy,
            SrtpDecisionReasonCode = reasonCode,
            SrtpSuite = sdesUsable ? remoteCrypto!.CryptoSuite : null,
            IsSrtcpEncrypted = sdesUsable,
            SrtpLocalKeyParams = sdesUsable ? localCrypto!.KeyParams : null,
            SrtpRemoteKeyParams = sdesUsable ? remoteCrypto!.KeyParams : null
        };
    }

    /// <summary>
    /// Recovers per-m-line SDES key material for the video stream (RFC 4568), the same way the
    /// audio path does: the peer's SDP carries their key (inbound decrypt), our own local
    /// description carries the key we generated (outbound encrypt). Returns the video parameters
    /// with the SDES fields stamped when both keys are present and agree on the suite; otherwise
    /// the original video parameters unchanged (a keyed video leg without usable keys stays
    /// fail-closed-silent). <see langword="null"/> passes through for an audio-only leg.
    /// </summary>
    // INVARIANT: the ICE enricher runs before this one (see SipCoreCallChannel: Ice → Srtp → Dtls),
    // so an incoming `video` already carries its ICE credentials/role. Both branches below must
    // therefore preserve them — the early return does so implicitly, the SDES rebuild explicitly.
    private static CallVideoParameters? EnrichVideo(CallVideoParameters? video, string remoteSdp, string? localSdp)
    {
        if (video is null)
            return null;

        var remoteCrypto = SdpUtilities.TryExtractVideoCrypto(remoteSdp);
        var localCrypto = SdpUtilities.TryExtractVideoCrypto(localSdp);
        var sdesUsable = remoteCrypto is not null
            && localCrypto is not null
            && string.Equals(remoteCrypto.CryptoSuite, localCrypto.CryptoSuite, StringComparison.Ordinal);
        if (!sdesUsable)
            return video;

        return new CallVideoParameters
        {
            PayloadType = video.PayloadType,
            CodecName = video.CodecName,
            ClockRate = video.ClockRate,
            FormatParameters = video.FormatParameters,
            RtxPayloadType = video.RtxPayloadType,
            RemoteSupportsNack = video.RemoteSupportsNack,
            RemoteSupportsPli = video.RemoteSupportsPli,
            LocalEndPoint = video.LocalEndPoint,
            RemoteEndPoint = video.RemoteEndPoint,
            SrtpSuite = remoteCrypto!.CryptoSuite,
            SrtpLocalKeyParams = localCrypto!.KeyParams,
            SrtpRemoteKeyParams = remoteCrypto.KeyParams,
            // Carry the ICE parameters the ICE enricher already stamped — the video stream needs
            // them to attach ICE to its 5-tuple, and the SDES rebuild must not drop them.
            IceEnabled = video.IceEnabled,
            IceControlling = video.IceControlling,
            LocalIceUfrag = video.LocalIceUfrag,
            LocalIcePwd = video.LocalIcePwd,
            RemoteIceUfrag = video.RemoteIceUfrag,
            RemoteIcePwd = video.RemoteIcePwd,
            TransportWideCcExtensionId = video.TransportWideCcExtensionId
        };
    }
}
