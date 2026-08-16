namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Strongly-typed, immutable identifier for a single call instance.
/// </summary>
public readonly record struct CallId(Guid Value)
{
    /// <summary>The underlying identifier; never <see cref="Guid.Empty"/> when constructed (#165 P3-12).</summary>
    /// <remarks>
    /// The generated positional constructor took any Guid, so <c>new CallId(Guid.Empty)</c> produced an
    /// identifier that is not one: it keys the call registry, the media orchestrator's active map and the
    /// per-call SSRC bookkeeping, and every empty id collides with every other. Validating in the property
    /// initialiser keeps the positional shape — constructor, <c>Value</c>, <c>Deconstruct</c>, <c>with</c> —
    /// while closing that door. <c>default(CallId)</c> still bypasses it, as it does for every struct in the
    /// language; treat a default instance as "no call", not as one.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public Guid Value
    {
        get => _value;
        init => _value = Validated(value);
    }

    // Declaring Value explicitly means the positional constructor no longer assigns it — this initialiser
    // is that assignment, so it has to validate too. The init accessor above covers the other door, a
    // `with` clone, which does go through the property.
    private readonly Guid _value = Validated(Value);

    private static Guid Validated(Guid value) => value != Guid.Empty
        ? value
        : throw new ArgumentException("A call id cannot be the empty GUID.", nameof(Value));

    /// <summary>Creates a new <see cref="CallId"/> backed by a freshly generated <see cref="Guid"/>.</summary>
    /// <returns>A unique <see cref="CallId"/>.</returns>
    public static CallId New() => new(Guid.NewGuid());

    /// <summary>Returns the underlying GUID as a string.</summary>
    public override string ToString() => Value.ToString();
}
