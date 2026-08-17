namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// One frame dependency template of a Dependency Descriptor structure (AV1 RTP specification §A.8): the
/// layer a frame built from it belongs to, and which earlier frames it depends on.
/// </summary>
/// <param name="SpatialId">The template's spatial layer.</param>
/// <param name="TemporalId">The template's temporal layer.</param>
/// <param name="FrameDiffCount">
/// How many earlier frames a frame using this template references. Zero means the frame depends on nothing
/// — the property that makes it a decodable entry point.
/// </param>
internal readonly record struct DependencyDescriptorTemplate(int SpatialId, int TemporalId, int FrameDiffCount);

/// <summary>
/// The template dependency structure a sender declares at the start of a coded video sequence (AV1 RTP
/// specification §A.8). It is transmitted only on key frames, so a receiver must retain it and apply it to
/// every later frame of that stream — a descriptor whose template structure has not been seen yet cannot be
/// interpreted beyond its mandatory fields.
/// </summary>
/// <param name="TemplateIdOffset">
/// The id of the first template; a frame's template index is <c>(templateId + 64 - offset) % 64</c>.
/// </param>
/// <param name="DecodeTargetCount">Number of decode targets the structure describes.</param>
/// <param name="Templates">The templates, in declaration order.</param>
internal sealed record DependencyDescriptorStructure(
    int TemplateIdOffset,
    int DecodeTargetCount,
    IReadOnlyList<DependencyDescriptorTemplate> Templates)
{
    /// <summary>
    /// Resolves the template a frame id refers to, or <see langword="null"/> when the id falls outside this
    /// structure — a stream that switched structures without us seeing the new one.
    /// </summary>
    public DependencyDescriptorTemplate? Resolve(int templateId)
    {
        var index = (templateId + 64 - TemplateIdOffset) % 64;
        return index >= 0 && index < Templates.Count ? Templates[index] : null;
    }
}
