using CalloraVoipSdk.Core.Application.Media;

namespace CalloraVoipSdk.Core.Application.Ports.Video;

/// <summary>
/// Video device/codec abstraction used by the application layer — the seam that turns the SDK's
/// transport-only encoded-frame tap into an "audio-simple" video path. Implement this interface in a
/// codec package (VP8, H.264, …) to plug in capture + encode + decode + render; the SDK core ships no
/// codec and never encodes or decodes. Mirrors
/// <see cref="CalloraVoipSdk.Core.Application.Ports.Audio.IAudioDevice"/> for video: applications that
/// already own an encoder can keep using <see cref="IVideoSender"/>/<see cref="IVideoReceiver"/>
/// directly with encoded frames; this port is the bundled convenience layer on top.
/// </summary>
public interface IVideoDevice
{
    /// <summary>Human-readable device name for diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Connects the device to a call's encoded-video pipeline. After this call the device captures and
    /// encodes local video into <paramref name="sender"/> and decodes and renders the frames arriving on
    /// <paramref name="receiver"/>. <paramref name="parameters"/> carries the negotiated codec so the
    /// device encodes/decodes with the right payload format. The SDK stays transport-only — it moves the
    /// already-encoded frames both ways; the device owns the codec and the capture/render surfaces.
    /// </summary>
    /// <param name="receiver">Tap delivering inbound encoded frames for the device to decode and render.</param>
    /// <param name="sender">Tap accepting the device's outbound encoded frames.</param>
    /// <param name="parameters">Negotiated video codec/stream parameters for this call leg.</param>
    void Connect(IVideoReceiver receiver, IVideoSender sender, VideoConnectionParameters parameters);

    /// <summary>Disconnects from the current call and stops all video capture and rendering.</summary>
    void Disconnect();
}
