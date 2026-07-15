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

    /// <summary>
    /// The SDK's ready-to-use recommended outbound video bitrate in bits per second for the attached
    /// call — set your encoder to it. <see langword="null"/> when no call is attached or transport-cc
    /// congestion control is inactive for the leg.
    /// </summary>
    long? RecommendedBitrateBps { get; }

    /// <summary>
    /// Coarse network quality for the attached call, or <see langword="null"/> when no call is attached
    /// or congestion control is inactive.
    /// </summary>
    NetworkQuality? NetworkQuality { get; }

    /// <summary>
    /// Raised when the SDK's recommendation for the attached call changes — subscribe and adjust your
    /// encoder's target bitrate. Stops firing on <see cref="Detach"/>/dispose. The typical usage is the
    /// whole point of the API: <c>sender.RecommendedBitrateChanged += (_, e) =&gt; encoder.SetBitrate(e.RecommendedBitrateBps);</c>
    /// </summary>
    event EventHandler<VideoBitrateRecommendationEventArgs>? RecommendedBitrateChanged;
}
