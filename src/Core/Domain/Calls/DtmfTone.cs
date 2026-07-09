namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>Validated DTMF tone (0-9, *, #, A-D).</summary>
public readonly record struct DtmfTone
{
    /// <summary>The uppercased tone character: <c>0</c>-<c>9</c>, <c>*</c>, <c>#</c>, or <c>A</c>-<c>D</c>.</summary>
    public char Symbol { get; }

    /// <summary>The RFC 4733 event code: 0-9 for digits, 10 for <c>*</c>, 11 for <c>#</c>, 12-15 for <c>A</c>-<c>D</c>.</summary>
    public byte Code   { get; }

    /// <summary>Creates a tone from its character, case-insensitively.</summary>
    /// <param name="symbol">A DTMF character: <c>0</c>-<c>9</c>, <c>*</c>, <c>#</c>, or <c>A</c>-<c>D</c> (any case).</param>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is not a valid DTMF character.</exception>
    public DtmfTone(char symbol)
    {
        Symbol = char.ToUpperInvariant(symbol);
        Code = Symbol switch
        {
            >= '0' and <= '9' => (byte)(Symbol - '0'),
            '*'               => 10,
            '#'               => 11,
            >= 'A' and <= 'D' => (byte)(Symbol - 'A' + 12),
            _ => throw new ArgumentException($"Invalid DTMF tone: '{symbol}'", nameof(symbol))
        };
    }

    /// <summary>Creates a tone from its RFC 4733 event code (0-15).</summary>
    /// <param name="code">The event code: 0-9 for digits, 10 for <c>*</c>, 11 for <c>#</c>, 12-15 for <c>A</c>-<c>D</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="code"/> is greater than 15.</exception>
    public static DtmfTone FromCode(byte code) => new(code switch
    {
        <= 9              => (char)('0' + code),
        10                => '*',
        11                => '#',
        >= 12 and <= 15   => (char)('A' + code - 12),
        _                 => throw new ArgumentOutOfRangeException(nameof(code))
    });

    /// <summary>Returns the tone <see cref="Symbol"/> as a single-character string.</summary>
    public override string ToString() => Symbol.ToString();
}
