using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Gathers a TURN relay allocation on an <em>already-bound</em> media socket — the TURN analog of the STUN
/// server-reflexive probe. ICE candidate gathering runs before the media transport takes over the socket, so
/// the allocation must be made on that same socket's 5-tuple: the TURN server binds the relayed address to
/// the client transport address it sees, and only an allocation on the media socket yields a relay candidate
/// whose data path the transport can later carry.
/// <para>
/// It drives <see cref="TurnRelayControlClient.AllocateAsync"/> (the shared auth engine + transactor) over
/// the raw socket: requests are sent to the TURN server through the socket, and a temporary receive loop
/// feeds inbound datagrams into the transactor until the allocation completes, then stops. The returned
/// <see cref="TurnAllocateResult"/> (relayed endpoint, lifetime, effective credentials) is what a caller
/// advertises as the relay candidate and later hands to the relay coordinator to continue the allocation
/// (permission / channel-bind / refresh) on the same socket without re-allocating. A failed allocation
/// returns <see langword="null"/> — a missing relay candidate is not fatal to gathering (as with srflx).
/// </para>
/// </summary>
internal sealed class TurnAllocationProbe
{
    private const int ReceiveBufferSize = 4096;

    private static readonly TimeSpan DefaultGatheringTimeout = TimeSpan.FromSeconds(5);

    private readonly IStunMessageCodec _codec;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TurnAllocationProbe> _logger;
    private readonly TimeSpan _gatheringTimeout;

    /// <summary>Creates the probe over the shared STUN wire codec and logger factory.</summary>
    /// <param name="codec">The STUN wire codec.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="gatheringTimeout">
    /// The overall bound for one allocation attempt; on expiry the probe gives up and returns
    /// <see langword="null"/> (no relay candidate) rather than hanging through the transactor's full RTO
    /// schedule against a silent server. Defaults to 5 s. Injectable for tests.
    /// </param>
    public TurnAllocationProbe(IStunMessageCodec codec, ILoggerFactory loggerFactory, TimeSpan? gatheringTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (gatheringTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gatheringTimeout), "The gathering timeout must be positive.");
        _codec = codec;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TurnAllocationProbe>();
        _gatheringTimeout = gatheringTimeout ?? DefaultGatheringTimeout;
    }

    /// <summary>
    /// Attempts a UDP relay allocation on <paramref name="socket"/> against <paramref name="serverEndPoint"/>.
    /// Returns the allocation on success, or <see langword="null"/> when the allocation fails (the relay is
    /// simply not offered as a candidate then).
    /// </summary>
    /// <param name="socket">The already-bound media socket the allocation is made on.</param>
    /// <param name="serverEndPoint">The TURN server's transport address.</param>
    /// <param name="credentials">Long-term credentials, or <see langword="null"/> for an open server.</param>
    /// <param name="lifetimeSeconds">Requested allocation lifetime, or <see langword="null"/> for the server default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocation result, or <see langword="null"/> on failure.</returns>
    public async Task<TurnAllocateResult?> TryAllocateAsync(
        Socket socket,
        IPEndPoint serverEndPoint,
        StunCredentials? credentials,
        uint? lifetimeSeconds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(serverEndPoint);

        var transactor = new TurnControlTransactor(
            _codec,
            (request, token) => socket.SendToAsync(request, SocketFlags.None, serverEndPoint, token).AsTask(),
            _loggerFactory.CreateLogger<TurnControlTransactor>());
        var control = new TurnRelayControlClient(new TurnTransactionEngine(_codec), transactor);

        // Bound the whole attempt so a silent server does not hang through the transactor's full RTO schedule.
        // The internal timeout yields null (no relay candidate); the caller's own cancellation propagates.
        using var timeoutCts = new CancellationTokenSource(_gatheringTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var receiveLoop = RunReceiveLoopAsync(socket, transactor, linkedCts.Token);
        try
        {
            return await control.AllocateAsync(credentials, lifetimeSeconds, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "TURN allocation on {Server} did not complete within {Timeout}; no relay candidate gathered.",
                serverEndPoint, _gatheringTimeout);
            return null;
        }
        catch (TurnChallengeException ex)
        {
            _logger.LogDebug(
                ex, "TURN allocation on {Server} exhausted the auth challenge/retry; no relay candidate gathered.",
                serverEndPoint);
            return null;
        }
        catch (TurnException ex)
        {
            _logger.LogDebug(ex, "TURN allocation on {Server} failed; no relay candidate gathered.", serverEndPoint);
            return null;
        }
        finally
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await receiveLoop.ConfigureAwait(false);
        }
    }

    // Feeds inbound datagrams from the socket into the transactor for the duration of the allocation. The
    // transactor matches responses to the pending request by transaction id; unrelated datagrams do not match
    // and are ignored, so no source filter is needed here (and gathering runs before other socket traffic).
    private async Task RunReceiveLoopAsync(Socket socket, TurnControlTransactor transactor, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var remoteTemplate = new IPEndPoint(
            socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                // ToArray inside OnControlDatagram copies before the buffer is reused, so passing the pooled
                // slice is safe.
                transactor.OnControlDatagram(buffer.AsMemory(0, received.ReceivedBytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
