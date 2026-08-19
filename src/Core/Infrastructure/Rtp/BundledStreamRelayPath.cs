using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Owns a bundle's adopted stream relay (ADR-073): a TURN relay that carries its own send and receive over its
/// own TCP/TLS connection, distinct from the shared UDP media socket. Extracted from
/// <see cref="BundledMediaSession"/> (mirroring <see cref="BundledRelayDataPath"/> for the UDP relay) — the
/// adoption, the one-shot media transition when its pair wins ICE, and the teardown order are a self-contained
/// lifecycle running across the transport and the ICE agent.
/// <para>
/// Unlike the UDP relay, a stream relay is wired into the ICE agent as a second local candidate with its own
/// per-candidate nomination hook, and on nomination the shared transport's media is switched onto the stream
/// (<see cref="BundledMediaTransport.EnterStreamRelayMode"/>) rather than the socket being flipped in place.
/// </para>
/// </summary>
internal sealed class BundledStreamRelayPath : IAsyncDisposable
{
    private readonly BundledMediaTransport _transport;
    private readonly BundledIceControl _ice;
    private readonly ILogger _logger;

    private IStreamRelayAttachment? _attachment;
    private int _wired;

    // The one-shot direct→stream media transition, kicked off on the ICE driver thread when the stream relay pair
    // wins. Guarded so it runs at most once; cancelled and awaited before the attachment and the shared transport
    // it rides are disposed.
    private int _transitionStarted;
    private Task? _transitionTask;
    private readonly CancellationTokenSource _transitionCts = new();

    // Set once the media transition succeeded (EnterStreamRelayMode ran) — media now rides the stream.
    private int _active;

    /// <param name="transport">The shared bundle transport whose media this path switches onto the stream.</param>
    /// <param name="ice">The bundle's ICE agent the relay candidate is wired into.</param>
    /// <param name="logger">The owning session's logger.</param>
    public BundledStreamRelayPath(BundledMediaTransport transport, BundledIceControl ice, ILogger logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ice = ice ?? throw new ArgumentNullException(nameof(ice));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the session's media has switched onto the adopted stream relay (ADR-073).</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>
    /// Adopts a stream relay candidate into the ICE agent alongside the direct candidates, direct-preferred by
    /// pair priority (RFC 8445 §6.1.2.3): its relayed inbound checks route into the agent through
    /// <see cref="BundledIceControl.OnRelayStunReceived"/> and it gains the relay send path plus a per-candidate
    /// nomination hook that switches the session's media onto its transport when it wins. Order matters — the
    /// inbound route and the transport's receive loop are wired (Activate) before the candidate is checked.
    /// Single-shot: a second adoption is a no-op that disposes the redundant candidate.
    /// </summary>
    /// <param name="streamRelay">The gathered stream relay candidate to adopt.</param>
    public void Adopt(IStreamRelayAttachment streamRelay)
    {
        ArgumentNullException.ThrowIfNull(streamRelay);
        if (Interlocked.Exchange(ref _wired, 1) != 0)
        {
            _logger.LogWarning("A stream relay was already adopted for this session; disposing the redundant candidate.");
            _ = DisposeRedundantAsync(streamRelay);
            return;
        }

        _attachment = streamRelay;
        // Wire the inbound route and start the relay transport's receive loop before the candidate is checked, so
        // a relayed inbound check has a live route into the ICE agent. The response goes back the way it came.
        streamRelay.Activate((peer, inner) => _ice.OnRelayStunReceived(inner, peer, streamRelay.RelaySend));
        _ice.AddRelayLocalCandidate(
            streamRelay.RelaySend,
            streamRelay.EnsurePermission,
            onNominated: peer => OnNominated(peer, streamRelay));
        // Keep the allocation alive (RFC 8656 §3.9). Idempotent — Start also starts it for a pre-start adoption.
        streamRelay.StartKeepAlive();
    }

    /// <summary>Starts the adopted allocation keepalive once the transport is up (idempotent; a no-op without one).</summary>
    public void Start() => _attachment?.StartKeepAlive();

    // The stream relay pair won ICE: switch the session's media onto its transport. Runs on the driver thread —
    // it only kicks off the async transition, at most once, and returns.
    private void OnNominated(IPEndPoint peer, IStreamRelayAttachment streamRelay)
    {
        if (Interlocked.Exchange(ref _transitionStarted, 1) != 0)
            return;
        _transitionTask = Task.Run(() => TransitionAsync(peer, streamRelay));
    }

    // ChannelBind the peer over the stream (RFC 8656 §11), route relayed inbound media into our pipeline, and
    // switch the shared transport's media send onto the stream (ADR-073). A failed bind leaves media on the
    // direct path — which, being dead (the relay only won because direct failed), then fails consent and the
    // pair goes down (ADR-073 §3 fail-and-renominate). A disposing session cancels the transition.
    private async Task TransitionAsync(IPEndPoint peer, IStreamRelayAttachment streamRelay)
    {
        try
        {
            // Attribute relayed inbound to the peer before media rides the stream.
            _transport.SetRemoteEndPoint(peer);
            var streamMediaSend = await streamRelay
                .BindChannelAsync(peer, inner => _transport.InjectRelayedInbound(inner, peer), _transitionCts.Token)
                .ConfigureAwait(false);
            if (streamMediaSend is null)
            {
                _logger.LogWarning(
                    "Stream relay pair nominated but no channel could be bound; media stays on the direct path (ADR-073).");
                return;
            }

            _transport.EnterStreamRelayMode(streamMediaSend);
            Volatile.Write(ref _active, 1);
            _logger.LogInformation(
                "Stream relay data path activated: media now flows as ChannelData over the TURN stream connection " +
                "(RFC 8656 §11–12, ADR-073).");
        }
        catch (OperationCanceledException) when (_transitionCts.IsCancellationRequested)
        {
            // Normal teardown cancelled the transition.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream relay media transition failed; media stays on the direct path.");
        }
    }

    private async Task DisposeRedundantAsync(IStreamRelayAttachment streamRelay)
    {
        try
        {
            await streamRelay.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing a redundant stream relay candidate failed.");
        }
    }

    /// <summary>
    /// Drains an in-flight media transition (it rides both the stream relay's transport and the shared transport,
    /// so it must finish before either is disposed), then disposes the adopted stream relay — which itself
    /// disposes its keepalives before its transport. Called after the ICE agent is disposed and before the shared
    /// transport.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _transitionCts.CancelAsync().ConfigureAwait(false);
        if (_transitionTask is { } transition)
        {
            try { await transition.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Stream relay media transition ended with an exception during dispose."); }
        }
        _transitionCts.Dispose();

        if (_attachment is { } attachment)
            await attachment.DisposeAsync().ConfigureAwait(false);
    }
}
