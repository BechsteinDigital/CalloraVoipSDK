using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L0 — #225: the Dependency Descriptor wire codec (AV1 RTP specification §A). The descriptor carries
/// key-frame and layer information in the RTP header, so a receiver no longer has to read the payload to
/// learn them — which is the only way to have either for an end-to-end encrypted stream (#223).
/// </summary>
/// <remarks>
/// No reference stack to calibrate the C# against: SIPSorcery does not implement the descriptor at all
/// (searched, zero hits), and pjsip has no RTP header-extension layer of this kind. The calibration is
/// therefore the specification's own bit syntax, asserted two ways — an exact byte layout for the mandatory
/// fields, and writer↔reader round trips for everything above them.
/// </remarks>
public sealed class DependencyDescriptorCodecTests
{
    // ── the mandatory fields, byte-exact ─────────────────────────────────────────────────────

    /// <summary>
    /// The three mandatory bytes, computed by hand from §A.8: start_of_frame(1) end_of_frame(1)
    /// template_id(6) frame_number(16). Byte-exact rather than round-tripped, so an error in both halves of
    /// the codec cannot cancel out.
    /// </summary>
    [Fact]
    public void A_delta_descriptor_is_three_bytes_in_the_specified_layout()
    {
        var writer = new DependencyDescriptorWriter();

        var bytes = writer.Write(isKeyFrame: false, startOfFrame: true, endOfFrame: true, frameNumber: 0x1234);

        // 1 (sof) | 1 (eof) | 000001 (template 1 = the delta template) = 0xC1, then the frame number.
        Assert.Equal(new byte[] { 0xC1, 0x12, 0x34 }, bytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_frame_boundary_flags_round_trip(bool startOfFrame, bool endOfFrame)
    {
        var bytes = new DependencyDescriptorWriter()
            .Write(isKeyFrame: false, startOfFrame, endOfFrame, frameNumber: 7);

        Assert.True(new DependencyDescriptorReader().TryParse(bytes, out var descriptor));
        Assert.Equal(startOfFrame, descriptor!.StartOfFrame);
        Assert.Equal(endOfFrame, descriptor.EndOfFrame);
        Assert.Equal(7, descriptor.FrameNumber);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)0x8000)]
    [InlineData((ushort)0xFFFF)]
    public void The_frame_number_round_trips_across_its_whole_range(ushort frameNumber)
    {
        var bytes = new DependencyDescriptorWriter()
            .Write(isKeyFrame: false, startOfFrame: true, endOfFrame: true, frameNumber);

        Assert.True(new DependencyDescriptorReader().TryParse(bytes, out var descriptor));
        Assert.Equal(frameNumber, descriptor!.FrameNumber);
    }

    // ── the template structure ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A key frame carries the whole template structure; a receiver reads the key-frame signal and the layer
    /// out of it without ever touching the payload.
    /// </summary>
    [Fact]
    public void A_key_frame_declares_the_structure_and_reads_back_as_a_key_frame()
    {
        var bytes = new DependencyDescriptorWriter()
            .Write(isKeyFrame: true, startOfFrame: true, endOfFrame: true, frameNumber: 0);

        Assert.True(new DependencyDescriptorReader().TryParse(bytes, out var descriptor));

        Assert.True(descriptor!.StartsCodedVideoSequence);
        Assert.True(descriptor.IsKeyFrame);
        Assert.Equal(0, descriptor.SpatialId);
        Assert.Equal(0, descriptor.TemporalId);
        Assert.Equal(0, descriptor.FrameDependencyCount); // depends on nothing — a decodable entry point

        var structure = descriptor.Structure;
        Assert.NotNull(structure);
        Assert.Equal(0, structure!.TemplateIdOffset);
        Assert.Equal(1, structure.DecodeTargetCount);
        Assert.Equal(2, structure.Templates.Count);      // key-frame template + delta template
        Assert.Equal(0, structure.Templates[0].FrameDiffCount);
        Assert.Equal(1, structure.Templates[1].FrameDiffCount);
    }

