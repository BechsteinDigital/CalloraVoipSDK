using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Client;

/// <summary>
/// Infrastructure adapter exposing STUN primitives to the application ICE agent.
/// </summary>
internal sealed class StunIceProbe : IIceStunProbe
{
    private readonly IStunClient _stunClient;
    private readonly ILogger<StunIceProbe> _logger;

    /// <summary>
    /// Creates a STUN ICE probe.
    /// </summary>
    internal StunIceProbe(IStunClient stunClient, ILoggerFactory loggerFactory)
    {
        _stunClient = stunClient ?? throw new ArgumentNullException(nameof(stunClient));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<StunIceProbe>();
    }

    /// <inheritdoc />
    public async Task<IPEndPoint?> TryGetServerReflexiveEndPointAsync(
        IPEndPoint localEndPoint,
        IceServerConfiguration server,
        System.Net.Sockets.Socket? sharedUdpSocket = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(server);

        if (server.Type != IceServerType.Stun)
            return null;

        try
        {
            var transport = MapTransport(server.Transport);
            var serverEndPoint = await ResolveServerEndPointAsync(
                    server, transport, localEndPoint.AddressFamily, ct)
                .ConfigureAwait(false);
            var result = await _stunClient.QueryBindingAsync(
                    serverEndPoint,
                    credentials: BuildCredentials(server),
                    transport: transport,
                    localEndPoint: localEndPoint,
                    sharedUdpSocket: sharedUdpSocket,
                    ct: ct)
                .ConfigureAwait(false);
            return result.MappedEndPoint;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "ICE STUN srflx discovery failed for {Host}:{Port}.",
                server.Host,
                server.Port ?? 0);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryCheckConnectivityAsync(
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint,
        string localIceUfrag,
        string remoteIceUfrag,
        string remoteIcePassword,
        uint localCandidatePriority,
        bool isControlling,
        ulong tieBreaker,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(localIceUfrag);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteIceUfrag);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteIcePassword);

        var effectiveTimeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : timeout;

        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var credentials = new StunCredentials
            {
                Username = $"{remoteIceUfrag}:{localIceUfrag}",
                Password = remoteIcePassword
            };

            // RFC 8445 §7.2.2 check attributes. USE-CANDIDATE (regular nomination) is a later
            // package; the controlled agent never nominates either.
            var iceAttributes = StunIceCheckAttributes.Build(
                priority: localCandidatePriority,
                isControlling: isControlling,
                tieBreaker: tieBreaker,
                useCandidate: false);

            _ = await _stunClient.QueryBindingAsync(
                    remoteEndPoint,
                    credentials: credentials,
                    transport: StunTransport.Udp,
                    localEndPoint: localEndPoint,
                    additionalAttributes: iceAttributes,
                    ct: linked.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "ICE connectivity check failed for {LocalEndPoint} -> {RemoteEndPoint}.",
                localEndPoint,
                remoteEndPoint);
            return false;
        }
    }

    private static StunCredentials? BuildCredentials(IceServerConfiguration server)
    {
        if (string.IsNullOrWhiteSpace(server.Username) || string.IsNullOrWhiteSpace(server.Password))
            return null;

        return new StunCredentials
        {
            Username = server.Username,
            Password = server.Password
        };
    }

    private static async Task<IPEndPoint> ResolveServerEndPointAsync(
        IceServerConfiguration server,
        StunTransport transport,
        System.Net.Sockets.AddressFamily addressFamily,
        CancellationToken ct)
    {
        var port = server.Port ?? DefaultPortFor(transport);
        if (IPAddress.TryParse(server.Host, out var ipAddress))
            return new IPEndPoint(ipAddress, port);

        var addresses = await Dns.GetHostAddressesAsync(server.Host, ct).ConfigureAwait(false);
        var address = PickAddressForFamily(addresses, addressFamily)
            ?? throw new InvalidOperationException(
                $"Unable to resolve STUN host '{server.Host}' to an address in family {addressFamily} " +
                "(the query must use the media socket's address family).");
        return new IPEndPoint(address, port);
    }

    /// <summary>
    /// Picks a resolved address matching the local socket's address family. DNS may
    /// return AAAA records first (e.g. stun.l.google.com) — sending from an IPv4-bound
    /// media socket to an IPv6 server fails with "address family not supported".
    /// </summary>
    internal static IPAddress? PickAddressForFamily(
        IPAddress[] addresses,
        System.Net.Sockets.AddressFamily addressFamily)
        => addresses.FirstOrDefault(a => a.AddressFamily == addressFamily);

    private static int DefaultPortFor(StunTransport transport)
        => transport == StunTransport.Tls ? 5349 : 3478;

    private static StunTransport MapTransport(IceTransport transport)
        => transport switch
        {
            IceTransport.Udp => StunTransport.Udp,
            IceTransport.Tcp => StunTransport.Tcp,
            IceTransport.Tls => StunTransport.Tls,
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported ICE transport.")
        };
}
