using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Public per-call tap for inbound media frames. Multiple receivers can be attached to the
/// same call in parallel (e.g. default audio, recording and a realtime bridge); every attached
/// receiver observes every inbound frame.
/// </summary>
public interface IMediaReceiver : IDisposable
{
    /// <summary>
    /// Raised for every inbound media frame of the attached call.
    /// The event runs synchronously on the media path: handlers must not block and must not
    /// perform I/O inline — buffer into your own queue and return immediately, otherwise the
    /// RTP receive path is delayed.
    /// </summary>
    event EventHandler<MediaFrameReceivedEventArgs>? FrameReceived;

    /// <summary>
    /// Attaches this receiver to one call. Attaching to a second call detaches from the first.
    /// </summary>
    void AttachToCall(ICall call);

    /// <summary>Detaches from the current call and stops frame delivery.</summary>
    void Detach();
}
