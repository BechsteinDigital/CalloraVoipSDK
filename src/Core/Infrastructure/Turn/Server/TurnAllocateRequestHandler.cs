using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Handles TURN Allocate requests, including RFC 6062 TCP allocation mode.
/// </summary>
internal sealed class TurnAllocateRequestHandler
{
    private readonly TurnServerOptions _options;
    private readonly TurnServerResponseFactory _responseFactory;
    private readonly TurnMobilityService _mobilityService;
    private readonly Func<TurnServerAllocation, CancellationToken, Task> _replaceAllocationAsync;
    private readonly Func<string, bool> _hasAllocationCapacity;
    private readonly TurnPortReservationStore _reservationStore;
    private readonly ILogger<TurnServer> _logger;

    /// <summary>
    /// Creates a handler for Allocate requests.
    /// </summary>
    /// <param name="hasAllocationCapacity">
    /// Predicate over the client key that returns false when a new allocation would exceed the
    /// server-wide allocation quota (an existing allocation for the same client always has capacity).
    /// </param>
    public TurnAllocateRequestHandler(
        TurnServerOptions options,
        TurnServerResponseFactory responseFactory,
        TurnMobilityService mobilityService,
        Func<TurnServerAllocation, CancellationToken, Task> replaceAllocationAsync,
        Func<string, bool> hasAllocationCapacity,
        TurnPortReservationStore reservationStore,
        ILogger<TurnServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(responseFactory);
        ArgumentNullException.ThrowIfNull(mobilityService);
        ArgumentNullException.ThrowIfNull(replaceAllocationAsync);
        ArgumentNullException.ThrowIfNull(hasAllocationCapacity);
        ArgumentNullException.ThrowIfNull(reservationStore);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _responseFactory = responseFactory;
        _mobilityService = mobilityService;
        _replaceAllocationAsync = replaceAllocationAsync;
        _hasAllocationCapacity = hasAllocationCapacity;
        _reservationStore = reservationStore;
        _logger = logger;
    }

    /// <summary>
    /// Processes one Allocate request and provisions either UDP or TCP relay state.
    /// </summary>
    public async Task<StunMessage> HandleAsync(
        StunMessage request,
        TurnClientContext context,
        StunCredentials? authenticatedCredentials,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestedTransport = TurnAttributeMapper.DecodeRequestedTransport(request);
        if (requestedTransport is null)
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        if (requestedTransport.Protocol is not (TurnRequestedTransportProtocol.Udp or TurnRequestedTransportProtocol.Tcp))
            return _responseFactory.BuildErrorResponse(request, 442, "Unsupported Transport Protocol", includeAuthAttributes: false);

        var requestedAddressFamilyAttribute = TurnAttributeMapper.DecodeRequestedAddressFamily(request);
        var requestedFamily = requestedAddressFamilyAttribute?.Family;
        if (requestedAddressFamilyAttribute is not null && !TurnMobilityService.IsKnownAddressFamily(requestedFamily!.Value))
            return _responseFactory.BuildErrorResponse(request, 440, "Address Family not Supported", includeAuthAttributes: false);

        if (requestedAddressFamilyAttribute is not null && TurnAttributeMapper.DecodeReservationToken(request) is not null)
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        if (requestedTransport.Protocol == TurnRequestedTransportProtocol.Tcp
            && (HasRawAttribute(request, TurnAttributeType.DontFragment)
                || HasRawAttribute(request, TurnAttributeType.EvenPort)
                || TurnAttributeMapper.DecodeReservationToken(request) is not null))
        {
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);
        }

        var reservationToken = TurnAttributeMapper.DecodeReservationToken(request);
        var evenPort = TurnAttributeMapper.DecodeEvenPort(request);
        // RFC 8656 §7.2: EVEN-PORT and RESERVATION-TOKEN cannot be combined in one request.
        if (evenPort is not null && reservationToken is not null)
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        var requestedMobilityTicket = TurnAttributeMapper.DecodeMobilityTicket(request);
        if (requestedMobilityTicket is not null && requestedMobilityTicket.Ticket.Length > 0)
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        if (requestedMobilityTicket is not null && !_options.EnableMobility)
            return _responseFactory.BuildErrorResponse(request, 405, "Mobility Forbidden", includeAuthAttributes: false);

        // Enforce the server-wide allocation quota before provisioning any relay resource, so a flood
        // of (possibly source-spoofed) Allocate requests cannot grow the allocation table without bound.
        if (!_hasAllocationCapacity(context.ClientKey))
        {
            _logger.LogWarning(
                "TURN allocation quota reached; refusing Allocate from {Client}", context.RemoteEndPoint);
            return _responseFactory.BuildErrorResponse(request, 486, "Allocation Quota Reached", includeAuthAttributes: false);
        }

