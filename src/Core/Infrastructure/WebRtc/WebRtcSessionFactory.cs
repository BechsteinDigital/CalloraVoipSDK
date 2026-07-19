using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
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
    /// <param name="relayIceBindingFactory">
    /// Builds the relay ICE binding once the shared socket exists, when a TURN allocation was gathered on it
    /// (the offerer path); <see langword="null"/> leaves the session direct-only.
    /// </param>
    public static BundledMediaSession? TryCreate(
        SdpSessionDescription remoteDescription,
        SdpSessionDescription localDescription,
        WebRtcPeerOptions options,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory,
        UdpClient? preBoundSocket = null,
        bool iceControlling = false,
        RelayIceBindingFactory? relayIceBindingFactory = null)
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

        // Outbound audio follows the negotiated direction (RFC 3264): this peer sends AND the remote receives.
        // Unlike video, audio is not dropped when it is not sent — the audio m-line anchors the bundle transport
        // (ICE/DTLS) and inbound audio is still received — so the session suppresses outbound audio instead.
        var audioSendEnabled = LocalSends(localAudio) && RemoteReceives(remoteAudio);
        if (!audioSendEnabled)
            loggerFactory.CreateLogger(typeof(WebRtcSessionFactory)).LogInformation(
                "Audio is negotiated but not for sending from this peer (local direction {LocalDirection}; remote " +
                "direction {RemoteDirection}); outbound audio is suppressed (the audio m-line still anchors the " +
                "bundle transport and inbound audio is still received).", localAudio.Direction, remoteAudio.Direction);

        var videoTrack = TryBuildVideoTrack(localDescription, remoteDescription, audioSsrc, loggerFactory);

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
            AudioSendEnabled = audioSendEnabled,
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
            // Present only when a TURN allocation was gathered on this socket (the offerer path, where gathering
            // precedes applying the answer). The relay ICE local candidate is then checked alongside the direct
            // one; absent, the session is direct-only.
            RelayIceBindingFactory = relayIceBindingFactory,
        };

        return new BundledMediaSession(sessionOptions, handshaker, certificate, loggerFactory);
    }

    private static BundledTrackConfig? TryBuildVideoTrack(
        SdpSessionDescription localDescription,
        SdpSessionDescription remoteDescription,
        uint audioSsrc,
        ILoggerFactory loggerFactory)
    {
        // Gated by the MID (grouped into the bundle), not the placeholder port under ICE.
        var video = localDescription.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci));
        if (video is null || string.IsNullOrEmpty(video.Mid))
            return null;

        // Build an outbound video track only when both sides negotiated it for sending (RFC 8829/3264):
        // this peer sends (send-recv/send-only) AND the remote will receive (enabled, send-recv/recv-only).
        // A remote that rejected video (port 0), made it inactive, or is send-only must not be streamed to —
        // the outbound mirror of the inbound port/direction gate. The local m-line's port is an ICE
        // placeholder, so the local side is gated by direction, not a zero port.
        var remoteVideo = remoteDescription.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci));
        if (!LocalSends(video) || !RemoteReceives(remoteVideo))
        {
            loggerFactory.CreateLogger(typeof(WebRtcSessionFactory)).LogInformation(
                "Video is present locally but not negotiated for sending (local direction {LocalDirection}; " +
                "remote {RemoteState}); no outbound video track is built.",
                video.Direction,
                remoteVideo is null ? "absent" : remoteVideo.Disabled ? "disabled" : remoteVideo.Direction.ToString());
            return null;
        }

        // The primary video codec (skip the RTX repair codec, RFC 4588).
        var codec = video.Codecs.FirstOrDefault(c => !c.Name.Equals("rtx", Ci));
        if (codec is null)
            return null;

        // Send-side simulcast (RFC 8853) only activates for the layers the remote ANSWER confirmed as recv
        // (and only if it echoed the RID header extension, RFC 8852) — never for our offered layers alone.
        // Stamping RIDs the peer cannot demux would break its reception; an unconfirmed offer falls back to a
        // single stream.
        var sendRids = video.Rids.Where(r => r.Direction.Equals("send", Ci)).Select(r => r.Id).ToArray();
        if (sendRids.Length > 0)
        {
            var confirmedRids = ConfirmedSimulcastRids(sendRids, remoteDescription);
            if (confirmedRids.Count > 0)
            {
                var used = new HashSet<uint> { audioSsrc };
                var encodings = new List<BundledVideoEncoding>(confirmedRids.Count);
                foreach (var rid in confirmedRids)
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

            loggerFactory.CreateLogger(typeof(WebRtcSessionFactory)).LogInformation(
                "Simulcast was offered (a=rid send {Layers}) but the remote answer confirmed no matching recv " +
                "RIDs with the RID header extension (RFC 8852/8853); falling back to a single video stream.",
                string.Join(",", sendRids));
        }

        return new BundledTrackConfig
        {
            Mid = video.Mid,
            Ssrc = NewSsrc(distinctFrom: audioSsrc),
            PayloadType = (byte)codec.PayloadType,
            VideoCodecName = codec.Name,
        };
    }

    // The subset of our offered send RIDs the remote answer confirmed as recv (RFC 8853): it must echo the
    // RID header extension (RFC 8852) — else it cannot demux the layers — and list the RID as recv via
    // a=simulcast:recv and/or an a=rid … recv line. Our offered order is preserved.
    //
    // Role assumption: this confirmation is only meaningful for the OFFERER, where localSendRids come from our
    // offer and remoteDescription is the peer's answer. The answerer's local description carries no a=rid send
    // today (answerer-side simulcast is a separate follow-up), so this is never reached on the answerer path.
    //
    // Limitation (RFC 8853 §5.1): comma-separated alternatives within one simulcast stream (e.g.
    // "recv hi,mid") are matched verbatim, so an alternative token never equals a bare send RID and that layer
    // is treated as unconfirmed — a safe conservative fallback (we never stamp a RID the peer did not confirm).
    // Splitting alternatives is deferred (their either/or semantics must not be flattened into "both").
    private static IReadOnlyList<string> ConfirmedSimulcastRids(
        IReadOnlyList<string> localSendRids, SdpSessionDescription remoteDescription)
    {
        var remoteVideo = remoteDescription.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci));
        if (remoteVideo is null || RidExtensionId(remoteVideo) is null)
            return [];

        var remoteRecv = new HashSet<string>(StringComparer.Ordinal);
        if (remoteVideo.Simulcast?.Recv is { } recvList)
            foreach (var id in recvList)
                remoteRecv.Add(id);
        foreach (var rid in remoteVideo.Rids.Where(r => r.Direction.Equals("recv", Ci)))
            remoteRecv.Add(rid.Id);

        return localSendRids.Where(remoteRecv.Contains).ToArray();
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

    // This peer will send media on a section it authored: direction send-recv or send-only (RFC 3264;
    // the default when no direction attribute is present is send-recv). The local m-line's port is an ICE
    // placeholder, so the local side is gated by direction, not a zero port.
    private static bool LocalSends(SdpMediaDescription media)
        => media.Direction is SdpMediaDirection.SendRecv or SdpMediaDirection.SendOnly;

    // The remote party will receive media on a section it authored: enabled (real port) and direction
    // send-recv or recv-only (RFC 3264). The outbound counterpart to the inbound RemoteSends gate.
    private static bool RemoteReceives(SdpMediaDescription? media)
        => media is { Disabled: false, Direction: SdpMediaDirection.SendRecv or SdpMediaDirection.RecvOnly };

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
