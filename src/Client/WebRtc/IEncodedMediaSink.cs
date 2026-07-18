namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A destination for recorded encoded media. The recorder writes every captured <see cref="RecordedFrame"/>
/// here (both directions, audio and video); the sink decides how to persist it — a per-track file, a
/// container, an upload, an in-memory buffer, … Transport-only: payloads are the raw codec bitstream (no
/// decoding). A container writer (Opus→Ogg, VP8→IVF, muxed MP4/…) is a concrete sink implementation.
/// </summary>
/// <remarks>
/// <see cref="Write"/> is invoked on the media path — it must return quickly and should not throw (a
/// throwing sink is isolated from the media flow and its frame is dropped). <see cref="CompleteAsync"/> is
/// invoked exactly once when the recording stops, to flush and finalise.
/// </remarks>
public interface IEncodedMediaSink
{
    /// <summary>Persists one captured frame.</summary>
    void Write(in RecordedFrame frame);

    /// <summary>Flushes and finalises the sink; called once when recording stops.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}
