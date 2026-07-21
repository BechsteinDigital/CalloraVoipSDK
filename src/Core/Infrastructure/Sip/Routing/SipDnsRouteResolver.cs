using System.Net;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Routing;

/// <summary>
/// DNS-backed SIP route resolver with RFC3263-style fallback chain:
/// NAPTR -> SRV -> A/AAAA.
/// </summary>
internal sealed class SipDnsRouteResolver : ISipRouteResolver
{
    private readonly LookupClient _lookupClient;
    private readonly ILogger<SipDnsRouteResolver> _logger;
    private readonly Func<int, int> _nextInt;

    /// <summary>
    /// Creates a resolver using default DNS lookup settings.
    /// </summary>
    public SipDnsRouteResolver(ILoggerFactory loggerFactory)
        : this(
            new LookupClient(new LookupClientOptions
            {
                UseCache = true,
                ContinueOnDnsError = true,
                Timeout = TimeSpan.FromSeconds(2),
                Retries = 1
            }),
            loggerFactory)
    {
    }

    /// <summary>
    /// Creates a resolver with explicit lookup client dependency.
    /// </summary>
    public SipDnsRouteResolver(
        LookupClient lookupClient,
        ILoggerFactory loggerFactory,
        Func<int, int>? nextInt = null)
    {
        _lookupClient = lookupClient ?? throw new ArgumentNullException(nameof(lookupClient));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<SipDnsRouteResolver>();
        // RFC 2782 SRV weight selection uses a random draw; injectable so it is deterministic in tests.
        _nextInt = nextInt ?? Random.Shared.Next;
    }

    /// <inheritdoc />
    public async Task<SipRouteResolutionResult> ResolveAsync(
        SipRouteResolutionRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        var host = request.Host.Trim();
        var preferredTransport = NormalizeTransport(request.PreferredTransport);
        var effectivePort = request.Port.GetValueOrDefault();

        if (IPAddress.TryParse(host, out var ipAddress))
        {
            var ipCandidate = new SipRouteCandidate
            {
                EndPoint = new IPEndPoint(ipAddress, effectivePort > 0 ? effectivePort : DefaultPort(preferredTransport)),
                Transport = preferredTransport,
                Source = "ip-literal"
            };

            return new SipRouteResolutionResult { Candidates = [ipCandidate] };
        }

        if (effectivePort > 0)
        {
            var directCandidates = await ResolveAddressCandidatesAsync(
                    host,
                    effectivePort,
                    preferredTransport,
                    "explicit-port",
                    ct)
                .ConfigureAwait(false);

            if (directCandidates.Count > 0)
                return new SipRouteResolutionResult { Candidates = directCandidates };
        }

        var naptrCandidates = await TryResolveViaNaptrAsync(host, preferredTransport, ct).ConfigureAwait(false);
        if (naptrCandidates.Count > 0)
            return new SipRouteResolutionResult { Candidates = naptrCandidates };

        var srvCandidates = await TryResolveViaSrvFallbackAsync(host, preferredTransport, ct).ConfigureAwait(false);
        if (srvCandidates.Count > 0)
            return new SipRouteResolutionResult { Candidates = srvCandidates };

        var fallbackCandidates = await ResolveAddressCandidatesAsync(
                host,
                DefaultPort(preferredTransport),
                preferredTransport,
                "a-aaaa-fallback",
                ct)
            .ConfigureAwait(false);

        if (fallbackCandidates.Count == 0)
            throw new InvalidOperationException($"SIP route resolution returned no candidates for '{host}'.");

        return new SipRouteResolutionResult { Candidates = fallbackCandidates };
    }

    /// <summary>
    /// Attempts NAPTR-driven routing, including SRV expansion.
    /// </summary>
    private async Task<List<SipRouteCandidate>> TryResolveViaNaptrAsync(
        string host,
        SipTransportProtocol preferredTransport,
        CancellationToken ct)
    {
        try
        {
            var naptrResponse = await _lookupClient.QueryAsync(host, QueryType.NAPTR, cancellationToken: ct)
                .ConfigureAwait(false);
            var naptrRecords = naptrResponse.Answers
                .OfType<NAPtrRecord>()
                .OrderBy(r => r.Order)
                .ThenBy(r => r.Preference)
                .ToArray();
            if (naptrRecords.Length == 0)
                return [];

            var candidates = new List<SipRouteCandidate>();
            foreach (var naptrRecord in naptrRecords)
            {
                if (!TryMapNaptrService(naptrRecord.Services, out var mappedTransport))
                    continue;
                if (!IsTransportCompatible(mappedTransport, preferredTransport))
                    continue;

                var replacement = NormalizeDnsName(naptrRecord.Replacement.Value);
                if (string.IsNullOrWhiteSpace(replacement))
                    continue;

                var viaSrv = await ResolveSrvCandidatesAsync(
                        replacement,
                        mappedTransport,
                        "naptr+srv",
                        ct)
                    .ConfigureAwait(false);
                candidates.AddRange(viaSrv);
            }

            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP NAPTR lookup failed for {Host}.", host);
            return [];
        }
    }

