using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Gathers a stream relay ICE candidate over a persistent TCP/TLS connection to a TURN server (ADR-073 slice
/// 4c-i, #240) — the gathering-time producer that ties slices 1–4b together into one candidate the live session
/// can adopt. Given an already-connected stream, it allocates over it (<see cref="TurnStreamAllocationProbe"/>,
/// slice 2), then — on success — builds the <see cref="StreamRelayMediaTransport"/> (slice 1) that owns that
/// connection and the transport-agnostic <see cref="Common.Relay.RelayIceBinding"/> that drives its
/// permissions, send path, channel bind and keepalive.
/// <para>
/// The binding is produced by the same <see cref="WebRtcRelayBinding"/> factory the UDP relay uses: it is driven
/// entirely by a targeted raw-send, and for a stream that send collapses to a single stream write (there is one
/// connection, to the server), so the producer is reused verbatim — the payoff of the transport-agnostic seams
/// (ADR-073 decision 1). This producer only wires the transport-internal control plumbing (routing the relay
/// server's TURN control responses back into the binding's transactor); handing the candidate's send path to the
/// ICE agent and routing its inbound into consent/nomination is the session-assembly step (slice 4c-ii), driven
/// through <see cref="StreamRelayCandidate.Activate"/> and <see cref="StreamRelayCandidate.Binding"/>.
/// </para>
/// <para>
/// This lives in the WebRTC composition layer (which may depend on the TURN module) and hands the session only
/// the protocol-agnostic candidate, keeping the media path off the TURN module.
/// </para>
/// </summary>
internal sealed class WebRtcStreamRelayGatherer
{
    private readonly TurnStreamAllocationProbe _probe;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebRtcStreamRelayGatherer> _logger;

    /// <summary>Creates the gatherer over the shared STUN wire codec and logger factory.</summary>
    /// <param name="codec">The STUN wire codec.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="gatheringTimeout">
    /// The overall bound for one allocation attempt, forwarded to <see cref="TurnStreamAllocationProbe"/>; on
    /// expiry the gather returns <see langword="null"/> rather than hanging. Defaults to the probe's default (5 s).
    /// </param>
    public WebRtcStreamRelayGatherer(IStunMessageCodec codec, ILoggerFactory loggerFactory, TimeSpan? gatheringTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _probe = new TurnStreamAllocationProbe(codec, loggerFactory, gatheringTimeout);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebRtcStreamRelayGatherer>();
    }

    /// <summary>
    /// Attempts to gather a stream relay candidate over <paramref name="stream"/> against
    /// <paramref name="serverEndPoint"/>. On success the returned candidate owns the (still-open) stream through
    /// its transport; on a failed or timed-out allocation the stream is disposed and <see langword="null"/> is
    /// returned (no relay candidate — not fatal to gathering, as with srflx and the UDP relay).
    /// </summary>
    /// <param name="stream">The already-connected TCP/TLS stream to the TURN server (for TLS, already authenticated).</param>
    /// <param name="serverEndPoint">The TURN server's transport address.</param>
    /// <param name="credentials">Long-term credentials, or <see langword="null"/> for an open server.</param>
    /// <param name="lifetimeSeconds">Requested allocation lifetime, or <see langword="null"/> for the server default.</param>
    /// <param name="onInboundMedia">
    /// Sink for the inner payload of each inbound ChannelData frame — relayed media once a relay pair is nominated
    /// (RFC 8656 §12). Routed to the session's inbound pipeline by the consumer; no ChannelData arrives before
    /// nomination, so it is dormant through gathering and the checking phase.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The gathered candidate, or <see langword="null"/> on failure/timeout.</returns>
    public async Task<StreamRelayCandidate?> GatherAsync(
        Stream stream,
        IPEndPoint serverEndPoint,
        StunCredentials? credentials,
        uint? lifetimeSeconds,
        Action<byte[]> onInboundMedia,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(serverEndPoint);
        ArgumentNullException.ThrowIfNull(onInboundMedia);

        var allocation = await _probe
            .TryAllocateAsync(stream, serverEndPoint, credentials, lifetimeSeconds, ct)
            .ConfigureAwait(false);
        if (allocation is null)
        {
            // No relay candidate — dispose the stream we were handed (the probe leaves it open for hand-off).
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        // The transport's control sink is wired after the binding exists (the binding's transactor is what the
        // sink feeds), so it is a settable indirection the receive loop reads through the ctor delegate.
        Action<byte[]>? controlSink = null;
        var transport = new StreamRelayMediaTransport(
            stream,
            serverEndPoint,
            onRelayControl: bytes => controlSink?.Invoke(bytes),
            onInboundMedia: onInboundMedia,
            _loggerFactory.CreateLogger<StreamRelayMediaTransport>());

        // Reuse the UDP relay's binding producer: it needs only a targeted raw-send, and for a stream every send
        // goes to the one server connection, so the target is ignored and the send is a stream write.
        var binding = WebRtcRelayBinding
            .CreateFactory(serverEndPoint, allocation, _loggerFactory)
            .Invoke((bytes, _, sendCt) => transport.SendControlAsync(bytes, sendCt));
        if (binding is null)
        {
            // The factory only returns null when no allocation was gathered, which we already ruled out — but
            // stay defensive rather than dereference a null binding, and release the stream we own.
            _logger.LogWarning("The relay binding factory yielded no binding despite a gathered allocation; discarding the stream relay candidate.");
            await transport.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        // Route the relay server's TURN control responses (Allocate/CreatePermission/ChannelBind/Refresh) read off
        // the stream back into the binding's transactor, which correlates them by transaction id.
        controlSink = bytes => binding.OnControl(bytes);

        _logger.LogDebug(
            "Gathered a stream relay candidate: relayed endpoint {Relayed} via TURN server {Server} (RFC 8656 §2.1 over the stream).",
            allocation.RelayedEndPoint, serverEndPoint);
        return new StreamRelayCandidate(transport, binding, allocation.RelayedEndPoint, serverEndPoint);
    }
}
