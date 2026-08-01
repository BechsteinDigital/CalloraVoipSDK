using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Assembles the <see cref="SdpMediaOptions"/> a <see cref="WebRtcPeerConnection"/> offers/answers with,
/// extracted from the peer to keep it under the file-size limit. WebRTC is always BUNDLE + rtcp-mux
/// (RFC 8843 / RFC 8834); the DTLS identity and ICE credentials come from the peer's configuration.
/// <para>
/// Two paths, chosen by stable-numeric mode or whether any track was added at runtime:
/// </para>
/// <list type="bullet">
///   <item>1+1 (no AddAudioTrack/AddVideoTrack, at most the EnableVideo primary): the historic single-Video path
///   with the semantic mids <c>"audio"</c>/<c>"video"</c> — BYTE-IDENTICAL to the pre-P2c SDP, so existing 1+1
///   offers/answers and the SIP path are unchanged.</item>
///   <item>N (stable-numeric mode or ≥1 AddAudioTrack/AddVideoTrack): the numeric-MID multi-track path
///   (<see cref="SdpMediaOptions.Tracks"/>, RFC 8843). Compatibility mode retains the historic grouped order;
///   stable mode starts numeric and appends runtime tracks in API call order so renegotiation never changes an
///   existing m-line's index/MID (RFC 8829).</item>
/// </list>
/// </summary>
internal static class WebRtcSdpOptionsBuilder
{
    /// <summary>Builds the media options for the offer/answer at the bound local endpoint.</summary>
    /// <param name="local">The bound media endpoint (its port anchors every BUNDLE m-line).</param>
    /// <param name="options">The peer's configuration (audio/video codecs, DTLS, ICE).</param>
    /// <param name="addedAudio">The runtime-added additional audio tracks (4.7.0), each with its stable a=msid track id.</param>
    /// <param name="addedVideo">The runtime-added video tracks (P2c), each with its stable a=msid track id.</param>
    /// <param name="mediaStreamId">The peer's stable MediaStream id (RFC 8830).</param>
    /// <param name="audioTrackId">The peer's stable audio a=msid track id.</param>
    /// <param name="videoTrackId">The peer's stable primary-video a=msid track id.</param>
    public static SdpMediaOptions Build(
        IPEndPoint local,
        IReadOnlyList<IPEndPoint> hostEndPoints,
        WebRtcPeerOptions options,
        IReadOnlyList<(WebRtcAddedAudioTrack Track, string TrackId, int Order)> addedAudio,
        IReadOnlyList<(WebRtcAddedVideoTrack Track, string TrackId, int Order)> addedVideo,
        string mediaStreamId,
        string audioTrackId,
        string videoTrackId)
    {
        ArgumentNullException.ThrowIfNull(hostEndPoints);
        // Wildcard is a socket bind policy, not a candidate. The provider expands it into active-interface
        // addresses that all share this socket's real port (RFC 8445 §5.1.1.1).
        var candidates = new List<SdpIceCandidate>(options.Ice.Candidates.Count + hostEndPoints.Count);
        for (var index = 0; index < hostEndPoints.Count; index++)
            candidates.Add(WebRtcIceCandidateFactory.LocalHostCandidate(hostEndPoints[index], index));
        candidates.AddRange(options.Ice.Candidates);

        var ice = new SdpIceParameters
        {
            Ufrag = options.Ice.Ufrag,
            Pwd = options.Ice.Pwd,
            Options = options.Ice.Options,
            Candidates = candidates,
        };

        if (options.UseStableNumericMediaIds)
            return StableMultiTrack(local, ice, options, addedAudio, addedVideo, mediaStreamId, audioTrackId, videoTrackId);

        // Compatibility N-path: any track was added → numeric-MID multi-track offer. The added-audio m-lines follow the primary
        // audio and precede the videos, and the config primary video (if any) is the first video m-line, so the MIDs
        // match the AddAudioTrack/AddVideoTrack index arithmetic (audio 0, added-audio 1…A, primary video A+1, …).
        if (addedAudio.Count > 0 || addedVideo.Count > 0)
            return LegacyMultiTrack(local, ice, options, addedAudio, addedVideo, mediaStreamId, audioTrackId, videoTrackId);

        var primaryVideo = options.VideoTracks.Count > 0 ? options.VideoTracks[0] : null;
        return new SdpMediaOptions
        {
            Dtls = options.Dtls,
            Ice = ice,
            // All BUNDLE m-lines share the one bound transport port (the video m-line's own port is nominal).
            Video = primaryVideo is { } video
                ? new SdpVideoMediaOptions
                {
                    Port = local.Port,
                    Codecs = video.Codecs,
                    Crypto = video.Crypto,
                    Candidates = video.Candidates,
                    HeaderExtensionUris = video.HeaderExtensionUris,
                    SimulcastSendRids = video.SimulcastSendRids,
                }
                : null,
            AudioMsid = new SdpMsid { StreamId = mediaStreamId, TrackId = audioTrackId },
            VideoMsid = primaryVideo is not null
                ? new SdpMsid { StreamId = mediaStreamId, TrackId = videoTrackId }
                : null,
            Bundle = true,
            RtcpMux = true,
        };
    }

