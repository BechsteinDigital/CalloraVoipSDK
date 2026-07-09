namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Per-call dialing options — a pure Domain value object supplied to
/// <see cref="Lines.IPhoneLine.DialAsync"/>.
/// </summary>
public sealed class DialOptions
{
    public static readonly DialOptions Default = new();

    /// <summary>How long to ring before automatically cancelling. Default: 30 s.</summary>
    public TimeSpan RingTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Override display name for this call (uses SipAccount.DisplayName if null).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Route this call via a specific outbound proxy (overrides account setting).</summary>
    public string? OutboundProxy { get; init; }

    /// <summary>
    /// Per-call SRTP override:
    /// <list type="bullet">
    /// <item><description><c>null</c> keeps the SDK-configured SRTP policy.</description></item>
    /// <item><description><c>true</c> enforces <c>Required</c>.</description></item>
    /// <item><description><c>false</c> enforces <c>Disabled</c>.</description></item>
    /// </list>
    /// </summary>
    public bool? UseSrtp { get; init; }

    /// <summary>Extra SIP headers added to the INVITE.</summary>
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }
}
