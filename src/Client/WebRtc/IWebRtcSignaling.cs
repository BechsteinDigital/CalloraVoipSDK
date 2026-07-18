namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The app-owned signalling channel the SDK drives during <see cref="WebRtcPeerConnectionExtensions.ConnectAsync"/>:
/// a bidirectional transport (WebSocket, HTTP long-poll, Callora signalling, …) that carries SDP
/// descriptions between the two peers. The SDK stays signalling-neutral — it only sends and receives SDP
/// through this contract; the app decides how the bytes travel.
/// </summary>
public interface IWebRtcSignaling
{
    /// <summary>Sends a local SDP description (offer or answer) to the remote peer over the app's channel.</summary>
    Task SendDescriptionAsync(string sdp, CancellationToken cancellationToken = default);

    /// <summary>Awaits the next SDP description (answer or offer) from the remote peer on the app's channel.</summary>
    Task<string> ReceiveDescriptionAsync(CancellationToken cancellationToken = default);
}
