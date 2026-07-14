using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Stamps DTLS-SRTP keying metadata onto negotiated media parameters (RFC 5763). Like
/// <see cref="CallMediaParametersSrtpEnricher"/> this is a pure function over the SDP
/// strings that actually went over the wire: DTLS engages only when both sides signaled
/// a certificate fingerprint and SDES did not already key the leg (the keying methods
/// are mutually exclusive); the local DTLS role is derived from the negotiated
/// <c>a=setup</c> values (RFC 4145 §4 / RFC 5763 §5).
/// </summary>
internal static class CallMediaParametersDtlsEnricher
{
    /// <summary>
    /// Clones <paramref name="parameters"/> with DTLS metadata stamped when the exchange
    /// negotiated DTLS-SRTP; returns the instance unchanged otherwise.
    /// </summary>
    /// <param name="parameters">Parameters already enriched with SRTP/ICE metadata.</param>
    /// <param name="remoteSdp">The peer's SDP as received on the wire.</param>
    /// <param name="localSdp">Our own SDP as sent on the wire (offer or answer).</param>
    public static CallMediaParameters Enrich(
        CallMediaParameters parameters,
        string remoteSdp,
        string? localSdp)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // SDES already keyed this leg — mutually exclusive with DTLS (never rekey a
        // running SDES call via a stray fingerprint).
        if (parameters.SrtpLocalKeyParams is not null)
            return parameters;

        var (remoteFingerprint, remoteSetup) = SdpUtilities.TryExtractAudioDtls(remoteSdp);
        var (localFingerprint, localSetup) = SdpUtilities.TryExtractAudioDtls(localSdp);

        // DTLS is negotiated only when both sides committed a certificate fingerprint
        // (RFC 5763 §5); anything less keeps the leg on its non-DTLS keying (or plain RTP).
        if (remoteFingerprint is null || localFingerprint is null)
            return parameters;

        return new CallMediaParameters
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
            IceControlling = parameters.IceControlling,
            LocalIceUfrag = parameters.LocalIceUfrag,
            LocalIcePwd = parameters.LocalIcePwd,
            LocalIceOptions = parameters.LocalIceOptions,
            LocalIceCandidates = parameters.LocalIceCandidates,
            RemoteIceUfrag = parameters.RemoteIceUfrag,
            RemoteIcePwd = parameters.RemoteIcePwd,
            RemoteIceOptions = parameters.RemoteIceOptions,
            RemoteIceCandidates = parameters.RemoteIceCandidates,
            RemoteIceEndOfCandidates = parameters.RemoteIceEndOfCandidates,
            AppliedSrtpPolicy = parameters.AppliedSrtpPolicy,
            SrtpDecisionReasonCode = parameters.SrtpDecisionReasonCode,
            SrtpSuite = parameters.SrtpSuite,
            IsSrtcpEncrypted = parameters.IsSrtcpEncrypted,
            SrtpLocalKeyParams = parameters.SrtpLocalKeyParams,
            SrtpRemoteKeyParams = parameters.SrtpRemoteKeyParams,
            Video = parameters.Video,
            IsDtlsNegotiated = true,
            DtlsIsClient = ResolveIsClient(localSetup, remoteSetup),
            DtlsRemoteFingerprintAlgorithm = remoteFingerprint.Algorithm,
            DtlsRemoteFingerprintValue = remoteFingerprint.Value,
        };
    }

    /// <summary>
    /// Derives the local DTLS role from the negotiated <c>a=setup</c> values (RFC 4145 §4):
    /// <c>active</c> is the client. When we sent <c>actpass</c> (offerer per RFC 5763 §5),
    /// the peer's answer decides — peer <c>passive</c> makes us active. Defaults to the
    /// client role when both sides were silent (WebRTC default for answerers).
    /// </summary>
    private static bool ResolveIsClient(string? localSetup, string? remoteSetup)
    {
        if (string.Equals(localSetup, "active", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(localSetup, "passive", StringComparison.OrdinalIgnoreCase))
            return false;

        // Local actpass (or absent): the peer's committed role decides ours.
        if (string.Equals(remoteSetup, "active", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(remoteSetup, "passive", StringComparison.OrdinalIgnoreCase))
            return true;

        return true;
    }
}
