namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Event payload for <see cref="IMediaReceiver.FrameReceived"/>. Delivered synchronously on the
/// media path — handlers must return quickly (see the event's documentation).
/// </summary>
public sealed class MediaFrameReceivedEventArgs : EventArgs
{
    /// <summary>The received encoded media frame.</summary>
    public MediaFrame Frame { get; }

    /// <summary>Creates event args carrying one received frame.</summary>
    public MediaFrameReceivedEventArgs(MediaFrame frame) => Frame = frame;
}