    /// <summary>
    /// The reason the reader is stateful: a delta frame names a template by id and says nothing else, so it
    /// only means something against the structure the key frame declared.
    /// </summary>
    [Fact]
    public void A_delta_frame_resolves_against_the_retained_structure()
    {
        var writer = new DependencyDescriptorWriter();
        var reader = new DependencyDescriptorReader();

        Assert.True(reader.TryParse(
            writer.Write(isKeyFrame: true, startOfFrame: true, endOfFrame: true, writer.NextFrame()), out _));

        var delta = writer.Write(isKeyFrame: false, startOfFrame: true, endOfFrame: true, writer.NextFrame());
        Assert.True(reader.TryParse(delta, out var descriptor));

        Assert.Equal(3, delta.Length);                    // mandatory fields only
        Assert.False(descriptor!.StartsCodedVideoSequence);
        Assert.False(descriptor.IsKeyFrame);
        Assert.Equal(0, descriptor.SpatialId);
        Assert.Equal(0, descriptor.TemporalId);
        Assert.Equal(1, descriptor.FrameDependencyCount);  // depends on the frame before it
    }

    /// <summary>
    /// Joining a stream mid-sequence: the mandatory fields still parse, but the layer information is
    /// reported as unknown rather than guessed — and the frame is not claimed to be a key frame.
    /// </summary>
    [Fact]
    public void A_delta_frame_without_a_known_structure_reports_unknown_layers()
    {
        var delta = new DependencyDescriptorWriter()
            .Write(isKeyFrame: false, startOfFrame: true, endOfFrame: true, frameNumber: 42);

        Assert.True(new DependencyDescriptorReader().TryParse(delta, out var descriptor));

        Assert.Equal(42, descriptor!.FrameNumber);
        Assert.Null(descriptor.SpatialId);
        Assert.Null(descriptor.TemporalId);
        Assert.Null(descriptor.FrameDependencyCount);
        Assert.False(descriptor.IsKeyFrame);
    }

    [Fact]
    public void A_second_key_frame_replaces_the_retained_structure()
    {
        var writer = new DependencyDescriptorWriter();
        var reader = new DependencyDescriptorReader();

        Assert.True(reader.TryParse(writer.Write(true, true, true, writer.NextFrame()), out _));
        var first = reader.RetainedStructure;

        Assert.True(reader.TryParse(writer.Write(true, true, true, writer.NextFrame()), out var second));

        Assert.NotNull(first);
        Assert.NotNull(reader.RetainedStructure);
        Assert.True(second!.StartsCodedVideoSequence);
        Assert.Equal(first!.Templates.Count, reader.RetainedStructure!.Templates.Count);
    }

    // ── hostile and truncated input (K4) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_descriptor_shorter_than_the_mandatory_fields_is_refused(int length)
        => Assert.False(new DependencyDescriptorReader().TryParse(new byte[length], out _));

    /// <summary>A structure cut off mid-way ends the parse instead of yielding half a structure.</summary>
    [Fact]
    public void A_truncated_structure_is_refused()
    {
        var full = new DependencyDescriptorWriter().Write(true, true, true, frameNumber: 1);

        for (var length = 4; length < full.Length; length++)
            Assert.False(new DependencyDescriptorReader().TryParse(full[..length], out _));
    }

    /// <summary>
    /// Remote input never throws (K4). Every prefix of every random buffer either parses or is refused —
    /// what it must not do is escape as an exception into the receive loop.
    /// </summary>
    [Fact]
    public void Random_input_never_throws()
    {
        var random = new Random(225);
        var reader = new DependencyDescriptorReader();

        for (var i = 0; i < 5_000; i++)
        {
            var buffer = new byte[random.Next(0, 40)];
            random.NextBytes(buffer);
            _ = reader.TryParse(buffer, out _); // must not throw
        }
    }

    // ── the bit primitives ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The non-symmetric unsigned encoding (AV1 §4.10.7) is the one piece of the syntax that is easy to get
    /// subtly wrong, so it is pinned on its own across the full value range of several moduli.
    /// </summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(8u)]
    [InlineData(33u)]
    public void The_non_symmetric_encoding_round_trips(uint n)
    {
        for (var value = 0u; value < n; value++)
        {
            var writer = new RtpBitWriter(8);
            writer.WriteNonSymmetric(value, n);

            var reader = new RtpBitReader(writer.ToArray());
            Assert.Equal(value, reader.ReadNonSymmetric(n));
        }
    }

    [Fact]
    public void The_bit_reader_reports_exhaustion_instead_of_reading_past_the_end()
    {
        var reader = new RtpBitReader(new byte[] { 0xFF });

        Assert.Equal(0xFFu, reader.Read(8));
        Assert.False(reader.Exhausted);
        Assert.Equal(0u, reader.Read(1));
        Assert.True(reader.Exhausted);
    }
}
