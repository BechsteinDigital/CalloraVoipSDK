using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Network;

/// <summary>
/// Löst einen mDNS-Hostnamen (<c>uuid.local</c>, RFC 6762/8828) zu genau einer IP-Adresse auf.
/// Für die Auflösung EMPFANGENER mDNS-ICE-Candidates (Resolution-only; der SDK publiziert selbst keine).
/// </summary>
internal interface IMdnsResolver
{
    /// <summary>Die eine aufgelöste IP, oder <see langword="null"/> (nicht auflösbar/Timeout/RFC-Regel verletzt).</summary>
    Task<IPAddress?> ResolveAsync(string hostname, CancellationToken cancellationToken);
}
