using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// One relayed inbound datagram observed on the client socket: either a TURN Data indication
/// (permission-only relay) or a raw ChannelData frame (channel-bound relay).
/// </summary>
internal readonly record struct RelayInbound(
    bool IsChannelData,
    ushort ChannelNumber,
    IPEndPoint? PeerEndPoint,
    byte[] Data);

/// <summary>
/// The relayed and mapped addresses learned from a TURN Allocate success response.
/// </summary>
internal readonly record struct TurnAllocation(IPEndPoint RelayedEndPoint, uint LifetimeSeconds);

/// <summary>
/// Minimal single-socket TURN client for the server E2E harness. Unlike the production
/// <c>TurnClient</c> (which opens a fresh socket per transaction and is a stateless per-request
/// protocol building block), this helper keeps ONE UDP socket open for the whole allocation
/// lifecycle, so its 5-tuple stays stable across Allocate/CreatePermission/ChannelBind/Send and it
/// can receive the relayed inbound traffic (Data indications / ChannelData) the server sends back to
/// the allocation's client address. It speaks the real wire format via <see cref="IStunMessageCodec"/>
/// and <see cref="TurnAttributeMapper"/>, drives the unauthenticated server path, and is intentionally
/// not thread-safe (sequential test use only).
/// </summary>
internal sealed class RawTurnUdpClient : IDisposable
{
    private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(3);

    private readonly UdpClient _udp;
    private readonly IStunMessageCodec _codec;
    private readonly TurnTransactionEngine _engine;

    /// <summary>Binds a loopback socket and connects it to the TURN server endpoint.</summary>
    public RawTurnUdpClient(IPEndPoint serverEndPoint, IStunMessageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(serverEndPoint);
        ArgumentNullException.ThrowIfNull(codec);

        _codec = codec;
        _engine = new TurnTransactionEngine(codec);
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _udp.Connect(serverEndPoint);
    }

