namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Parses the Dependency Descriptor RTP header extension (AV1 RTP specification §A) — the codec-agnostic
/// carrier of per-frame key-frame and layer information (#225).
/// </summary>
/// <remarks>
/// <para>
/// A descriptor is three bytes at minimum (the mandatory fields) and carries the full template dependency
/// structure only at the start of a coded video sequence. Every later frame references a template by id, so
/// the reader is <b>stateful per stream</b>: it retains the last structure it saw and resolves subsequent
/// descriptors against it. A frame whose structure has never been seen — a stream joined mid-sequence —
/// still yields its mandatory fields, with the layer information reported as unknown rather than guessed.
/// </para>
/// <para>
/// Threading: one reader per inbound stream, driven from that stream's single receive loop, like the
/// depacketisers it sits next to. Remote input never throws (K4): a truncated or malformed descriptor ends
/// the parse and yields what was read up to that point, or nothing.
/// </para>
/// </remarks>
internal sealed class DependencyDescriptorReader
{
    private const int MandatoryFieldBytes = 3;
    private const int MaxTemplates = 64;      // the template id field is 6 bits
    private const int MaxDecodeTargets = 32;  // dt_cnt_minus_one is 5 bits
    private const int MaxSpatialLayers = 4;   // AV1 RTP specification §A.8: at most 4 spatial layers

    private DependencyDescriptorStructure? _structure;

    /// <summary>The structure most recently declared on this stream, or null before the first key frame.</summary>
    public DependencyDescriptorStructure? RetainedStructure => _structure;

    /// <summary>
    /// Parses one descriptor. Returns <see langword="false"/> when the extension is too short to hold the
    /// mandatory fields or is malformed beyond them — the caller then treats the packet as carrying no
    /// descriptor and falls back to whatever the payload can tell it.
    /// <para>
    /// Allocation-free for the common case (K3): the result is a struct, and only a descriptor that declares
    /// a new template structure — a key frame — allocates, for the structure itself.
    /// </para>
    /// </summary>
    public bool TryParse(ReadOnlySpan<byte> data, out DependencyDescriptor descriptor)
    {
        descriptor = default;
        if (data.Length < MandatoryFieldBytes)
            return false;

        var reader = new RtpBitReader(data);

        // mandatory_descriptor_fields()
        var startOfFrame = reader.ReadFlag();
        var endOfFrame = reader.ReadFlag();
        var templateId = (int)reader.Read(6);
        var frameNumber = (ushort)reader.Read(16);

        DependencyDescriptorStructure? structure = null;
        if (data.Length > MandatoryFieldBytes)
        {
            // extended_descriptor_fields()
            var structurePresent = reader.ReadFlag();
            var activeDecodeTargetsPresent = reader.ReadFlag();
            var customDtis = reader.ReadFlag();
            var customFdiffs = reader.ReadFlag();
            var customChains = reader.ReadFlag();

            if (structurePresent && !TryReadStructure(ref reader, out structure))
                return false;

            if (activeDecodeTargetsPresent)
            {
                var decodeTargetCount = structure?.DecodeTargetCount ?? _structure?.DecodeTargetCount ?? 0;
                if (decodeTargetCount is > 0 and <= MaxDecodeTargets)
                    reader.Read(decodeTargetCount);
            }

            // The per-frame overrides (custom dtis/fdiffs/chains) change dependency detail this SDK does not
            // act on — it forwards frames whole and never drops one by decode target. They are skipped rather
            // than parsed, which is safe because they are the last fields of the descriptor: nothing this
            // reader still needs sits behind them.
            //
            // Limitation, stated rather than hidden: with custom_fdiffs_flag set, the frame overrides its
            // template's dependencies, so FrameDependencyCount below reports the TEMPLATE's count and not the
            // frame's. IsKeyFrame consults it, but only as a corroborating check behind structure presence —
            // and a sender that declares a new structure is starting a coded video sequence, which is the
            // authoritative signal. Parsing the overrides is follow-up work if a layer-selecting forwarder
            // ever needs frame-exact dependencies.
            _ = customDtis;
            _ = customFdiffs;
            _ = customChains;

            if (reader.Exhausted)
                return false;
        }

        if (structure is not null)
            _structure = structure;

        var template = (structure ?? _structure)?.Resolve(templateId);
        descriptor = new DependencyDescriptor
        {
            StartOfFrame = startOfFrame,
            EndOfFrame = endOfFrame,
            TemplateId = templateId,
            FrameNumber = frameNumber,
            Structure = structure,
            SpatialId = template?.SpatialId,
            TemporalId = template?.TemporalId,
            FrameDependencyCount = template?.FrameDiffCount,
        };
        return true;
    }