    // Builds the numeric-MID multi-track options (N-path): the primary audio track (MID 0), then each runtime-added
    // audio track in order, then the config-time EnableVideo primary video (if any), then each runtime-added video
    // track in order. The negotiator assigns numeric a=mid by list index, so this order MUST match the
    // AddAudioTrack/AddVideoTrack index arithmetic (audio 0, added-audio 1…A, primary video A+1, added video …).
    private static SdpMediaOptions LegacyMultiTrack(
        IPEndPoint local,
        SdpIceParameters ice,
        WebRtcPeerOptions options,
        IReadOnlyList<(WebRtcAddedAudioTrack Track, string TrackId, int Order)> addedAudio,
        IReadOnlyList<(WebRtcAddedVideoTrack Track, string TrackId, int Order)> addedVideo,
        string mediaStreamId,
        string audioTrackId,
        string videoTrackId)
    {
        var tracks = new List<SdpTrackOptions>(1 + addedAudio.Count + options.VideoTracks.Count + addedVideo.Count)
        {
            new()
            {
                Kind = "audio",
                Codecs = options.AudioCodecs,
                Direction = SdpMediaDirection.SendRecv,
                Msid = new SdpMsid { StreamId = mediaStreamId, TrackId = audioTrackId },
            },
        };

        // Each runtime-added AUDIO track sits immediately after the primary audio and before any video (RFC 8843):
        // its own direction, stable msid track id, and optional stream id (else the peer's default MediaStream), so a
        // receiver can group or separate the tracks (RFC 8830). Audio has no simulcast/header-extension/crypto seam here.
        foreach (var (track, trackId, _) in addedAudio)
            tracks.Add(new SdpTrackOptions
            {
                Kind = "audio",
                Codecs = track.Codecs,
                Direction = track.Direction,
                Msid = new SdpMsid { StreamId = track.StreamId ?? mediaStreamId, TrackId = trackId },
            });

        // The config-time primary video (EnableVideo) keeps its stable msid track id and shares the default stream.
        foreach (var video in options.VideoTracks)
            tracks.Add(new SdpTrackOptions
            {
                Kind = "video",
                Codecs = video.Codecs,
                Direction = SdpMediaDirection.SendRecv,
                Msid = new SdpMsid { StreamId = mediaStreamId, TrackId = videoTrackId },
                Crypto = video.Crypto,
                HeaderExtensionUris = video.HeaderExtensionUris,
                SimulcastSendRids = video.SimulcastSendRids,
            });

        // Each runtime-added VIDEO track: its own direction, stable msid track id, and optional stream id (else the
        // peer's default MediaStream), so a receiver can group or separate the tracks (RFC 8830).
        foreach (var (track, trackId, _) in addedVideo)
            tracks.Add(new SdpTrackOptions
            {
                Kind = "video",
                Codecs = track.Codecs,
                Direction = track.Direction,
                Msid = new SdpMsid { StreamId = track.StreamId ?? mediaStreamId, TrackId = trackId },
                SimulcastSendRids = track.SimulcastSendRids,
            });

        return new SdpMediaOptions
        {
            Dtls = options.Dtls,
            Ice = ice,
            Tracks = tracks,
            Bundle = true,
            RtcpMux = true,
        };
    }

    // Stable SFU path: primary audio/video occupy their numeric MIDs from the very first offer. Every runtime
    // track then appends in AddAudioTrack/AddVideoTrack call order. This is the W3C/RFC 8829 invariant a browser
    // enforces on a re-offer: existing m-lines may change direction but never move or receive a different MID.
    private static SdpMediaOptions StableMultiTrack(
        IPEndPoint local,
        SdpIceParameters ice,
        WebRtcPeerOptions options,
        IReadOnlyList<(WebRtcAddedAudioTrack Track, string TrackId, int Order)> addedAudio,
        IReadOnlyList<(WebRtcAddedVideoTrack Track, string TrackId, int Order)> addedVideo,
        string mediaStreamId,
        string audioTrackId,
        string videoTrackId)
    {
        var tracks = new List<SdpTrackOptions>(
            1 + options.VideoTracks.Count + addedAudio.Count + addedVideo.Count)
        {
            new()
            {
                Kind = "audio",
                Codecs = options.AudioCodecs,
                Direction = SdpMediaDirection.SendRecv,
                Msid = new SdpMsid { StreamId = mediaStreamId, TrackId = audioTrackId },
            },
        };

        foreach (var video in options.VideoTracks)
            tracks.Add(new SdpTrackOptions
            {
                Kind = "video",
                Codecs = video.Codecs,
                Direction = SdpMediaDirection.SendRecv,
                Msid = new SdpMsid { StreamId = mediaStreamId, TrackId = videoTrackId },
                Crypto = video.Crypto,
                HeaderExtensionUris = video.HeaderExtensionUris,
                SimulcastSendRids = video.SimulcastSendRids,
            });

        var appendedTracks = new List<(int Order, SdpTrackOptions Track)>(
            addedAudio.Count + addedVideo.Count);

        foreach (var (track, trackId, order) in addedAudio)
            appendedTracks.Add((order, new SdpTrackOptions
            {
                Kind = "audio",
                Codecs = track.Codecs,
                Direction = track.Direction,
                Msid = new SdpMsid { StreamId = track.StreamId ?? mediaStreamId, TrackId = trackId },
            }));

        foreach (var (track, trackId, order) in addedVideo)
            appendedTracks.Add((order, new SdpTrackOptions
            {
                Kind = "video",
                Codecs = track.Codecs,
                Direction = track.Direction,
                Msid = new SdpMsid { StreamId = track.StreamId ?? mediaStreamId, TrackId = trackId },
                SimulcastSendRids = track.SimulcastSendRids,
            }));

        tracks.AddRange(appendedTracks
            .OrderBy(entry => entry.Order)
            .Select(entry => entry.Track));

        return new SdpMediaOptions
        {
            Dtls = options.Dtls,
            Ice = ice,
            Tracks = tracks,
            Bundle = true,
            RtcpMux = true,
        };
    }
}
