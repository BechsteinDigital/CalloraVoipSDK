namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Where an inbound frame's key-frame flag came from (#310) — the Core-internal counterpart of the SDK's
/// public <c>KeyFrameSource</c>.
/// </summary>
/// <remarks>
/// The distinction exists because the two sources are not equally trustworthy under end-to-end encryption
/// (RFC 9605): the RTP header stays readable by design, the payload does not. Reference stacks report the
/// merged answer only — libwebrtc, mediasoup and LiveKit all prefer the Dependency Descriptor and fall back
/// to payload parsing without saying which they used. Carrying the provenance is the small piece this SDK
/// adds on top, because a forwarder built on it can then decide how much to trust a given frame.
/// </remarks>
internal enum VideoKeyFrameSource
{
    /// <summary>
    /// No key-frame signal was available: the payload format does not read the payload (the opaque path,
    /// #223) and no descriptor arrived. The flag is <see langword="false"/> and means "unknown", not "no".
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The sender's Dependency Descriptor (#225). Written before any encryption and readable by a forwarder,
    /// so this answer holds whatever the payload contains.
    /// </summary>
    RtpHeaderExtension,

    /// <summary>
    /// Derived by the depacketiser from the payload (VP8 P bit, H.264 IDR NAL type). Authoritative for a
    /// stream in the clear. For a partially encrypted sender it depends on which bytes were left readable —
    /// see <c>docs/adr/ADR-071-payload-reads-under-partial-encryption.md</c>.
    /// </summary>
    Payload,
}
