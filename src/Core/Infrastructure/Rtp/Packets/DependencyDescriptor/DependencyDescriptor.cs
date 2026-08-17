namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// One parsed Dependency Descriptor (AV1 RTP specification §A, negotiated as
/// <see cref="RtpHeaderExtensionUris.DependencyDescriptor"/>): per-frame key-frame and layer information
/// carried in the RTP header instead of the payload (#225).
/// </summary>
/// <remarks>
/// This is what makes a forwarder — and this SDK's own receive path — independent of the payload: for an
/// end-to-end encrypted stream (#223) the payload is ciphertext, so the key-frame flag derived from it is
/// worthless, while the descriptor is written by the sender before encryption and stays readable.
/// </remarks>
internal sealed record DependencyDescriptor
{
    /// <summary>Whether this packet carries the first byte of its frame.</summary>
    public required bool StartOfFrame { get; init; }

    /// <summary>Whether this packet carries the last byte of its frame.</summary>
    public required bool EndOfFrame { get; init; }

    /// <summary>The frame dependency template this frame is built from (0..63).</summary>
    public required int TemplateId { get; init; }

    /// <summary>The sender's frame counter, strictly monotonic and wrapping at 16 bits.</summary>
    public required ushort FrameNumber { get; init; }

    /// <summary>
    /// The template structure carried by this descriptor, present only at the start of a coded video
    /// sequence — which is exactly what marks a key frame (see <see cref="StartsCodedVideoSequence"/>).
    /// </summary>
    public DependencyDescriptorStructure? Structure { get; init; }

    /// <summary>
    /// The frame's spatial layer, resolved from the retained structure; <see langword="null"/> when no
    /// structure for this stream has been seen yet or the template id falls outside it.
    /// </summary>
    public int? SpatialId { get; init; }

    /// <summary>The frame's temporal layer, resolved the same way as <see cref="SpatialId"/>.</summary>
    public int? TemporalId { get; init; }

    /// <summary>
    /// How many earlier frames this frame depends on, from its template; <see langword="null"/> when the
    /// template could not be resolved. Zero marks a frame that can be decoded on its own.
    /// </summary>
    public int? FrameDependencyCount { get; init; }

    /// <summary>
    /// Whether this descriptor begins a coded video sequence — the sender declares the template structure
    /// only there, so it is the receiver's key-frame signal (AV1 RTP specification §A.8).
    /// </summary>
    public bool StartsCodedVideoSequence => Structure is not null;

    /// <summary>
    /// Whether this frame is a key frame: it starts a coded video sequence, is the start of its frame, and
    /// — when the template resolved — depends on nothing. The dependency count is the corroborating check,
    /// not the primary one, because it needs a structure that only a key frame carries in the first place.
    /// </summary>
    public bool IsKeyFrame =>
        StartsCodedVideoSequence && StartOfFrame && FrameDependencyCount is null or 0;
}
