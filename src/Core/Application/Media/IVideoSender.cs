using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Public per-call injection point for outbound encoded video frames. Frames are handed to the call's
/// active video send path; the payload must already be encoded in the negotiated codec
/// (see <see cref="ICall.MediaParameters"/> for the negotiated media). The SDK is transport-only and
/// never encodes.
/// </summary>
public interface IVideoSender : IDisposable
{
    /// <summary>
    /// Attaches this sender to one call. Attaching to a second call detaches from the first.
    /// </summary>
    void AttachToCall(ICall call);

    /// <summary>Detaches from the current call.</summary>
    void Detach();

    /// <summary>
    /// Sends one encoded video frame into the attached call. Frames sent while the call is not in
    /// <see cref="CallState.Connected"/> or <see cref="CallState.OnHold"/> are dropped.
    /// </summary>
    Task SendAsync(VideoFrame frame, CancellationToken ct = default);
}
