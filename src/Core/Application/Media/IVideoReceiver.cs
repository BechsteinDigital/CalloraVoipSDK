using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Public per-call tap for inbound encoded video frames. Multiple receivers can attach to the same
/// call in parallel; every attached receiver observes every reassembled inbound frame. The SDK is
/// transport-only — decode the payload with your own codec.
/// </summary>
public interface IVideoReceiver : IDisposable
{
    /// <summary>
    /// Raised for every reassembled inbound video frame of the attached call.
    /// The event runs synchronously on the media path: handlers must not block and must not perform
    /// I/O inline — buffer into your own queue and return immediately, otherwise the RTP receive path
    /// is delayed.
    /// </summary>
    event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

    /// <summary>
    /// Attaches this receiver to one call. Attaching to a second call detaches from the first.
    /// </summary>
    void AttachToCall(ICall call);

    /// <summary>Detaches from the current call and stops frame delivery.</summary>
    void Detach();
}
