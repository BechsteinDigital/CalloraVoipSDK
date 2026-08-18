using Microsoft.Extensions.Logging;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Gathers a TURN relay allocation over an <em>already-connected</em> TCP/TLS stream to the server — the
/// stream counterpart of the UDP <see cref="TurnAllocationProbe"/> (ADR-073 slice 2, #240). Where the UDP
/// probe allocates on an already-bound media socket, this allocates over a stream the caller has already
/// connected (and, for TLS, authenticated), because a stream relay has no shared media socket: the connection
/// itself is the transport (ADR-073 decision 2).
/// <para>
/// It drives <see cref="TurnRelayControlClient.AllocateAsync"/> over the stream: requests are written as
/// stream frames (STUN is self-framing by length, RFC 8489 §5) and a temporary receive loop feeds inbound
/// frames read by <see cref="TurnStreamFramer"/> into the transactor until the allocation completes, then
/// stops. The stream is <b>not</b> disposed — on success the caller hands the same live connection to a
/// <see cref="StreamRelayMediaTransport"/> and the relay coordinator to continue (permission / channel-bind /
/// refresh) without re-allocating, mirroring the UDP probe's socket-continuity contract. A failed or timed-out
/// allocation returns <see langword="null"/> (no relay candidate — not fatal to gathering, as with srflx); the
/// caller then disposes the stream it opened.
/// </para>
/// </summary>
internal sealed class TurnStreamAllocationProbe
{
    private static readonly TimeSpan DefaultGatheringTimeout = TimeSpan.FromSeconds(5);

    private readonly IStunMessageCodec _codec;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TurnStreamAllocationProbe> _logger;
    private readonly TimeSpan _gatheringTimeout;

    /// <summary>Creates the probe over the shared STUN wire codec and logger factory.</summary>
    /// <param name="codec">The STUN wire codec.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="gatheringTimeout">
    /// The overall bound for one allocation attempt; on expiry the probe gives up and returns
    /// <see langword="null"/> rather than hanging through the transactor's full RTO schedule against a silent
    /// server. Defaults to 5 s. Injectable for tests.
    /// </param>
    public TurnStreamAllocationProbe(IStunMessageCodec codec, ILoggerFactory loggerFactory, TimeSpan? gatheringTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (gatheringTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gatheringTimeout), "The gathering timeout must be positive.");
        _codec = codec;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TurnStreamAllocationProbe>();
        _gatheringTimeout = gatheringTimeout ?? DefaultGatheringTimeout;
    }

    /// <summary>
    /// Attempts a relay allocation over <paramref name="stream"/> against <paramref name="serverEndPoint"/>.
    /// Returns the allocation on success, or <see langword="null"/> when it fails or times out. The stream is
    /// left open either way — the caller owns its lifetime (hand it on to the transport on success, dispose it
    /// on failure).
    /// </summary>
    /// <param name="stream">The already-connected TCP/TLS stream to the TURN server.</param>
    /// <param name="serverEndPoint">The TURN server's transport address (for the returned candidate's server match).</param>
    /// <param name="credentials">Long-term credentials, or <see langword="null"/> for an open server.</param>
    /// <param name="lifetimeSeconds">Requested allocation lifetime, or <see langword="null"/> for the server default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocation result, or <see langword="null"/> on failure/timeout.</returns>
    public async Task<TurnAllocateResult?> TryAllocateAsync(
        Stream stream,
        IPEndPoint serverEndPoint,
        StunCredentials? credentials,
        uint? lifetimeSeconds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(serverEndPoint);

        var transactor = new TurnControlTransactor(
            _codec,
            async (request, token) =>
            {
                // The allocation is a sequential request/response (retransmits are serialised by the
                // transactor's RTO), so a plain write is safe here — no other writer shares the stream yet.
                await stream.WriteAsync(request, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            },
            _loggerFactory.CreateLogger<TurnControlTransactor>());
        var control = new TurnRelayControlClient(new TurnTransactionEngine(_codec), transactor);

        using var timeoutCts = new CancellationTokenSource(_gatheringTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var receiveLoop = RunReceiveLoopAsync(stream, transactor, linkedCts.Token);
        try
        {
            return await control.AllocateAsync(credentials, lifetimeSeconds, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("TURN stream allocation gave up after the {Timeout} gathering timeout.", _gatheringTimeout);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A TURN failure (auth rejected, error response, malformed reply) simply means no relay candidate,
            // exactly as a failed srflx probe yields none — logged, not thrown.
            _logger.LogDebug(ex, "TURN stream allocation failed; no relay candidate gathered.");
            return null;
        }
        finally
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await receiveLoop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The loop ends on cancellation; a stream fault surfaced here during teardown is logged rather
                // than swallowed so a gathering anomaly stays visible.
                _logger.LogDebug(ex, "TURN stream allocation receive loop ended with an exception.");
            }
        }
    }

    // Reads framed messages off the stream for the duration of the allocation and feeds STUN control frames
    // into the transactor. ChannelData cannot legitimately arrive before a channel is bound, so it is ignored.
    private async Task RunReceiveLoopAsync(Stream stream, TurnControlTransactor transactor, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await TurnStreamFramer.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    _logger.LogDebug("TURN stream closed by the server during allocation.");
                    return;
                }

                if (!frame.IsChannelData)
                    transactor.OnControlDatagram(frame.Payload);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop once the allocation completed or timed out.
        }
    }
}
