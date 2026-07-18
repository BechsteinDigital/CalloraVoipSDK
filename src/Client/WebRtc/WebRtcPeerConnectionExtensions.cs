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
    /// tracks are not missed. As the offerer, configure a reachable media port on the client (an ephemeral
    /// port yields a disabled offer m-line until early-bind/trickle ICE lands — see ADR-012).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="peer"/> or <paramref name="signalling"/> is null.</exception>
    /// <exception cref="WebRtcConnectException">The connection failed or was closed during negotiation.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task ConnectAsync(
        this IPeerConnection peer,
        IWebRtcSignaling signalling,
        WebRtcRole role,
        CancellationToken cancellationToken = default)
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

            await peer.StartAsync(cancellationToken).ConfigureAwait(false);

            using (cancellationToken.Register(() => established.TrySetCanceled(cancellationToken)))
            {
                await established.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            peer.ConnectionStateChanged -= OnStateChanged;
        }
    }
}
