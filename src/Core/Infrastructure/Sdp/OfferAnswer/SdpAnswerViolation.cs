namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// The kind of RFC 3264 §6 violation found in a remote answer (#160 P1-2b).
/// </summary>
/// <remarks>
/// The validator used to return only a human-readable string, so a caller could log the problem but not
/// react to it — every rejection looked alike, and a test could only assert on prose. The reason is now a
/// value; the message stays alongside it for the log line.
/// </remarks>
internal enum SdpAnswerViolation
{
    /// <summary>The answer has a different number of m-lines than the offer.</summary>
    MediaSectionCount,

    /// <summary>An answered m-line has a different media type than the offered one at that index.</summary>
    MediaType,

    /// <summary>An answered m-line carries a different MID than the offered one.</summary>
    Mid,

    /// <summary>An answered m-line uses a different transport profile than the offered one.</summary>
    Profile,

    /// <summary>The answer selects a payload type the offer did not contain.</summary>
    UnofferedPayloadType,

    /// <summary>The answer's BUNDLE group names a MID that was not in the offered group.</summary>
    BundleMidNotOffered,

    /// <summary>The offer required BUNDLE and the answer carries no group.</summary>
    BundleMissing,

    /// <summary>The answered direction is not a legal response to the offered one (RFC 3264 §6.1).</summary>
    Direction,

    /// <summary>The answer enables <c>rtcp-mux</c> although the offer did not.</summary>
    RtcpMuxNotOffered,

    /// <summary>The answer's DTLS setup role is not a legal response to the offered one (RFC 5763 §5).</summary>
    DtlsSetupRole,

    /// <summary>The answer requests RTCP feedback that was not offered for that payload type.</summary>
    UnofferedRtcpFeedback,

    /// <summary>The answer maps a header extension URI, or an id for it, that was not offered.</summary>
    UnofferedHeaderExtension,

    /// <summary>The answer carries format parameters for a payload type the offer had none for.</summary>
    UnofferedFormatParameters,

    /// <summary>An RTX payload type points at an associated payload type that was not offered.</summary>
    RtxAssociatedPayloadTypeNotOffered,
}