    /// <summary>
    /// Attempts SRV fallback when NAPTR is unavailable or empty.
    /// </summary>
    private async Task<List<SipRouteCandidate>> TryResolveViaSrvFallbackAsync(
        string host,
        SipTransportProtocol preferredTransport,
        CancellationToken ct)
    {
        var srvTargets = BuildSrvFallbackTargets(host, preferredTransport);
        var candidates = new List<SipRouteCandidate>();
        foreach (var target in srvTargets)
        {
            var resolved = await ResolveSrvCandidatesAsync(
                    target.SrvName,
                    target.Transport,
                    "srv",
                    ct)
                .ConfigureAwait(false);
            candidates.AddRange(resolved);
        }

        return candidates;
    }

    /// <summary>
    /// Resolves one SRV host into endpoint candidates.
    /// </summary>
    private async Task<List<SipRouteCandidate>> ResolveSrvCandidatesAsync(
        string srvQueryName,
        SipTransportProtocol transport,
        string source,
        CancellationToken ct)
    {
        try
        {
            var srvResponse = await _lookupClient.QueryAsync(srvQueryName, QueryType.SRV, cancellationToken: ct)
                .ConfigureAwait(false);
            var srvRecords = srvResponse.Answers.OfType<SrvRecord>().ToArray();
            if (srvRecords.Length == 0)
                return [];

            // RFC 2782 / RFC 3263: ascending priority, then a weighted random draw within each priority group.
            // (The former deterministic highest-weight-first order defeated load balancing across a proxy farm.)
            var orderedSrvRecords = SipSrvWeightedOrdering.Order(
                srvRecords, r => r.Priority, r => r.Weight, _nextInt);

            var candidates = new List<SipRouteCandidate>();
            foreach (var srvRecord in orderedSrvRecords)
            {
                var targetHost = NormalizeDnsName(srvRecord.Target.Value);
                if (string.IsNullOrWhiteSpace(targetHost))
                    continue;

                var targetPort = srvRecord.Port > 0 ? srvRecord.Port : (ushort)DefaultPort(transport);
                var resolved = await ResolveAddressCandidatesAsync(
                        targetHost,
                        targetPort,
                        transport,
                        source,
                        ct)
                    .ConfigureAwait(false);
                candidates.AddRange(resolved);
            }

            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP SRV lookup failed for {SrvQuery}.", srvQueryName);
            return [];
        }
    }

