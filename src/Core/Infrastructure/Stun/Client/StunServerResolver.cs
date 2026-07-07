using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Client;

/// <summary>
/// Resolves STUN server hostnames to IP endpoints via DNS SRV lookup with A/AAAA fallback
/// (RFC 5389 §9).
/// <para>
/// For each transport, the following SRV service name is queried:
/// <list type="bullet">
///   <item><see cref="StunTransport.Udp"/>: <c>_stun._udp.&lt;host&gt;</c>, default port 3478.</item>
///   <item><see cref="StunTransport.Tcp"/>: <c>_stun._tcp.&lt;host&gt;</c>, default port 3478.</item>
///   <item><see cref="StunTransport.Tls"/>: <c>_stuns._tcp.&lt;host&gt;</c>, default port 5349.</item>
/// </list>
/// When SRV lookup fails (NXDOMAIN, timeout, network error), falls back to a plain A/AAAA query
/// with the transport's default port.
/// </para>
/// </summary>
internal sealed class StunServerResolver : IStunServerResolver
{
    private readonly Func<string, IPEndPoint, CancellationToken, Task<IReadOnlyList<DnsSrvRecord>>> _srvQuery;
    private readonly Func<string, int, CancellationToken, Task<IPEndPoint>> _endPointResolver;
    private readonly Random _random;
    private readonly IPEndPoint                    _dnsServer;
    private readonly ILogger<StunServerResolver>   _logger;

    /// <summary>Default UDP port for STUN (RFC 5389 §9).</summary>
    private const int DefaultStunPort    = 3478;

    /// <summary>Default UDP/TCP port for STUN over TLS (RFC 5389 §9).</summary>
    private const int DefaultStunTlsPort = 5349;

    /// <summary>
    /// Initialises the resolver using the system's DNS server (read from <c>/etc/resolv.conf</c>
    /// on Linux/macOS). There is no fallback to a public third-party resolver; use the
    /// <see cref="StunServerResolver(IPEndPoint, ILogger{StunServerResolver})"/> overload to
    /// supply a resolver explicitly.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no usable system DNS resolver can be determined
    /// (see <see cref="DnsSrvQuery.GetSystemDnsServer"/>).
    /// </exception>
    public StunServerResolver(ILogger<StunServerResolver> logger)
        : this(
            DnsSrvQuery.GetSystemDnsServer(),
            DnsSrvQuery.QueryAsync,
            RemoteEndPointResolver.ResolveAsync,
            Random.Shared,
            logger) { }

    /// <summary>Initialises the resolver with a specific DNS server endpoint.</summary>
    public StunServerResolver(IPEndPoint dnsServer, ILogger<StunServerResolver> logger)
        : this(
            dnsServer,
            DnsSrvQuery.QueryAsync,
            RemoteEndPointResolver.ResolveAsync,
            Random.Shared,
            logger) { }

    /// <summary>
    /// Initialises the resolver with injectable DNS/endpoint resolvers for deterministic tests.
    /// </summary>
    internal StunServerResolver(
        IPEndPoint dnsServer,
        Func<string, IPEndPoint, CancellationToken, Task<IReadOnlyList<DnsSrvRecord>>> srvQuery,
        Func<string, int, CancellationToken, Task<IPEndPoint>> endPointResolver,
        Random random,
        ILogger<StunServerResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(dnsServer);
        ArgumentNullException.ThrowIfNull(srvQuery);
        ArgumentNullException.ThrowIfNull(endPointResolver);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(logger);

        _dnsServer = dnsServer;
        _srvQuery = srvQuery;
        _endPointResolver = endPointResolver;
        _random = random;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IPEndPoint> ResolveAsync(
        string            host,
        StunTransport     transport,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // If already an IP address, skip DNS entirely.
        if (IPAddress.TryParse(host, out var directIp))
            return new IPEndPoint(directIp, DefaultPortFor(transport));

        var srvName = SrvNameFor(host, transport);
        _logger.LogDebug("STUN SRV lookup: {SrvName} via {DnsServer}", srvName, _dnsServer);

        try
        {
            var records = await _srvQuery(srvName, _dnsServer, ct).ConfigureAwait(false);
            if (records.Count > 0)
            {
                // RFC 2782: sort by priority ascending, then select by weight.
                var best = SelectByWeight(records.OrderBy(r => r.Priority).ToList());
                var ep   = await _endPointResolver(best.Target, best.Port, ct).ConfigureAwait(false);
                _logger.LogInformation("STUN SRV resolved {Domain} → {Target}:{Port} → {Ep}", host, best.Target, best.Port, ep);
                return ep;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "STUN SRV lookup failed for {SrvName}; falling back to A/AAAA", srvName);
        }

        // Fallback: plain A/AAAA lookup with default port.
        var fallbackPort = DefaultPortFor(transport);
        var fallback     = await _endPointResolver(host, fallbackPort, ct).ConfigureAwait(false);
        _logger.LogDebug("STUN fallback A/AAAA resolved {Host}:{Port} → {Ep}", host, fallbackPort, fallback);
        return fallback;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SrvNameFor(string host, StunTransport transport) => transport switch
    {
        StunTransport.Udp => $"_stun._udp.{host}",
        StunTransport.Tcp => $"_stun._tcp.{host}",
        StunTransport.Tls => $"_stuns._tcp.{host}",
        _                 => throw new ArgumentOutOfRangeException(nameof(transport))
    };

    private static int DefaultPortFor(StunTransport transport) => transport switch
    {
        StunTransport.Tls => DefaultStunTlsPort,
        _                 => DefaultStunPort
    };

    /// <summary>
    /// Selects one record from a group with equal priority using weighted random selection (RFC 2782 §3).
    /// </summary>
    private DnsSrvRecord SelectByWeight(IReadOnlyList<DnsSrvRecord> sorted)
    {
        // Take records with the minimum priority.
        ushort minPriority = sorted[0].Priority;
        var candidates = sorted.Where(r => r.Priority == minPriority).ToList();

        int totalWeight = candidates.Sum(r => r.Weight);
        if (totalWeight == 0)
            return candidates[0];

        int pick = _random.Next(0, totalWeight);
        int acc  = 0;
        foreach (var r in candidates)
        {
            acc += r.Weight;
            if (pick < acc) return r;
        }

        return candidates[^1];
    }
}
