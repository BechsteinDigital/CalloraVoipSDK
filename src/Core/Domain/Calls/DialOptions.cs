using System.Collections.ObjectModel;

namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Per-call dialing options — a pure Domain value object supplied to
/// <see cref="Lines.IPhoneLine.DialAsync"/>.
/// </summary>
public sealed class DialOptions
{
    /// <summary>Shared instance carrying the default options (30 s ring timeout, no overrides).</summary>
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
    /// <remarks>
    /// Snapshotted on assignment (#165 P3-10): IReadOnlyDictionary is a view over the caller's own
    /// dictionary, which stays mutable. These options are read when the INVITE is actually built — after
    /// the call returns — so a caller reusing and editing one options object would change the headers of a
    /// dial it has already issued.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? CustomHeaders
    {
        get => _customHeaders;
        init => _customHeaders = value is null
            ? null
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value));
    }

    private readonly IReadOnlyDictionary<string, string>? _customHeaders;
}
