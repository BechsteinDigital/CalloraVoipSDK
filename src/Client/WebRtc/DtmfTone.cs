namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// One fully received inbound DTMF tone (RFC 4733 telephone-event), surfaced on
/// <see cref="IPeerConnection.DtmfReceived"/> once per tone after its end-of-event packet.
/// </summary>
/// <param name="ToneCode">
/// The DTMF event code (RFC 4733 §3.2): 0–9 for the digits, 10 for <c>*</c>, 11 for <c>#</c>, and 12–15
/// for the A–D tones.
/// </param>
/// <param name="DurationMs">The reassembled tone duration in milliseconds.</param>
public readonly record struct DtmfTone(byte ToneCode, int DurationMs);
