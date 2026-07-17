namespace CalloraVoipSdk.WebRtc;

/// <summary>The media kind of a remote WebRTC track surfaced by <see cref="IPeerConnection.TrackReceived"/>.</summary>
public enum TrackKind
{
    /// <summary>An audio track.</summary>
    Audio,

    /// <summary>A video track.</summary>
    Video,
}
