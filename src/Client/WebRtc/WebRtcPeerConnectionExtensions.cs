using System.Collections.Concurrent;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The signalling happy path (Level 1) for <see cref="IPeerConnection"/>: given an app-owned
/// <see cref="IWebRtcSignaling"/> channel, the SDK drives the full offer/answer handshake and returns once
/// the connection is established — the WebRTC counterpart to the SIP <c>DialAndWaitUntilConnectedAsync</c>.
/// The neutral primitives (<see cref="IPeerConnection.CreateOffer"/>,
/// <see cref="IPeerConnection.SetRemoteDescriptionAsync"/>, <see cref="IPeerConnection.StartAsync"/>) remain
/// for callers that drive signalling themselves.
/// </summary>
public static class WebRtcPeerConnectionExtensions
{
    /// <summary>
    /// Drives the SDP handshake over <paramref name="signalling"/> for the given <paramref name="role"/> and
    /// starts media, completing when the peer reaches <see cref="PeerConnectionState.Connected"/>.
    /// </summary>
    /// <remarks>
    /// Subscribe to <see cref="IPeerConnection.TrackReceived"/> <em>before</em> calling this so inbound
    /// tracks are not missed. Early-bind gives even an ephemeral (port 0) client a live offer m-line; a
    /// fixed, reachable media port is still recommended for NAT reachability without TURN. When
    /// <paramref name="signalling"/> also implements <see cref="IWebRtcTrickleSignaling"/>, local candidates
    /// (host + server-reflexive) trickle out and remote candidates are applied during negotiation (ADR-012).
    /// <para>
    /// Trickle contract: the remote-candidate pump is cancelled and awaited before this method returns. Your
    /// <see cref="IWebRtcTrickleSignaling.ReceiveCandidateAsync"/> implementation SHOULD honour the supplied
    /// <see cref="CancellationToken"/> (return or throw <see cref="OperationCanceledException"/> when it
    /// fires), because that is what makes the teardown immediate. The SDK cannot force a non-cooperative
    /// channel to unblock, so the wait is bounded: after a short grace period the pump is abandoned and this
    /// method returns anyway — a channel that ignores its token costs a lingering background task, not a
    /// <c>ConnectAsync</c> that never returns on an already-connected peer (#166 P2-11).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="peer"/> or <paramref name="signalling"/> is null.</exception>
    /// <exception cref="WebRtcConnectException">The connection failed or was closed during negotiation.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task ConnectAsync(
        this IPeerConnection peer,
        IWebRtcSignaling signalling,
        WebRtcRole role,
        CancellationToken cancellationToken = default)
        => ConnectAsync(peer, signalling, role, DefaultPumpDrainTimeout, cancellationToken);

    // How long the cancelled candidate pump is awaited before it is abandoned. A cooperative channel finishes
    // this in microseconds; the grace period only bounds the damage a channel that ignores its token can do.
    // Internal (with the overload below) so a test can pin the abandonment without a multi-second wait.
    internal static readonly TimeSpan DefaultPumpDrainTimeout = TimeSpan.FromSeconds(5);

    internal static async Task ConnectAsync(
        this IPeerConnection peer,
        IWebRtcSignaling signalling,
        WebRtcRole role,
        TimeSpan pumpDrainTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(signalling);

        // Arm the connected-or-failed wait BEFORE any handshake step so no state transition is missed.
        var established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? sender, PeerConnectionState state)
        {
            switch (state)
            {
                case PeerConnectionState.Connected:
                    established.TrySetResult();
                    break;
                case PeerConnectionState.Failed:
                    established.TrySetException(new WebRtcConnectException("The peer connection failed during negotiation."));
                    break;
                case PeerConnectionState.Closed:
                    established.TrySetException(new WebRtcConnectException("The peer connection was closed during negotiation."));
                    break;
            }
        }

        var trickle = signalling as IWebRtcTrickleSignaling;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var localCandidates = new ConcurrentQueue<string>();
        void OnLocalCandidate(object? sender, string candidate) => localCandidates.Enqueue(candidate);
        Task? candidatePump = null;

        peer.ConnectionStateChanged += OnStateChanged;
        try
        {
            // RFC 8829 offer/answer, carried over the app's signalling channel.
            if (role == WebRtcRole.Offerer)
            {
                var offer = peer.CreateOffer();
                await signalling.SendDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
                var answer = await signalling.ReceiveDescriptionAsync(cancellationToken).ConfigureAwait(false);
                await peer.SetRemoteDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var offer = await signalling.ReceiveDescriptionAsync(cancellationToken).ConfigureAwait(false);
                var answer = await peer.SetRemoteDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
                await signalling.SendDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);
            }

            // RFC 8838 trickle: only when the app supplies a trickle channel — otherwise the offer/answer
            // candidates are all that is exchanged. The host candidate already rode the SDP; gathering adds
            // server-reflexive candidates, which must run BEFORE StartAsync (it shares the media socket).
            if (trickle is not null)
            {
                // Apply remote candidates as they arrive, concurrently with the rest of the handshake.
                candidatePump = PumpRemoteCandidatesAsync(peer, trickle, established, connectCts.Token);

                peer.LocalIceCandidateDiscovered += OnLocalCandidate;
                await peer.GatherCandidatesAsync(connectCts.Token).ConfigureAwait(false);
                peer.LocalIceCandidateDiscovered -= OnLocalCandidate;

                while (localCandidates.TryDequeue(out var candidate))
                    await trickle.SendCandidateAsync(candidate, connectCts.Token).ConfigureAwait(false);
                await trickle.SendEndOfCandidatesAsync(connectCts.Token).ConfigureAwait(false);
            }

            await peer.StartAsync(cancellationToken).ConfigureAwait(false);

            using (cancellationToken.Register(() => established.TrySetCanceled(cancellationToken)))
            {
                await established.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            peer.ConnectionStateChanged -= OnStateChanged;
            peer.LocalIceCandidateDiscovered -= OnLocalCandidate; // no-op if never added / already removed
            await connectCts.CancelAsync().ConfigureAwait(false);
            if (candidatePump is not null)
                await DrainPumpAsync(candidatePump, pumpDrainTimeout).ConfigureAwait(false);
        }
    }

    // Awaits the cancelled pump for a bounded grace period (#166 P2-11). The pump observes cancellation
    // internally and never throws out, so a cooperative channel completes this immediately. A channel that
    // ignores its token blocks inside ReceiveCandidateAsync forever, and awaiting it unconditionally — inside a
    // finally, after a peer that may already have reached Connected — turned an app-side signalling bug into a
    // public Connect task that never returns. Such a pump is abandoned instead: it keeps running until its
    // channel unblocks, its eventual fault is observed rather than dropped, and Connect returns.
    private static async Task DrainPumpAsync(Task pump, TimeSpan drainTimeout)
    {
        if (pump.IsCompleted)
        {
            await pump.ConfigureAwait(false);
            return;
        }

        using var expiry = new CancellationTokenSource(drainTimeout);
        try
        {
            await pump.WaitAsync(expiry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            ObserveAbandoned(pump);
        }
    }

    // Keeps a fault on the abandoned pump observed (K3: fire-and-forget only with fault observation), so a
    // channel that eventually throws cannot surface as an unobserved task exception in the app's process.
    private static void ObserveAbandoned(Task pump)
        => _ = pump.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // Applies remote ICE candidates as they trickle in (RFC 8838) until the remote signals end-of-candidates
    // (a null line, RFC 8840) or the connection resolves. A signalling-channel failure surfaces through the
    // establishment wait, but only if the connection has not already been established (TrySetException no-ops
    // once Connected won the race), so a late channel error never masks a working connection.
    private static async Task PumpRemoteCandidatesAsync(
        IPeerConnection peer, IWebRtcTrickleSignaling trickle, TaskCompletionSource established, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var candidate = await trickle.ReceiveCandidateAsync(cancellationToken).ConfigureAwait(false);
                if (candidate is null)
                    break; // remote end-of-candidates
                await peer.AddIceCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The connection resolved or the caller cancelled — stop polling.
        }
        catch (Exception ex)
        {
            established.TrySetException(
                new WebRtcConnectException("The signalling channel failed while trickling ICE candidates.", ex));
        }
    }
}
