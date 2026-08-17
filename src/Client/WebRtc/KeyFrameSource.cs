namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Where <see cref="EncodedFrame.IsKeyFrame"/> came from. Two sources answer that question, and they do not
/// survive end-to-end encryption equally well — a forwarder that needs to trust the flag needs to know which
/// one it got.
/// </summary>
/// <remarks>
/// Only relevant to video: audio frames are never key frames and always report <see cref="Unknown"/>.
/// </remarks>
public enum KeyFrameSource
{
    /// <summary>
    /// No key-frame signal was available. The frame's <see cref="EncodedFrame.IsKeyFrame"/> is
    /// <see langword="false"/> and means <em>unknown</em>, not <em>no</em>: this is what an opaque video
    /// session (<see cref="WebRtcConfiguration.OpaqueVideoFrames"/>) reports when the peer negotiated no
    /// Dependency Descriptor, and what every audio frame reports.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The sender's Dependency Descriptor, an RTP header extension written before any payload encryption.
    /// Trustworthy whatever the payload holds — this is the source to require when frames may be encrypted
    /// end to end (RFC 9605).
    /// </summary>
    RtpHeaderExtension,

    /// <summary>
    /// Derived from the payload by the depacketiser (the VP8 P bit, an H.264 IDR NAL unit). Authoritative
    /// for a session in the clear. For a sender that encrypts its frames, it holds only as far as that
    /// sender leaves the relevant bytes readable — H.264's NAL headers are left in the clear by every
    /// shipping implementation, VP8's key-frame bit is not guaranteed to be.
    /// </summary>
    Payload,
}
