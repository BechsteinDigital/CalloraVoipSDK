using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Builds the shared media transport (<see cref="BundledMediaSession"/>) for a WebRTC peer from the
/// negotiated descriptions (Weg 1 / ADR-011 B5). A WebRTC peer holds all the facts itself — its own
/// description plus the peer's — so it derives the transport options directly (endpoints, DTLS role and
/// remote fingerprint, ICE credentials, payload types, and the BUNDLE MID facts) without the SIP call
/// path's CallMediaParameters/enricher machinery, keeping the WebRTC path off the SIP module. It is
/// role-neutral: <paramref name="localDescription"/> is this peer's description (offer or answer) and
/// <paramref name="remoteDescription"/> is the peer's, so it serves both the offerer and the answerer.
/// </summary>
internal static class WebRtcSessionFactory
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Creates the bundle session for a negotiated WebRTC exchange, or returns <see langword="null"/>
    /// when the descriptions are not a keyed BUNDLE session (no audio, no sdes:mid, or no DTLS
    /// fingerprint) — WebRTC media is DTLS-SRTP over one BUNDLE group.
    /// </summary>
    // Host-candidate priority (RFC 8445 §5.1.2.1: type pref 126, local pref 65535, component 1). Used only
    // as the ordering weight for a remote endpoint taken from the m-line address (no a=candidate priority).
    private const long DefaultCandidatePriority = (126L << 24) | (65535L << 8) | 255L;

    /// <param name="localDescription">This peer's description (the offer if offering, else the answer).</param>
    /// <param name="remoteDescription">The other peer's description (the answer if we offered, else the offer).</param>
    /// <param name="iceControlling">
    /// Whether this peer holds the ICE controlling role (RFC 8445 §6.1.1) — the offerer. The controlling
    /// agent drives connectivity checks and USE-CANDIDATE nomination; the controlled agent (answerer) adopts
    /// the nominated pair from the peer's check.
    /// </param>
    public static BundledMediaSession? TryCreate(
        SdpSessionDescription remoteDescription,
        SdpSessionDescription localDescription,
        WebRtcPeerOptions options,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory,
        UdpClient? preBoundSocket = null,
        bool iceControlling = false)
    {
        ArgumentNullException.ThrowIfNull(remoteDescription);
        ArgumentNullException.ThrowIfNull(localDescription);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handshaker);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var remoteAudio = remoteDescription.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci) && !m.Disabled);
        // The local m-line's port is a placeholder under ICE (the real address comes via candidates), so
        // the local side is gated by its MID below, not by a zero port.
        var localAudio = localDescription.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci));
        if (remoteAudio is null || localAudio is null)
            return null;

        // BUNDLE facts from our own description: the shared sdes:mid id and the audio m-line's MID.
        var midExtensionId = MidExtensionId(localAudio);
        if (midExtensionId is null || string.IsNullOrEmpty(localAudio.Mid))
            return null;

        // Remote media address: the best ICE candidate (RFC 8839), or the m-line address/port.
        var remoteEndPoint = WebRtcRemoteEndPoint.Resolve(remoteAudio, remoteDescription.ConnectionAddress);
        if (remoteEndPoint is null)
            return null;

        // The remote candidates the controlling agent runs connectivity checks against (RFC 8445 §7.2.2/§8).
        // Falls back to the resolved endpoint when the peer advertised no a=candidate (m-line address style),
        // so nomination still validates the one reachable pair.
        var remoteCandidates = RemoteCandidates(remoteAudio);
        if (remoteCandidates.Count == 0)
            remoteCandidates = [new IceRemoteCandidate(remoteEndPoint, DefaultCandidatePriority)];

        // DTLS-SRTP (WebRTC is DTLS only): the peer's fingerprint, and our role from both a=setup values
        // (RFC 5763 §5 / RFC 4145: the active side is the client).
        var fingerprint = remoteAudio.Fingerprint ?? remoteDescription.Fingerprint;
        if (fingerprint is null)
            return null;
        var dtlsIsClient = ResolveIsClient(localAudio.DtlsSetup, remoteAudio.DtlsSetup);

        var audioCodec = localAudio.Codecs.FirstOrDefault(c => !c.Name.Equals("telephone-event", Ci));
        if (audioCodec is null)
            return null;
        var clockRate = audioCodec.ClockRate > 0 ? audioCodec.ClockRate : 8000;

        var audioSsrc = NewSsrc();
        var audioTrack = new BundledTrackConfig
        {
            Mid = localAudio.Mid,
            Ssrc = audioSsrc,
            PayloadType = (byte)audioCodec.PayloadType,
            SamplesPerPacket = clockRate * 20 / 1000, // 20 ms frames
        };

        var videoTrack = TryBuildVideoTrack(localDescription, audioSsrc);

        // A simulcast video track (RFC 8853) needs the negotiated RID header-extension id (RFC 8852) to
        // stamp each layer's RID on outbound packets; without it in our own description we cannot key the
        // encodings, so the exchange is not a usable simulcast session.
        byte? ridExtensionId = null;
        if (videoTrack is { Encodings.Count: > 0 })
        {
            var localVideo = localDescription.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci));
            ridExtensionId = RidExtensionId(localVideo);
            if (ridExtensionId is null)
            {
                loggerFactory.CreateLogger(typeof(WebRtcSessionFactory)).LogWarning(
                    "Simulcast video is configured but the local description carries no RID header-extension id " +
                    "(a=extmap … sdes:rtp-stream-id); cannot key the simulcast encodings — no session built.");
                return null;
            }
        }

        var sessionOptions = new BundledMediaSessionOptions
        {
            LocalEndPoint = options.LocalEndPoint,
            PreBoundSocket = preBoundSocket,
            RemoteEndPoint = remoteEndPoint,
            MidExtensionId = midExtensionId.Value,
            RidExtensionId = ridExtensionId,
            Audio = audioTrack,
            Video = videoTrack,
            DtlsIsClient = dtlsIsClient,
            RemoteFingerprint = new DtlsFingerprint { Algorithm = fingerprint.Algorithm, Value = fingerprint.Value },
            Ice = new IceMediaParameters(
                remoteEndPoint,
                IceEnabled: true,
                IceControlling: iceControlling,
                LocalIceUfrag: options.Ice.Ufrag,
                LocalIcePwd: options.Ice.Pwd,
                RemoteIceUfrag: remoteAudio.IceUfrag,
                RemoteIcePwd: remoteAudio.IcePwd)
            {
                RemoteCandidates = remoteCandidates,
            },
        };

        return new BundledMediaSession(sessionOptions, handshaker, certificate, loggerFactory);
    }

    private static BundledTrackConfig? TryBuildVideoTrack(SdpSessionDescription localDescription, uint audioSsrc)
    {
        // Gated by the MID (grouped into the bundle), not the placeholder port under ICE.
        var video = localDescription.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci));
        if (video is null || string.IsNullOrEmpty(video.Mid))
            return null;

        // The primary video codec (skip the RTX repair codec, RFC 4588).
        var codec = video.Codecs.FirstOrDefault(c => !c.Name.Equals("rtx", Ci));
        if (codec is null)
            return null;

        // Send-side simulcast (RFC 8853): one encoding per a=rid … send, each on its own SSRC distinct
        // from audio and every other layer.
        var sendRids = video.Rids.Where(r => r.Direction.Equals("send", Ci)).Select(r => r.Id).ToArray();
        if (sendRids.Length > 0)
        {
            var used = new HashSet<uint> { audioSsrc };
            var encodings = new List<BundledVideoEncoding>(sendRids.Length);
            foreach (var rid in sendRids)
            {
                var ssrc = NewSsrc(used);
                used.Add(ssrc);
                encodings.Add(new BundledVideoEncoding { Rid = rid, Ssrc = ssrc });
            }

            return new BundledTrackConfig
            {
                Mid = video.Mid,
                Ssrc = encodings[0].Ssrc, // primary; per-layer SSRCs carry the actual sends
                PayloadType = (byte)codec.PayloadType,
                VideoCodecName = codec.Name,
                Encodings = encodings,
            };
        }

        return new BundledTrackConfig
        {
            Mid = video.Mid,
            Ssrc = NewSsrc(distinctFrom: audioSsrc),
            PayloadType = (byte)codec.PayloadType,
            VideoCodecName = codec.Name,
        };
    }

    // The negotiated RID header-extension id (RFC 8852) on a media section, or null when absent — required
    // to stamp a simulcast layer's RID on outbound packets. Mirrors <see cref="MidExtensionId"/>.
    private static byte? RidExtensionId(SdpMediaDescription? media)
    {
        var rid = media?.Extensions.FirstOrDefault(e => string.Equals(e.Uri, RtpHeaderExtensionUris.Rid, StringComparison.Ordinal));
        return rid is not null && rid.Id is >= 1 and <= 14 ? (byte)rid.Id : null;
    }

    // Local DTLS role from both a=setup values (RFC 4145 §4 / RFC 5763 §5): a concrete local role wins;
    // an offerer's actpass defers to the peer's answer (peer active → we are server, peer passive → we
    // are client). Defaults to the client role when both are silent (WebRTC answerer default).
    private static bool ResolveIsClient(string? localSetup, string? remoteSetup)
    {
        if (string.Equals(localSetup, "active", Ci)) return true;
        if (string.Equals(localSetup, "passive", Ci)) return false;
        if (string.Equals(remoteSetup, "active", Ci)) return false;
        if (string.Equals(remoteSetup, "passive", Ci)) return true;
        return true;
    }

    private static byte? MidExtensionId(SdpMediaDescription media)
    {
        var mid = media.Extensions.FirstOrDefault(e => string.Equals(e.Uri, RtpHeaderExtensionUris.Mid, StringComparison.Ordinal));
        return mid is not null && mid.Id is >= 1 and <= 14 ? (byte)mid.Id : null;
    }

    // The usable remote candidates for connectivity checks (RFC 8839): component-1 UDP candidates with a
    // parseable address and real port, paired with their priority so the nomination driver checks the
    // highest-priority pair first (RFC 8445 §7.2.2).
    private static IReadOnlyList<IceRemoteCandidate> RemoteCandidates(SdpMediaDescription remoteAudio) =>
        remoteAudio.Candidates
            .Where(c => c.Component == 1 // RTP; rtcp-mux shares it (RFC 8843)
                        && c.Transport.Equals("udp", Ci)
                        && c.Port > 0
                        && c.Priority >= 0
                        && IPAddress.TryParse(c.Address, out _))
            .Select(c => new IceRemoteCandidate(new IPEndPoint(IPAddress.Parse(c.Address), c.Port), c.Priority))
            .ToArray();

    // RFC 3550 §5.1: a random 32-bit SSRC. NextInt64 covers the full [1, 2^32-1] range (Next(1, int.MaxValue)
    // would leave the upper half of the SSRC space unused).
    private static uint NewSsrc(uint? distinctFrom = null)
    {
        uint ssrc;
        do
        {
            ssrc = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1);
        }
        while (ssrc == distinctFrom);
        return ssrc;
    }

    private static uint NewSsrc(ISet<uint> distinctFrom)
    {
        uint ssrc;
        do
        {
            ssrc = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1);
        }
        while (distinctFrom.Contains(ssrc));
        return ssrc;
    }
}
