namespace CalloraVoipSdk.Core.Application.Ports.Video;

/// <summary>
/// Codec and stream parameters passed to <see cref="IVideoDevice.Connect"/> so the device encodes and
/// decodes with the negotiated video payload format. Mirrors
/// <see cref="CalloraVoipSdk.Core.Application.Ports.Audio.AudioConnectionParameters"/> for video. The
/// SDK is transport-only: capture resolution and frame rate are the device's concern, not the SDK's, so
/// they are deliberately absent here — only the negotiated transport format is carried.
/// </summary>
public sealed class VideoConnectionParameters
{
    /// <summary>Sensible defaults for VP8 on the 90 kHz video clock.</summary>
    public static readonly VideoConnectionParameters Default = new();

    /// <summary>Negotiated RTP payload type for the video codec (dynamic, for example 96).</summary>
    public int PayloadType { get; init; } = 96;

    /// <summary>Normalized negotiated codec name (for example VP8 or H264).</summary>
    public string CodecName { get; init; } = "VP8";

    /// <summary>RTP clock rate in Hz — 90000 for video (RFC 3551).</summary>
    public int ClockRate { get; init; } = 90_000;

    /// <summary>
    /// Builds transport-only connection parameters from the negotiated video parameters of a call, so the
    /// device encodes/decodes in the SDP-agreed format. Mirrors
    /// <see cref="CalloraVoipSdk.Core.Application.Ports.Audio.AudioConnectionParameters.From"/>.
    /// </summary>
    /// <param name="video">The negotiated video parameters from <c>CallMediaParameters.Video</c>.</param>
    public static VideoConnectionParameters From(CalloraVoipSdk.Core.Domain.Calls.CallVideoParameters video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return new VideoConnectionParameters
        {
            PayloadType = video.PayloadType,
            CodecName = video.CodecName,
            ClockRate = video.ClockRate,
        };
    }
}