    // template_dependency_structure()
    private static bool TryReadStructure(ref RtpBitReader reader, out DependencyDescriptorStructure? structure)
    {
        structure = null;

        var templateIdOffset = (int)reader.Read(6);
        var decodeTargetCount = (int)reader.Read(5) + 1;
        if (reader.Exhausted || decodeTargetCount > MaxDecodeTargets)
            return false;

        // template_layers(): walk the layer ladder, one template per step, until next_layer_idc says stop.
        var spatialIds = new List<int>(MaxTemplates);
        var temporalIds = new List<int>(MaxTemplates);
        var temporalId = 0;
        var spatialId = 0;
        uint nextLayerIdc;
        do
        {
            if (spatialIds.Count >= MaxTemplates)
                return false;

            spatialIds.Add(spatialId);
            temporalIds.Add(temporalId);

            nextLayerIdc = reader.Read(2);
            if (reader.Exhausted)
                return false;

            if (nextLayerIdc == 1)
            {
                temporalId++;
            }
            else if (nextLayerIdc == 2)
            {
                temporalId = 0;
                spatialId++;
                if (spatialId >= MaxSpatialLayers)
                    return false;
            }
        }
        while (nextLayerIdc != 3);

        var templateCount = spatialIds.Count;

        // template_dtis(): two bits per template per decode target. Not acted on here (see TryParse), but
        // they have to be consumed to reach the frame diffs behind them.
        for (var i = 0; i < templateCount * decodeTargetCount; i++)
            reader.Read(2);
        if (reader.Exhausted)
            return false;

        // template_fdiffs(): the dependency count per template — zero means a decodable entry point.
        var frameDiffCounts = new int[templateCount];
        for (var templateIndex = 0; templateIndex < templateCount; templateIndex++)
        {
            var count = 0;
            while (reader.ReadFlag())
            {
                reader.Read(4); // fdiff_minus_one — the distance itself is not acted on here
                count++;
                if (reader.Exhausted || count > MaxTemplates)
                    return false;
            }

            if (reader.Exhausted)
                return false;

            frameDiffCounts[templateIndex] = count;
        }

        // template_chains(): consumed for completeness — chain protection is a forwarder's dropping policy,
        // which this SDK does not implement, but the fields sit before the resolutions.
        var chainCount = reader.ReadNonSymmetric((uint)decodeTargetCount + 1);
        if (reader.Exhausted || chainCount > decodeTargetCount)
            return false;

        if (chainCount > 0)
        {
            for (var i = 0; i < decodeTargetCount; i++)
                reader.ReadNonSymmetric(chainCount);
            for (var i = 0; i < templateCount * (int)chainCount; i++)
                reader.Read(4);
            if (reader.Exhausted)
                return false;
        }

        // decode_target_layers() reads no bits — it is derived from the templates above.

        // render_resolutions(): one 16-bit width/height pair per spatial layer.
        if (reader.ReadFlag())
        {
            for (var i = 0; i <= spatialId; i++)
            {
                reader.Read(16);
                reader.Read(16);
            }
        }

        if (reader.Exhausted)
            return false;

        var templates = new DependencyDescriptorTemplate[templateCount];
        for (var i = 0; i < templateCount; i++)
            templates[i] = new DependencyDescriptorTemplate(spatialIds[i], temporalIds[i], frameDiffCounts[i]);

        structure = new DependencyDescriptorStructure(templateIdOffset, decodeTargetCount, templates);
        return true;
    }
}
