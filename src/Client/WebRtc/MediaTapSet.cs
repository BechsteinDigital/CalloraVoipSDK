using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Holds the media taps attached to a peer and dispatches encoded frames to them — fault-isolated (a
/// throwing tap is logged, never breaking the media path) and allocation-free on the hot path (copy-on-write
/// snapshot; an empty set costs nothing). Extracted so the dispatch is unit-testable without a live peer.
/// </summary>
internal sealed class MediaTapSet
{
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private volatile IMediaTap[] _taps = [];

    public MediaTapSet(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public IDisposable Attach(IMediaTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        lock (_sync)
        {
            _taps = [.. _taps, tap];
        }
        return new MediaTapHandle(this, tap);
    }

    public void Audio(MediaDirection direction, ReadOnlyMemory<byte> payload)
    {
        foreach (var tap in _taps)
        {
            try
            {
                tap.OnAudio(direction, payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A media tap threw on the {Direction} audio path and was isolated.", direction);
            }
        }
    }

    public void Video(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame, string? rid)
    {
        foreach (var tap in _taps)
        {
            try
            {
                tap.OnVideo(direction, frame, rtpTimestamp, isKeyFrame, rid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A media tap threw on the {Direction} video path and was isolated.", direction);
            }
        }
    }

    internal void Detach(IMediaTap tap)
    {
        lock (_sync)
        {
            _taps = Array.FindAll(_taps, t => !ReferenceEquals(t, tap));
        }
    }
}