    /// <summary>
    /// Resolves A and AAAA addresses for one host and maps them to candidates.
    /// </summary>
    private async Task<List<SipRouteCandidate>> ResolveAddressCandidatesAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        string source,
        CancellationToken ct)
    {
        var addresses = await ResolveAddressesAsync(host, ct).ConfigureAwait(false);
        var candidates = new List<SipRouteCandidate>(addresses.Count);
        foreach (var address in addresses)
        {
            candidates.Add(new SipRouteCandidate
            {
                EndPoint = new IPEndPoint(address, port),
                Transport = transport,
                Source = source
            });
        }

        return candidates;
    }

    /// <summary>
    /// Resolves host addresses with IPv4-first ordering and DNS fallback.
    /// </summary>
    private async Task<List<IPAddress>> ResolveAddressesAsync(string host, CancellationToken ct)
    {
        var collected = new List<IPAddress>();
        try
        {
            var aRecords = await _lookupClient.QueryAsync(host, QueryType.A, cancellationToken: ct)
                .ConfigureAwait(false);
            collected.AddRange(aRecords.Answers.OfType<ARecord>().Select(r => r.Address));

            var aaaaRecords = await _lookupClient.QueryAsync(host, QueryType.AAAA, cancellationToken: ct)
                .ConfigureAwait(false);
            collected.AddRange(aaaaRecords.Answers.OfType<AaaaRecord>().Select(r => r.Address));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Primary A/AAAA lookup failed for {Host}.", host);
        }

        if (collected.Count == 0)
        {
            try
            {
                var fallback = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                collected.AddRange(fallback);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fallback host address lookup failed for {Host}.", host);
            }
        }

        return collected
            .Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();
    }

    /// <summary>
    /// Returns fallback SRV query names ordered by transport preference.
    /// </summary>
    private static IReadOnlyList<(string SrvName, SipTransportProtocol Transport)> BuildSrvFallbackTargets(
        string host,
        SipTransportProtocol preferredTransport) => preferredTransport switch
    {
        SipTransportProtocol.Ws =>
        [
            ($"_sip._ws.{host}", SipTransportProtocol.Ws),
            ($"_sip._tcp.{host}", SipTransportProtocol.Tcp),
            ($"_sip._udp.{host}", SipTransportProtocol.Udp)
        ],
        SipTransportProtocol.Wss =>
        [
            ($"_sips._ws.{host}", SipTransportProtocol.Wss),
            ($"_sips._tcp.{host}", SipTransportProtocol.Tls),
            ($"_sip._tcp.{host}", SipTransportProtocol.Tcp)
        ],
        SipTransportProtocol.Tcp =>
        [
            ($"_sip._tcp.{host}", SipTransportProtocol.Tcp),
            ($"_sip._udp.{host}", SipTransportProtocol.Udp)
        ],
        SipTransportProtocol.Tls =>
        [
            ($"_sips._tcp.{host}", SipTransportProtocol.Tls),
            ($"_sip._tcp.{host}", SipTransportProtocol.Tcp),
            ($"_sip._udp.{host}", SipTransportProtocol.Udp)
        ],
        _ =>
        [
            ($"_sip._udp.{host}", SipTransportProtocol.Udp),
            ($"_sip._tcp.{host}", SipTransportProtocol.Tcp)
        ]
    };

    /// <summary>
    /// Tries to map NAPTR service token to SIP transport.
    /// </summary>
    private static bool TryMapNaptrService(
        string? service,
        out SipTransportProtocol transport)
    {
        transport = SipTransportProtocol.Udp;
        if (string.IsNullOrWhiteSpace(service))
            return false;

        var normalized = service.Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "SIP+D2U":
                transport = SipTransportProtocol.Udp;
                return true;
            case "SIP+D2T":
                transport = SipTransportProtocol.Tcp;
                return true;
            case "SIPS+D2T":
                transport = SipTransportProtocol.Tls;
                return true;
            case "SIP+D2W":
                transport = SipTransportProtocol.Ws;
                return true;
            case "SIPS+D2W":
                transport = SipTransportProtocol.Wss;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Validates route resolution input.
    /// </summary>
    private static void ValidateRequest(SipRouteResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host))
            throw new ArgumentException("Host is required for SIP route resolution.", nameof(request));
        if (request.Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), "Port must be between 0 and 65535.");
    }

    /// <summary>
    /// Returns true when a resolved transport can satisfy preferred transport intent.
    /// </summary>
    private static bool IsTransportCompatible(
        SipTransportProtocol resolved,
        SipTransportProtocol preferred) =>
        preferred switch
        {
            SipTransportProtocol.Tls => resolved == SipTransportProtocol.Tls,
            SipTransportProtocol.Wss => resolved is SipTransportProtocol.Wss or SipTransportProtocol.Tls,
            SipTransportProtocol.Ws => resolved is SipTransportProtocol.Ws or SipTransportProtocol.Tcp or SipTransportProtocol.Udp,
            SipTransportProtocol.Tcp => resolved is SipTransportProtocol.Tcp or SipTransportProtocol.Udp,
            SipTransportProtocol.Udp => resolved is SipTransportProtocol.Udp or SipTransportProtocol.Tcp,
            _ => resolved == SipTransportProtocol.Udp
        };

    /// <summary>
    /// Normalizes requested transport.
    /// Keeps WS/WSS explicit for RFC7118 routing.
    /// </summary>
    private static SipTransportProtocol NormalizeTransport(SipTransportProtocol transport) => transport;

    /// <summary>
    /// Returns default SIP port for a transport protocol.
    /// </summary>
    private static int DefaultPort(SipTransportProtocol transport) =>
        transport switch
        {
            SipTransportProtocol.Ws => 80,
            SipTransportProtocol.Wss => 443,
            SipTransportProtocol.Tls => 5061,
            _ => 5060
        };

    /// <summary>
    /// Removes trailing DNS root dot from fully qualified names.
    /// </summary>
    private static string NormalizeDnsName(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('.');
}
