using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Public per-call injection point for outbound media frames. Frames are handed to the call's
/// active send path; the payload must already be encoded in the negotiated codec
/// (see <see cref="ICall.MediaParameters"/> for payload type and clock rate).
/// </summary>
public interface IMediaSender : IDisposable
{
    /// <summary>
    /// Attaches this sender to one call. Attaching to a second call detaches from the first.
    /// </summary>
    void AttachToCall(ICall call);

    /// <summary>Detaches from the current call.</summary>
    void Detach();

    /// <summary>
    /// Sends one encoded media frame into the attached call. Frames sent while the call is not
    /// in <see cref="CallState.Connected"/> or <see cref="CallState.OnHold"/> are dropped.
    /// </summary>
    Task SendAsync(MediaFrame frame, CancellationToken ct = default);
}