        var requestedLifetime = TurnAttributeMapper.DecodeLifetime(request)?.Seconds;
        var allocationLifetime = ClampAllocationLifetime(requestedLifetime);
        var addressFamily = requestedFamily switch
        {
            TurnAddressFamily.IPv6 => AddressFamily.InterNetworkV6,
            TurnAddressFamily.IPv4 => AddressFamily.InterNetwork,
            _ => context.RemoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                ? AddressFamily.InterNetworkV6
                : AddressFamily.InterNetwork
        };

        UdpClient? relayUdp = null;
        TcpListener? relayTcp = null;
        IPEndPoint relayedEndPoint;
        ulong? issuedReservationToken = null;

        try
        {
            if (requestedTransport.Protocol == TurnRequestedTransportProtocol.Udp)
            {
                // DONT-FRAGMENT, EVEN-PORT and RESERVATION-TOKEN only reach here for a UDP allocation
                // (all three are rejected above for TCP).
                var dontFragment = HasRawAttribute(request, TurnAttributeType.DontFragment);
                if (reservationToken is not null)
                {
                    // Claim the odd port reserved by an earlier EVEN-PORT (reserve) allocation (RFC 8656 §7).
                    relayUdp = _reservationStore.Claim(reservationToken.Token);
                    if (relayUdp is null)
                        return _responseFactory.BuildErrorResponse(request, 508, "Insufficient Capacity", includeAuthAttributes: false);
                }
                else if (evenPort is not null)
                {
                    var (evenSocket, reservedSocket) = CreateEvenPortRelaySockets(addressFamily, evenPort.ReserveNextPort, dontFragment);
                    relayUdp = evenSocket;
                    if (reservedSocket is not null)
                        issuedReservationToken = _reservationStore.Reserve(reservedSocket);
                }
                else
                {
                    relayUdp = CreateRelaySocket(addressFamily, dontFragment);
                }

                relayedEndPoint = (IPEndPoint)relayUdp.Client.LocalEndPoint!;
            }
            else
            {
                relayTcp = CreateRelayTcpListener(addressFamily);
                relayedEndPoint = (IPEndPoint)relayTcp.LocalEndpoint;
            }
        }
        catch (SocketException ex) when (requestedFamily is not null)
        {
            _logger.LogWarning(ex, "TURN requested address family {Family} is not supported", requestedFamily);
            relayUdp?.Dispose();
            relayTcp?.Stop();
            return _responseFactory.BuildErrorResponse(request, 440, "Address Family not Supported", includeAuthAttributes: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TURN failed to allocate relay resource for client {Client}", context.RemoteEndPoint);
            relayUdp?.Dispose();
            relayTcp?.Stop();
            return _responseFactory.BuildErrorResponse(request, 508, "Insufficient Capacity", includeAuthAttributes: false);
        }

        relayedEndPoint = ResolveAdvertisedRelayedEndPoint(relayedEndPoint, context.RemoteEndPoint);

        var allocation = new TurnServerAllocation
        {
            ClientKey = context.ClientKey,
            ClientTransport = context.Transport,
            ClientUdpEndPoint = context.Transport == TurnServerTransport.Udp ? context.RemoteEndPoint : null,
            ClientStreamConnection = context.StreamConnection,
            RelayedTransport = requestedTransport.Protocol,
            RelaySocket = relayUdp,
            RelayStop = relayUdp is null ? null : new CancellationTokenSource(),
            RelayTcpListener = relayTcp,
            RelayTcpStop = relayTcp is null ? null : new CancellationTokenSource(),
            RelayedEndPoint = relayedEndPoint,
            MappedEndPoint = context.RemoteEndPoint,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(allocationLifetime),
            Username = authenticatedCredentials?.Username,
            Realm = authenticatedCredentials?.Realm
        };

        await _replaceAllocationAsync(allocation, ct).ConfigureAwait(false);

        var responseAttributes = new List<StunAttribute>
        {
            TurnAttributeMapper.Encode(new TurnXorRelayedAddressAttribute { EndPoint = relayedEndPoint }, request.TransactionId),
            new XorMappedAddressAttribute { EndPoint = context.RemoteEndPoint },
            TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = allocationLifetime })
        };
        if (requestedMobilityTicket is not null)
        {
            responseAttributes.Add(TurnAttributeMapper.Encode(new TurnMobilityTicketAttribute
            {
                Ticket = _mobilityService.IssueTicket(allocation)
            }));
        }
        if (issuedReservationToken is not null)
        {
            // The reserved (odd) port can be claimed by a later Allocate carrying this RESERVATION-TOKEN.
            responseAttributes.Add(TurnAttributeMapper.Encode(new TurnReservationTokenAttribute
            {
                Token = issuedReservationToken.Value
            }));
        }

        return new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = responseAttributes
        };
    }

    private IPEndPoint ResolveAdvertisedRelayedEndPoint(
        IPEndPoint boundRelayedEndPoint,
        IPEndPoint clientRemoteEndPoint)
    {
        // An operator-configured public relay address always wins: in NAT'd / multi-homed / cloud
        // deployments it is the only reliable source, since neither the bound wildcard nor the routed
        // local interface address is what remote peers must reach (RFC 8656 §7.2 XOR-RELAYED-ADDRESS).
        // Applied only when its family matches the relay so an IPv4 value is never advertised for an
        // IPv6 allocation (which would XOR-encode to a wrong address the client cannot reach).
        var configured = _options.PublicRelayAddress;
        if (configured is not null && configured.AddressFamily == boundRelayedEndPoint.AddressFamily)
            return new IPEndPoint(configured, boundRelayedEndPoint.Port);

        var advertised = LocalEndPointAdvertisementResolver.ResolveAdvertisedLocalEndPoint(
            boundRelayedEndPoint,
            clientRemoteEndPoint);

        var isV6 = advertised.Address.AddressFamily == AddressFamily.InterNetworkV6;
        if (IPAddress.Any.Equals(advertised.Address) || IPAddress.IPv6Any.Equals(advertised.Address))
        {
            // No routable local interface toward the client could be determined. Advertising loopback keeps
            // a single-host / loopback deployment working, but it is unreachable from any other host — so warn
            // loudly and point at PublicRelayAddress instead of silently handing out a dead relay address.
            var loopback = isV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            _logger.LogWarning(
                "TURN could not resolve a routable relay address for client {Client}; advertising loopback " +
                "{Loopback}:{Port}, which is unreachable off-host. Set TurnServerOptions.PublicRelayAddress " +
                "for multi-host deployments.",
                clientRemoteEndPoint, loopback, advertised.Port);
            return new IPEndPoint(loopback, advertised.Port);
        }

        return advertised;
    }

    private uint ClampAllocationLifetime(uint? requestedLifetime)
    {
        if (!requestedLifetime.HasValue)
            return _options.DefaultAllocationLifetimeSeconds;

        return Math.Clamp(
            requestedLifetime.Value,
            0,
            _options.MaxAllocationLifetimeSeconds);
    }

    private static bool HasRawAttribute(StunMessage message, TurnAttributeType type)
    {
        return message.Attributes
            .OfType<UnknownRawAttribute>()
            .Any(attribute => attribute.RawAttributeType == (ushort)type);
    }

    // Creates and binds the relay UDP socket for an allocation. When the client asked for DONT-FRAGMENT
    // (RFC 8656 §14) the IPv4 Don't-Fragment bit is set so relayed datagrams are not fragmented in transit;
    // IPv6 never fragments in transit, so the flag is meaningless (and unsettable) there.
    internal static UdpClient CreateRelaySocket(AddressFamily addressFamily, bool dontFragment = false)
        => BuildRelaySocket(addressFamily, port: 0, dontFragment);

    // Binds a relay UDP socket to an explicit port (0 = ephemeral). DONT-FRAGMENT sets the IPv4 DF bit (RFC 8656
    // §14); an IPv6 relay uses dual-mode and never fragments in transit.
    private static UdpClient BuildRelaySocket(AddressFamily addressFamily, int port, bool dontFragment)
    {
        var socket = new UdpClient(addressFamily);
        if (addressFamily == AddressFamily.InterNetworkV6)
            socket.Client.DualMode = true;
        else if (dontFragment)
            socket.Client.DontFragment = true;

        var bindAddress = addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        socket.Client.Bind(new IPEndPoint(bindAddress, port));
        return socket;
    }

    // Binds an even relayed port (RFC 8656 §7). When reservePair is set it also binds the next (odd) port so it
    // can be reserved for a follow-up RESERVATION-TOKEN allocation; the two ports must be consecutive and both
    // free, so it retries with fresh random even ports. Throws (mapped to 508) when no suitable pair is found.
    internal static (UdpClient Even, UdpClient? Reserved) CreateEvenPortRelaySockets(
        AddressFamily addressFamily, bool reservePair, bool dontFragment)
    {
        const int maxAttempts = 40;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            // A random even port in the ephemeral range [49152, 65534].
            var evenPort = 49152 + Random.Shared.Next(0, 8192) * 2;
            UdpClient? even = null;
            UdpClient? reserved = null;
            try
            {
                even = BuildRelaySocket(addressFamily, evenPort, dontFragment);
                if (!reservePair)
                    return (even, null);

                reserved = BuildRelaySocket(addressFamily, evenPort + 1, dontFragment);
                return (even, reserved);
            }
            catch (SocketException)
            {
                even?.Dispose();
                reserved?.Dispose();
            }
        }

        throw new InvalidOperationException("No free even relay port pair for an EVEN-PORT allocation.");
    }

    private static TcpListener CreateRelayTcpListener(AddressFamily addressFamily)
    {
        var bindEndPoint = new IPEndPoint(
            addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
            0);

        var listener = new TcpListener(bindEndPoint);
        listener.Server.NoDelay = true;
        if (addressFamily == AddressFamily.InterNetworkV6)
            listener.Server.DualMode = true;
        listener.Start(64);
        return listener;
    }
}
