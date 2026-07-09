namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>Stable, opaque identifier for a phone line.</summary>
/// <param name="Value">The underlying GUID value.</param>
public readonly record struct LineId(Guid Value)
{
    /// <summary>Creates a new unique line identifier.</summary>
    public static LineId New() => new(Guid.NewGuid());

    /// <summary>Returns the underlying GUID in its canonical string form.</summary>
    public override string ToString() => Value.ToString();
}
