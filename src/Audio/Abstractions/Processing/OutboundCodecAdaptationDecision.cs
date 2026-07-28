namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// The outcome of <see cref="OutboundCodecAdaptationPolicy"/>: whether the outbound codec/payload
/// type should change in response to an inbound frame, and the target payload type if so. Immutable
/// so it can be produced and asserted in isolation from any device.
/// </summary>
public readonly struct OutboundCodecAdaptationDecision : IEquatable<OutboundCodecAdaptationDecision>
{
    private OutboundCodecAdaptationDecision(bool shouldAdapt, int targetPayloadType)
    {
        ShouldAdapt = shouldAdapt;
        TargetPayloadType = targetPayloadType;
    }

    /// <summary>A decision to leave the outbound codec unchanged.</summary>
    public static OutboundCodecAdaptationDecision NoChange { get; } = new(false, -1);

    /// <summary>Builds a decision to adapt the outbound codec to <paramref name="targetPayloadType"/>.</summary>
    /// <param name="targetPayloadType">The negotiated payload type to send with.</param>
    /// <returns>An adaptation decision carrying the target payload type.</returns>
    public static OutboundCodecAdaptationDecision Adapt(int targetPayloadType)
        => new(true, targetPayloadType);

    /// <summary>True when the outbound codec/payload type should change.</summary>
    public bool ShouldAdapt { get; }

    /// <summary>The payload type to adapt to; only meaningful when <see cref="ShouldAdapt"/> is true.</summary>
    public int TargetPayloadType { get; }

    /// <inheritdoc />
    public bool Equals(OutboundCodecAdaptationDecision other)
        => ShouldAdapt == other.ShouldAdapt && TargetPayloadType == other.TargetPayloadType;

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is OutboundCodecAdaptationDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ShouldAdapt, TargetPayloadType);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(OutboundCodecAdaptationDecision left, OutboundCodecAdaptationDecision right)
        => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(OutboundCodecAdaptationDecision left, OutboundCodecAdaptationDecision right)
        => !left.Equals(right);
}
