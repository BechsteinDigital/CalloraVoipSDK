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
            AppliedSrtpPolicy = appliedPolicy,
            SrtpDecisionReasonCode = reasonCode,
            SrtpSuite = sdesUsable ? remoteCrypto!.CryptoSuite : null,
            IsSrtcpEncrypted = sdesUsable,
            SrtpLocalKeyParams = sdesUsable ? localCrypto!.KeyParams : null,
            SrtpRemoteKeyParams = sdesUsable ? remoteCrypto!.KeyParams : null
        };
    }
}
