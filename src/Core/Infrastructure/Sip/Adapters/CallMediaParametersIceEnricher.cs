using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Merges the local ICE description generated for a call channel into parsed remote media
/// parameters, producing the ICE-enriched <see cref="CallMediaParameters"/> the media layer
/// consumes. Extracted as a dedicated unit so the SIP channel adapter stays focused on signalling.
/// </summary>
internal static class CallMediaParametersIceEnricher
{
    /// <summary>
    /// Returns <paramref name="parameters"/> enriched with the local ICE credentials, candidates
    /// and role (<paramref name="iceControlling"/>, RFC 8445 §5.1.1). When no local ICE description
    /// was generated the input is returned unchanged. SRTP key material is applied separately.
    /// </summary>
    /// <param name="parameters">The parsed remote media parameters.</param>
    /// <param name="localIceDescription">The local ICE description, or null when ICE is not used.</param>
    /// <param name="iceControlling">Whether this agent holds the ICE controlling role.</param>
    public static CallMediaParameters Enrich(
        CallMediaParameters parameters,
        CallIceLocalDescription? localIceDescription,
        bool iceControlling)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (localIceDescription is null)
            return parameters;

        var iceEnabled = parameters.IceEnabled
                         || !string.IsNullOrWhiteSpace(parameters.RemoteIceUfrag)
                         || !string.IsNullOrWhiteSpace(parameters.RemoteIcePwd)
                         || parameters.RemoteIceCandidates.Count > 0;

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
            IceEnabled = iceEnabled,
            IceControlling = iceControlling,
            LocalIceUfrag = localIceDescription.Ufrag,
            LocalIcePwd = localIceDescription.Pwd,
            LocalIceOptions = localIceDescription.Options,
            LocalIceCandidates = localIceDescription.Candidates,
            RemoteIceUfrag = parameters.RemoteIceUfrag,
            RemoteIcePwd = parameters.RemoteIcePwd,
            RemoteIceOptions = parameters.RemoteIceOptions,
            RemoteIceCandidates = parameters.RemoteIceCandidates,
            RemoteIceEndOfCandidates = parameters.RemoteIceEndOfCandidates,
            Video = parameters.Video
        };
    }
}
