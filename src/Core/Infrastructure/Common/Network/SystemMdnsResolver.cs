using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Network;

/// <summary>
/// Default-<see cref="IMdnsResolver"/>: nutzt den OS-Hostname-Resolver (<see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>),
/// der auf Systemen mit mDNS-Unterstützung (Linux+avahi/nss-mdns, macOS mDNSResponder, Windows 10+) auch
/// <c>.local</c>-Namen auflöst — RFC 8828 §3.2.2 erlaubt genau das für Resolution-only. Wendet die
/// RFC-Pflichtregeln an: Name muss <c>uuid.local</c> sein (genau ein Punkt); Auflösung zu mehr als einer
/// IP wird verworfen (Anti-Spoofing).
/// </summary>
internal sealed class SystemMdnsResolver : IMdnsResolver
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookup;
    private readonly TimeSpan _timeout;

    /// <summary>Produktions-Ctor: OS-Resolver (<see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>), 3 s Timeout.</summary>
    public SystemMdnsResolver() : this(Dns.GetHostAddressesAsync, TimeSpan.FromSeconds(3)) { }

    /// <summary>Test-Ctor: injiziert den Lookup (Default-Timeout), um die Regeln ohne echte Query zu prüfen.</summary>
    public SystemMdnsResolver(Func<string, CancellationToken, Task<IPAddress[]>> lookup)
        : this(lookup, TimeSpan.FromSeconds(3)) { }

    public SystemMdnsResolver(Func<string, CancellationToken, Task<IPAddress[]>> lookup, TimeSpan timeout)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<IPAddress?> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        // RFC 8828 §3.2.2: nur "<label>.local" mit GENAU einem Punkt und nicht-leerem Label
        // (StartsWith('.') lehnt ".local" ab).
        if (string.IsNullOrEmpty(hostname)
            || !hostname.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || hostname.Count(ch => ch == '.') != 1
            || hostname.StartsWith('.'))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var addrs = await _lookup(hostname, cts.Token).ConfigureAwait(false);
            // RFC 8828: mehr als eine IP → ignorieren.
            return addrs is { Length: 1 } ? addrs[0] : null;
        }
        catch
        {
            return null; // Timeout / SocketException / kein OS-mDNS → verworfen (verhaltensbewahrend).
        }
    }
}
