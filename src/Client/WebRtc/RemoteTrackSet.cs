using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Projects the peer's inbound media onto the W3C per-track model. A track is materialised — and its
/// track-received callback raised exactly once — either when the remote description is applied (the W3C
/// <c>ontrack</c> point) or, as a fallback, on the first frame of that kind. Materialising up front lets a
/// handler subscribe to <see cref="RemoteTrack.FrameReceived"/> before any media arrives.
/// </summary>
/// <remarks>
/// N audio tracks and N video tracks, each keyed by MID (P2c / 4.7.0: several remote cameras/screen-shares, or
/// several remote participants' audio streams, stay separable). A track without a MID (a legacy 1+1 remote, or
/// the primary audio anchor addressed via the mid-less path) is keyed under the empty string, so the single-audio
/// and single-video paths each materialise exactly one track.
/// <para>
/// Precondition: inbound frames are delivered <em>serially</em> — the peer's transport dispatches every
/// <c>AudioReceived</c>/<c>AudioTrackFrameReceived</c>/<c>VideoFrameReceived</c> from a single receive loop. The
/// lock guarantees exactly one track per key and exactly one callback under any interleaving. Once tracks are
/// materialised from the remote description, frame delivery simply routes to the existing track.
/// </para>
/// </remarks>
internal sealed class RemoteTrackSet
{
    /// <summary>
    /// Cumulative cap on retained remote tracks per kind (#166 P1-4). A track is keyed by MID and never removed,
    /// so a sequence of reoffers carrying fresh MIDs would otherwise grow the retained set over the peer's whole
    /// lifetime — unbounded by any single-SDP cap. Far above any real peer-to-peer session; beyond it a new MID
    /// is not materialised and its frames are dropped, so retention stays bounded.
    /// </summary>
    private const int MaxTracksPerKind = 128;

    private readonly object _sync = new();
    private readonly Action<RemoteTrack> _onTrackReceived;
    private readonly ILogger _logger;
    // Audio tracks keyed by MID (empty string for the primary/anchor addressed via the mid-less path), so N remote
    // audio m-lines (4.7.0) materialise N distinct tracks and each inbound frame routes to its own track.
    private readonly Dictionary<string, RemoteTrack> _audio = new(StringComparer.Ordinal);
    // Video tracks keyed by MID (empty string for a MID-less legacy 1+1 remote), so N remote video m-lines
    // materialise N distinct tracks and each inbound frame routes to its own track.
    private readonly Dictionary<string, RemoteTrack> _video = new(StringComparer.Ordinal);

    /// <param name="onTrackReceived">Raises the facade's track-received event for a newly materialised track.</param>
    /// <param name="logger">
    /// Logs a throwing <see cref="RemoteTrack.FrameReceived"/> subscriber instead of letting it break the media
    /// receive loop (#166 P3-14). Null uses no logging, for tests that only assert the projection.
    /// </param>
    public RemoteTrackSet(Action<RemoteTrack> onTrackReceived, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(onTrackReceived);
        _onTrackReceived = onTrackReceived;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Materialises the audio track for <paramref name="mid"/> (raising the callback once) without delivering a
    /// frame. A null MID keys the primary/anchor audio track (the mid-less path), so it stays one track — backward
    /// compatible with the pre-4.7.0 single-audio path.
    /// </summary>
    public RemoteTrack? EnsureAudioTrack(string? mid, string? streamId, string? trackId)
    {
        var key = mid ?? string.Empty;
        RemoteTrack? created = null;
        RemoteTrack track;
        lock (_sync)
        {
            if (!_audio.TryGetValue(key, out var existing))
            {
                // #166 P1-4: beyond the cumulative cap a fresh MID is not retained (no track, no callback), so a
                // reoffer flood of new MIDs cannot grow retention without limit. Its frames are dropped.
                if (_audio.Count >= MaxTracksPerKind)
                    return null;
                existing = new RemoteTrack(TrackKind.Audio, streamId, trackId, mid, _logger);
                _audio[key] = existing;
                created = existing;
            }
            track = existing;
        }
        if (created is not null) _onTrackReceived(created);
        return track;
    }

    /// <summary>
    /// Materialises the video track for <paramref name="mid"/> (raising the callback once) without delivering a
    /// frame. A null MID keys the single legacy video track (the 1+1 path), so it stays one track.
    /// </summary>
    public RemoteTrack? EnsureVideoTrack(string? mid, string? streamId, string? trackId)
    {
        var key = mid ?? string.Empty;
        RemoteTrack? created = null;
        RemoteTrack track;
        lock (_sync)
        {
            if (!_video.TryGetValue(key, out var existing))
            {
                // #166 P1-4: beyond the cumulative cap a fresh MID is not retained (no track, no callback), so a
                // reoffer flood of new MIDs cannot grow retention without limit. Its frames are dropped.
                if (_video.Count >= MaxTracksPerKind)
                    return null;
                existing = new RemoteTrack(TrackKind.Video, streamId, trackId, mid, _logger);
                _video[key] = existing;
                created = existing;
            }
            track = existing;
        }
        if (created is not null) _onTrackReceived(created);
        return track;
    }

    /// <summary>
    /// Delivers one inbound audio frame on the <paramref name="mid"/> track (4.7.0: N remote audio tracks),
    /// materialising it first if not already present. A null MID targets the primary/anchor audio track (the
    /// mid-less path).
    /// </summary>
    public void DeliverAudioFrame(string? mid, string? streamId, string? trackId, EncodedFrame frame)
        => EnsureAudioTrack(mid, streamId, trackId)?.RaiseFrame(frame);

    /// <summary>
    /// Delivers one inbound video frame on the <paramref name="mid"/> track (P2c), materialising it first if not
    /// already present. A null MID targets the single legacy video track (the 1+1 path).
    /// </summary>
    public void DeliverVideoFrame(string? mid, string? streamId, string? trackId, EncodedFrame frame)
        => EnsureVideoTrack(mid, streamId, trackId)?.RaiseFrame(frame);
}
