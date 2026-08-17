namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Writes the Dependency Descriptor for this SDK's own outbound video (AV1 RTP specification §A, #225), so
/// a receiver — a forwarder, or a peer whose payload is end-to-end encrypted — gets the key-frame signal
/// from the header instead of the payload.
/// </summary>
/// <remarks>
/// <para>
/// The structure written is <b>L1T1</b>: one spatial layer, one temporal layer, one decode target, no
/// chains. That is not a simplification, it is what the SDK can honestly declare — it does not encode
/// video, so it knows of no layer ladder to describe. Two templates make the sequence meaningful: template
/// 0 depends on nothing (the key frame) and template 1 on its predecessor (a delta frame). A sender that
/// one day forwards someone else's scalable stream would pass that stream's descriptor through instead of
/// synthesising one.
/// </para>
/// <para>
/// The structure is declared on key frames only, as the specification requires; delta frames carry the
/// three mandatory bytes. Threading: one writer per outbound stream, driven from that stream's send path,
/// which is already serialised per encoding.
/// </para>
/// </remarks>
internal sealed class DependencyDescriptorWriter
{
    // Template ids: the structure declares an offset of 0, so the ids are the template indices themselves.
    private const int KeyFrameTemplateId = 0;
    private const int DeltaFrameTemplateId = 1;

    // Decode target indication (AV1 RTP specification Table A.1): 2 = Switch — a receiver may start
    // decoding this decode target at this frame. Correct for both templates of a single-layer stream.
    private const uint SwitchIndication = 2;

    // Enough for the structure-carrying descriptor; the mandatory-only form needs three.
    private const int MaxDescriptorBytes = 16;

    private ushort _frameNumber;

    /// <summary>The frame number the next frame will carry — exposed for tests and diagnostics.</summary>
    public ushort NextFrameNumber => _frameNumber;

    /// <summary>
    /// Builds the descriptor for one packet of a frame. The structure rides along on a key frame's packets;
    /// a delta frame gets the mandatory three bytes.
    /// </summary>
    /// <param name="isKeyFrame">Whether this frame can be decoded on its own.</param>
    /// <param name="startOfFrame">Whether this packet carries the frame's first byte.</param>
    /// <param name="endOfFrame">Whether this packet carries the frame's last byte.</param>
    /// <param name="frameNumber">The frame's number; use <see cref="NextFrameNumber"/> for a new frame.</param>
    public byte[] Write(bool isKeyFrame, bool startOfFrame, bool endOfFrame, ushort frameNumber)
    {
        var writer = new RtpBitWriter(MaxDescriptorBytes);

        // mandatory_descriptor_fields()
        writer.WriteFlag(startOfFrame);
        writer.WriteFlag(endOfFrame);
        writer.Write((uint)(isKeyFrame ? KeyFrameTemplateId : DeltaFrameTemplateId), 6);
        writer.Write(frameNumber, 16);

        if (!isKeyFrame)
            return writer.ToArray();

        // extended_descriptor_fields(): structure present, nothing else. The active-decode-targets bitmask
        // is implied by the structure ((1 << DtCnt) - 1), so it is not written; no custom overrides.
        writer.WriteFlag(true);   // template_dependency_structure_present_flag
        writer.WriteFlag(false);  // active_decode_targets_present_flag
        writer.WriteFlag(false);  // custom_dtis_flag
        writer.WriteFlag(false);  // custom_fdiffs_flag
        writer.WriteFlag(false);  // custom_chains_flag

        // template_dependency_structure()
        writer.Write(0, 6);  // template_id_offset
        writer.Write(0, 5);  // dt_cnt_minus_one → one decode target

        // template_layers(): two templates, both at spatial 0 / temporal 0, then stop.
        writer.Write(0, 2);  // next_layer_idc = 0 → same layer, another template follows
        writer.Write(3, 2);  // next_layer_idc = 3 → no more templates

        // template_dtis(): one decode target per template.
        writer.Write(SwitchIndication, 2);
        writer.Write(SwitchIndication, 2);

        // template_fdiffs(): template 0 depends on nothing, template 1 on the frame before it.
        writer.WriteFlag(false);           // template 0: no fdiff follows
        writer.WriteFlag(true);            // template 1: one fdiff follows
        writer.Write(0, 4);                // fdiff_minus_one = 0 → distance 1
        writer.WriteFlag(false);           // template 1: no further fdiff

        // template_chains(): none. Chain protection guides a forwarder's dropping policy, and a
        // single-layer stream has nothing to drop.
        writer.WriteNonSymmetric(0, 2);    // chain_cnt = 0, ns(DtCnt + 1)

        writer.WriteFlag(false);           // resolutions_present_flag — the SDK does not decode, so it does
                                           // not know the render resolution to declare.

        return writer.ToArray();
    }

    /// <summary>Reserves the next frame number, advancing the counter (it wraps at 16 bits by design).</summary>
    public ushort NextFrame() => _frameNumber++;
}
