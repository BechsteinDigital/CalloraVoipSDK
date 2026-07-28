namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// A parsed bandwidth line (<c>b=&lt;bwtype&gt;:&lt;bandwidth&gt;</c>, RFC 4566 §5.8).
/// The bandwidth type is preserved verbatim so it round-trips unchanged: <c>AS</c> is measured
/// in kilobits per second while <c>TIAS</c> (RFC 3890) is in bits per second, so collapsing them
/// to a single type would misreport the value by a factor of 1000.
/// </summary>
internal sealed record SdpBandwidth
{
    /// <summary>The bandwidth type token, e.g. <c>AS</c> (RFC 4566 §5.8) or <c>TIAS</c> (RFC 3890).</summary>
    public required string Type { get; init; }

    /// <summary>The bandwidth value, in the unit implied by <see cref="Type"/>.</summary>
    public required int Value { get; init; }
}
