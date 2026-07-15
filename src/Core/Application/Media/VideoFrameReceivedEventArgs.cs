namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Event payload for <see cref="IVideoReceiver.FrameReceived"/>. Delivered synchronously on the
/// media path — handlers must return quickly (see the event's documentation).
/// </summary>
public sealed class VideoFrameReceivedEventArgs : EventArgs
{
    /// <summary>The received encoded video frame.</summary>
    public VideoFrame Frame { get; }

    /// <summary>Creates event args carrying one received frame.</summary>
    public VideoFrameReceivedEventArgs(VideoFrame frame) => Frame = frame;
}
