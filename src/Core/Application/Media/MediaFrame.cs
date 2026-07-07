namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// One encoded audio frame on the public media contract. The payload carries codec data as
/// negotiated for the call (identified by <paramref name="PayloadType"/>, e.g. 0 = PCMU,
/// 8 = PCMA, 9 = G.722); it is not decoded PCM.
/// </summary>
/// <param name="Payload">Encoded audio payload bytes.</param>
/// <param name="PayloadType">RTP payload type identifying the codec.</param>
/// <param name="DurationRtpUnits">Frame duration in RTP clock units (e.g. 160 for 20 ms at 8 kHz).</param>
public readonly record struct MediaFrame(
    ReadOnlyMemory<byte> Payload,
    int PayloadType,
    uint DurationRtpUnits);