    /// <summary>The stable client transport address that keys the allocation on the server.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>Sends an Allocate request (UDP relay) and returns the relayed address and lifetime.</summary>
    public async Task<TurnAllocation> AllocateAsync(uint? lifetimeSeconds = null, CancellationToken ct = default)
    {
        var txId = NewTransactionId();
        var attributes = new List<StunAttribute>
        {
            TurnAttributeMapper.Encode(new TurnRequestedTransportAttribute
            {
                Protocol = TurnRequestedTransportProtocol.Udp
            })
        };
        if (lifetimeSeconds.HasValue)
            attributes.Add(TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds.Value }));

        var response = await RoundTripAsync(RequestMessage(TurnMessageMethod.Allocate, txId, attributes), ct)
            .ConfigureAwait(false);
        EnsureSuccess(response, "Allocate");

        var relayed = TurnAttributeMapper.DecodeXorRelayedAddress(response)?.EndPoint
            ?? throw new InvalidOperationException("TURN Allocate success response is missing XOR-RELAYED-ADDRESS.");
        var lifetime = TurnAttributeMapper.DecodeLifetime(response)?.Seconds ?? 0;
        return new TurnAllocation(relayed, lifetime);
    }

    /// <summary>
    /// Sends an Allocate request authenticated with long-term credentials (RFC 5389 §10.2
    /// challenge/response, driven by the production <see cref="TurnTransactionEngine"/> over this
    /// client's stable socket) and returns the relayed address and lifetime.
    /// </summary>
    public async Task<TurnAllocation> AllocateAuthenticatedAsync(
        StunCredentials credentials,
        uint? lifetimeSeconds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var (response, _) = await _engine.ExecuteWithAuthAsync(
                TurnMessageMethod.Allocate,
                _ =>
                {
                    var attributes = new List<StunAttribute>
                    {
                        TurnAttributeMapper.Encode(new TurnRequestedTransportAttribute
                        {
                            Protocol = TurnRequestedTransportProtocol.Udp
                        })
                    };
                    if (lifetimeSeconds.HasValue)
                        attributes.Add(TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds.Value }));
                    return attributes;
                },
                credentials,
                SendRequestOnceAsync,
                ct)
            .ConfigureAwait(false);

        var relayed = TurnAttributeMapper.DecodeXorRelayedAddress(response)?.EndPoint
            ?? throw new InvalidOperationException("TURN Allocate success response is missing XOR-RELAYED-ADDRESS.");
        var lifetime = TurnAttributeMapper.DecodeLifetime(response)?.Seconds ?? 0;
        return new TurnAllocation(relayed, lifetime);
    }

    /// <summary>Installs a permission for the peer address using long-term-authenticated requests.</summary>
    public async Task CreatePermissionAuthenticatedAsync(
        StunCredentials credentials,
        IPEndPoint peerEndPoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(peerEndPoint);

        _ = await _engine.ExecuteWithAuthAsync(
                TurnMessageMethod.CreatePermission,
                txId => [TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peerEndPoint }, txId)],
                credentials,
                SendRequestOnceAsync,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Installs a permission for the peer address (keyed by IP per RFC 5766 §8).</summary>
    public async Task CreatePermissionAsync(IPEndPoint peerEndPoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peerEndPoint);
        var txId = NewTransactionId();
        var response = await RoundTripAsync(
                RequestMessage(TurnMessageMethod.CreatePermission, txId,
                    [TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peerEndPoint }, txId)]),
                ct)
            .ConfigureAwait(false);
        EnsureSuccess(response, "CreatePermission");
    }

    /// <summary>Binds a channel number to the peer transport address (keyed by IP:port per RFC 5766 §11).</summary>
    public async Task ChannelBindAsync(ushort channelNumber, IPEndPoint peerEndPoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peerEndPoint);
        var txId = NewTransactionId();
        var response = await RoundTripAsync(
                RequestMessage(TurnMessageMethod.ChannelBind, txId,
                [
                    TurnAttributeMapper.Encode(new TurnChannelNumberAttribute { ChannelNumber = channelNumber }),
                    TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peerEndPoint }, txId)
                ]),
                ct)
            .ConfigureAwait(false);
        EnsureSuccess(response, "ChannelBind");
    }

    /// <summary>Refreshes (or, with <paramref name="lifetimeSeconds"/> = 0, deletes) the allocation.</summary>
    public async Task<uint> RefreshAsync(uint lifetimeSeconds, CancellationToken ct = default)
    {
        var txId = NewTransactionId();
        var response = await RoundTripAsync(
                RequestMessage(TurnMessageMethod.Refresh, txId,
                    [TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds })]),
                ct)
            .ConfigureAwait(false);
        EnsureSuccess(response, "Refresh");
        return TurnAttributeMapper.DecodeLifetime(response)?.Seconds ?? 0;
    }

    /// <summary>Relays <paramref name="data"/> to the peer via a Send indication (no response).</summary>
    public async Task SendIndicationAsync(IPEndPoint peerEndPoint, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peerEndPoint);
        ArgumentNullException.ThrowIfNull(data);

        var txId = NewTransactionId();
        var indication = new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Send,
            TransactionId = txId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peerEndPoint }, txId),
                TurnAttributeMapper.Encode(new TurnDataAttribute { Value = data })
            ]
        };
        await _udp.SendAsync(_codec.Encode(indication), ct).ConfigureAwait(false);
    }

    /// <summary>Relays <paramref name="data"/> to the bound peer via a raw ChannelData frame.</summary>
    public async Task SendChannelDataAsync(ushort channelNumber, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        await _udp.SendAsync(TurnChannelDataCodec.Encode(channelNumber, data), ct).ConfigureAwait(false);
    }

    /// <summary>Receives one relayed inbound datagram (Data indication or ChannelData) from the server.</summary>
    public async Task<RelayInbound> ReceiveRelayAsync(CancellationToken ct = default)
    {
        var received = await ReceiveWithTimeoutAsync(RelayTimeout, ct).ConfigureAwait(false);

        if (TurnChannelDataCodec.TryParse(received.Buffer, out var channelNumber, out var channelData))
            return new RelayInbound(true, channelNumber, PeerEndPoint: null, channelData);

        var decoded = _codec.Decode(received.Buffer)
            ?? throw new InvalidOperationException("Relayed datagram is neither a ChannelData frame nor a valid STUN message.");

        if (decoded.MessageClass != StunMessageClass.Indication
            || (TurnMessageMethod)(ushort)decoded.MessageMethod != TurnMessageMethod.Data)
        {
            throw new InvalidOperationException(
                $"Expected a TURN Data indication, got class {decoded.MessageClass} method {decoded.MessageMethod}.");
        }

        var peer = TurnAttributeMapper.DecodeXorPeerAddress(decoded)?.EndPoint;
        var data = TurnAttributeMapper.DecodeData(decoded)?.Value.ToArray()
            ?? throw new InvalidOperationException("TURN Data indication is missing the DATA attribute.");
        return new RelayInbound(false, ChannelNumber: 0, peer, data);
    }

    /// <inheritdoc />
    public void Dispose() => _udp.Dispose();

    private static StunMessage RequestMessage(
        TurnMessageMethod method,
        byte[] transactionId,
        IReadOnlyList<StunAttribute> attributes) => new()
    {
        MessageClass = StunMessageClass.Request,
        MessageMethod = (StunMessageMethod)(ushort)method,
        TransactionId = transactionId,
        Attributes = attributes
    };

    private async Task<StunMessage> RoundTripAsync(StunMessage request, CancellationToken ct)
    {
        await _udp.SendAsync(_codec.Encode(request), ct).ConfigureAwait(false);

        while (true)
        {
            var received = await ReceiveWithTimeoutAsync(TransactionTimeout, ct).ConfigureAwait(false);
            var decoded = _codec.Decode(received.Buffer);
            if (decoded is null)
                continue; // ChannelData or non-STUN noise — not our response.
            if (!decoded.TransactionId.SequenceEqual(request.TransactionId))
                continue; // A different transaction; keep waiting.
            if (decoded.MessageClass is not (StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse))
                continue; // A stray indication racing the response; ignore.
            return decoded;
        }
    }

    // The single-round-trip delegate the TurnTransactionEngine drives: send one encoded request over the
    // stable socket and return the transaction-matched, validated success response — raising
    // TurnChallengeException on a 401/438 challenge so the engine's auth flow can react (same contract as
    // the production TurnClientTransport).
    private async Task<StunMessage> SendRequestOnceAsync(StunMessage request, byte[] requestBytes, CancellationToken ct)
    {
        await _udp.SendAsync(requestBytes, ct).ConfigureAwait(false);

        while (true)
        {
            var received = await ReceiveWithTimeoutAsync(TransactionTimeout, ct).ConfigureAwait(false);
            var decoded = _codec.Decode(received.Buffer);
            if (decoded is null || !decoded.TransactionId.SequenceEqual(request.TransactionId))
                continue; // ChannelData, noise, or a different transaction — keep waiting.
            if (decoded.MessageClass is not (StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse))
                continue; // A stray indication racing the response.
            return TurnResponseValidator.Validate(decoded, request.MessageMethod);
        }
    }

    private async Task<UdpReceiveResult> ReceiveWithTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await _udp.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No datagram received within {timeout.TotalMilliseconds:F0} ms.");
        }
    }

    private static void EnsureSuccess(StunMessage response, string operation)
    {
        if (response.MessageClass == StunMessageClass.SuccessResponse)
            return;

        var code = response.Attributes.OfType<ErrorCodeAttribute>().FirstOrDefault()?.Code;
        throw new InvalidOperationException(
            $"TURN {operation} failed: class {response.MessageClass}, error code {(code?.ToString() ?? "n/a")}.");
    }

    private static byte[] NewTransactionId()
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return transactionId;
    }
}
