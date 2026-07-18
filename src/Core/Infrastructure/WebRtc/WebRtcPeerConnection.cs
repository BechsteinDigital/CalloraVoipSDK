using System.Linq;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// A signalling-neutral WebRTC peer (the entry point of <c>CalloraVoipSdk.WebRtc</c>, ADR-010/founder
/// architecture): it consumes and produces SDP, mirroring the W3C <c>RTCPeerConnection</c>, so any
/// signalling transport (SIP-over-WebSocket, a custom channel, …) can carry the descriptions. It does
/// not touch the SIP call path.
///
/// This slice covers the signalling surface: applying a remote offer and producing a WebRTC answer
/// (BUNDLE per RFC 8843, DTLS-SRTP per RFC 5763, rtcp-mux per RFC 8834, and the MID SDES extension per
/// RFC 9143) via the existing SDP negotiator, plus the <see cref="WebRtcConnectionState"/> machine. The
/// media transport (the <c>BundledMediaSession</c> built from the negotiated description) and track
/// events attach in a later slice.
/// <para>
/// Threading contract (HARD-C6, interim): the signalling handshake — <see cref="CreateOffer"/>,
/// <see cref="SetRemoteDescriptionAsync"/>, <see cref="StartAsync"/> — is a single ordered sequence
/// and must be driven by one caller at a time, mirroring the W3C signalling-state serialisation; the
/// internal <c>_sync</c> gate protects the shared fields but does not make out-of-order concurrent
/// signalling meaningful. <see cref="DisposeAsync"/> is part of that same single-caller ordering: it must
/// not race an in-flight <see cref="SetRemoteDescriptionAsync"/>, which builds the media session and hands
/// the pre-bound socket over to it — disposing concurrently could tear down the peer between the bind and
/// the hand-over and orphan or double-dispose the socket. The media hot path (<see cref="SendAudioAsync"/>/<see cref="SendVideoFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>)
/// is hardened against a concurrent <see cref="DisposeAsync"/> (HARD-C6): each send holds a drain lease so
/// dispose waits for in-flight sends before tearing down the media session, and a send begun after
/// dispose throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </summary>
internal sealed class WebRtcPeerConnection : IAsyncDisposable
{
    private readonly WebRtcPeerOptions _options;
    private readonly ISdpOfferAnswerNegotiator _negotiator;
    private readonly ISdpSessionParser _parser;
    private readonly ISdpSessionSerializer _serializer;
    private readonly IDtlsSrtpHandshaker _handshaker;
    private readonly DtlsCertificate _certificate;
    private readonly IIceStunProbe? _stunProbe;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebRtcPeerConnection> _logger;
    private readonly object _sync = new();

    // Stable WebRTC track identity (a=msid, RFC 8830): one MediaStream carrying one audio and one
    // video track. Generated once per peer so re-offers keep the same stream/track ids.
    private readonly string _mediaStreamId = Guid.NewGuid().ToString("N");
    private readonly string _audioTrackId = Guid.NewGuid().ToString("N");
    private readonly string _videoTrackId = Guid.NewGuid().ToString("N");

    private WebRtcConnectionState _state = WebRtcConnectionState.New;
    private string? _remoteDescription;
    private SdpMsid? _remoteAudioMsid;
    private SdpMsid? _remoteVideoMsid;
    private bool _hasRemoteAudio;
    private bool _hasRemoteVideo;
    private string? _localDescription;
    private SdpSessionDescription? _localOfferModel;
    private BundledMediaSession? _session;
    private readonly SendDrainGate _sendGate = new();
    private UdpClient? _mediaSocket;
    private bool _socketHandedOver;
    private bool _started;
    // Trickle ICE (RFC 8838): the best remote candidate applied so far as the send target, buffered until
    // the session exists. Highest-priority-wins; the symmetric transport latches the peer's real source on
    // the first received packet regardless.
    private IPEndPoint? _pendingRemoteCandidate;
    private long _appliedRemotePriority = long.MinValue;

    /// <summary>Raised when the connection state changes (RFC 8829 <c>connectionstatechange</c>).</summary>
    public event Action<WebRtcConnectionState>? ConnectionStateChanged;

    /// <summary>Raised with each inbound audio RTP payload (the app owns the codec — transport-only).</summary>
    public event Action<byte[]>? AudioReceived;

    /// <summary>Raised with each reassembled inbound video frame (frame, RTP timestamp, is-key-frame).</summary>
    public event Action<byte[], uint, bool>? VideoFrameReceived;

    /// <summary>
    /// Raised as each local ICE candidate is gathered (RFC 8838 trickle), carrying the RFC 8829
    /// <c>candidate:</c> line so the app can signal it out-of-band. Today the peer gathers one host
    /// candidate (the early-bound media endpoint), emitted right after the offer/answer is produced;
    /// server-reflexive/relay gathering arrives in a later slice.
    /// </summary>
    public event Action<string>? LocalIceCandidateDiscovered;

    public WebRtcPeerConnection(
        WebRtcPeerOptions options,
        ISdpOfferAnswerNegotiator negotiator,
        ISdpSessionParser parser,
        ISdpSessionSerializer serializer,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory,
        IIceStunProbe? stunProbe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.LocalEndPoint);
        ArgumentNullException.ThrowIfNull(options.AudioCodecs);
        ArgumentNullException.ThrowIfNull(options.Dtls);
        ArgumentNullException.ThrowIfNull(options.Ice);
        _negotiator = negotiator ?? throw new ArgumentNullException(nameof(negotiator));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _handshaker = handshaker ?? throw new ArgumentNullException(nameof(handshaker));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _stunProbe = stunProbe;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<WebRtcPeerConnection>();
    }

    /// <summary>The current connection state.</summary>
    public WebRtcConnectionState State
    {
        get { lock (_sync) { return _state; } }
    }

    /// <summary>The applied remote SDP offer, or null before <see cref="SetRemoteDescriptionAsync"/>.</summary>
    public string? RemoteDescription
    {
        get { lock (_sync) { return _remoteDescription; } }
    }

    /// <summary>The generated local SDP answer, or null before <see cref="SetRemoteDescriptionAsync"/>.</summary>
    public string? LocalDescription
    {
        get { lock (_sync) { return _localDescription; } }
    }

    /// <summary>The bound local media endpoint of the shared transport, or null before a session is built.</summary>
    public IPEndPoint? LocalMediaEndPoint
    {
        get { lock (_sync) { return _session?.LocalEndPoint; } }
    }

    /// <summary>The selected remote media endpoint of the shared transport, or null before one is set.</summary>
    public IPEndPoint? RemoteMediaEndPoint
    {
        get { lock (_sync) { return _session?.RemoteEndPoint; } }
    }

    /// <summary>
    /// The remote peer's audio-track identity (a=msid, RFC 8830) from the applied remote description, or
    /// null before one is applied or when the remote advertised no audio msid. This is the remote stream's
    /// identity — what the W3C track model surfaces on the receiver, not this peer's own local msid.
    /// </summary>
    public SdpMsid? RemoteAudioMsid
    {
        get { lock (_sync) { return _remoteAudioMsid; } }
    }

    /// <summary>The remote peer's video-track identity (a=msid), or null. See <see cref="RemoteAudioMsid"/>.</summary>
    public SdpMsid? RemoteVideoMsid
    {
        get { lock (_sync) { return _remoteVideoMsid; } }
    }

    /// <summary>
    /// Whether the applied remote description contains an audio media line — i.e. the remote will send an
    /// audio track (independent of whether it carries an a=msid). Lets the receiver materialise the track
    /// from the description rather than waiting for the first frame.
    /// </summary>
    public bool HasRemoteAudio
    {
        get { lock (_sync) { return _hasRemoteAudio; } }
    }

    /// <summary>Whether the applied remote description contains a video media line. See <see cref="HasRemoteAudio"/>.</summary>
    public bool HasRemoteVideo
    {
        get { lock (_sync) { return _hasRemoteVideo; } }
    }

    /// <summary>Cumulative transport counters for the media session, or null before a session is built.</summary>
    public BundledMediaStats? GetStats()
    {
        lock (_sync) { return _session?.SnapshotStats(); }
    }

    /// <summary>
    /// Creates a local WebRTC offer (RFC 8829 createOffer + setLocalDescription): BUNDLE, DTLS-SRTP,
    /// rtcp-mux, and the sdes:mid extension. It becomes <see cref="LocalDescription"/>; apply the peer's
    /// answer with <see cref="SetRemoteDescriptionAsync"/> to establish media.
    /// </summary>
    public string CreateOffer()
    {
        var local = EnsureLocalMediaEndPoint();
        var offerModel = _negotiator.CreateOffer(
            local, _options.AudioCodecs, SdpMediaDirection.SendRecv, MediaOptions(local));
        var offerSdp = _serializer.Serialize(offerModel);
        lock (_sync)
        {
            _localOfferModel = offerModel;
            _localDescription = offerSdp;
        }

        RaiseLocalCandidate(local);
        return offerSdp;
    }

    /// <summary>
    /// Applies the peer's remote description and returns this peer's local description. As the answerer
    /// (no local offer created) the remote description is an offer: this negotiates and returns the
    /// WebRTC answer (RFC 8829 setRemoteDescription → createAnswer). As the offerer (after
    /// <see cref="CreateOffer"/>) the remote description is the answer: it is applied and the existing
    /// offer is returned. Either way the shared BUNDLE media transport is built from the two
    /// descriptions and the peer moves to <see cref="WebRtcConnectionState.Connecting"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The remote description is missing or not valid SDP.</exception>
    /// <exception cref="InvalidOperationException">As the answerer, no answer could be negotiated.</exception>
    public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteSdp);
        cancellationToken.ThrowIfCancellationRequested();

        SdpSessionDescription remote;
        try
        {
            remote = _parser.Parse(remoteSdp);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The remote description is not valid SDP.", nameof(remoteSdp), ex);
        }

        SdpSessionDescription pendingOffer;
        string? pendingLocalDescription;
        lock (_sync)
        {
            // Capture the offerer state as one snapshot: the local description belongs to _localOfferModel
            // and must be read under the same gate, not unsynchronised afterwards (HARD-C6).
            pendingOffer = _localOfferModel!;
            pendingLocalDescription = _localDescription;
        }

        SdpSessionDescription localModel;
        string localSdp;
        IPEndPoint? answererLocal = null;
        if (pendingOffer is not null)
        {
            // Offerer: the remote description is the answer; our offer is the local description.
            localModel = pendingOffer;
            localSdp = pendingLocalDescription!;
        }
        else
        {
            // Answerer: the remote description is the offer; negotiate our answer.
            var local = EnsureLocalMediaEndPoint();
            answererLocal = local;
            var result = _negotiator.NegotiateAnswer(
                remote, local, _options.AudioCodecs, SdpMediaDirection.SendRecv, MediaOptions(local));
            if (!result.Success || result.Answer is null)
            {
                TransitionTo(WebRtcConnectionState.Failed);
                throw new InvalidOperationException("Could not negotiate an answer for the remote description.");
            }

            localModel = result.Answer;
            localSdp = _serializer.Serialize(result.Answer);
        }

        // Build the shared media transport from the two descriptions (WebRTC is DTLS-SRTP over one
        // BUNDLE group). A non-bundle exchange yields no session — the local description is still
        // returned, but the peer has no transport (logged), which StartAsync then surfaces. The offerer
        // (a local offer was created) holds the ICE controlling role (RFC 8445 §6.1.1).
        var session = WebRtcSessionFactory.TryCreate(
            remote, localModel, _options, _handshaker, _certificate, _loggerFactory, _mediaSocket,
            iceControlling: pendingOffer is not null);
        if (session is null)
            _logger.LogWarning("The remote description did not negotiate a BUNDLE media session; no transport was built.");

        IPEndPoint? pendingCandidate;
        lock (_sync)
        {
            _remoteDescription = remoteSdp;
            _localDescription = localSdp;
            _session = session;
            // The transport now owns the pre-bound socket (if a session was built); DisposeAsync must not
            // dispose it again.
            _socketHandedOver = session is not null;
            // Capture any candidate that trickled in before the session existed under the SAME lock that
            // publishes _session, so a concurrent AddIceCandidateAsync either wrote it before this (picked
            // up here) or observes the published session and applies it live — never lost in a gap (RFC 8838).
            pendingCandidate = _pendingRemoteCandidate;
            // Retain the remote track identity (a=msid) so the receiver can group inbound tracks by the
            // remote MediaStream (the W3C RTCTrackEvent.streams semantics).
            var audioMedia = remote.Media.FirstOrDefault(m => string.Equals(m.MediaType, "audio", StringComparison.OrdinalIgnoreCase));
            var videoMedia = remote.Media.FirstOrDefault(m => string.Equals(m.MediaType, "video", StringComparison.OrdinalIgnoreCase));
            _hasRemoteAudio = audioMedia is not null;
            _hasRemoteVideo = videoMedia is not null;
            _remoteAudioMsid = audioMedia?.Msid;
            _remoteVideoMsid = videoMedia?.Msid;
        }

        // Publish _session before wiring its event handlers, so a state-transition callback can never
        // fire against a peer that has not yet recorded the session it belongs to (HARD-C6).
        if (session is not null)
        {
            WireSession(session);
            if (pendingCandidate is not null)
                session.SetRemoteEndPoint(pendingCandidate);
        }

        TransitionTo(WebRtcConnectionState.Connecting);
        if (answererLocal is not null)
            RaiseLocalCandidate(answererLocal);
        return Task.FromResult(localSdp);
    }

    /// <summary>
    /// Starts the shared transport: the receive loop, the ICE consent loop, and the DTLS handshake.
    /// The connection reaches <see cref="WebRtcConnectionState.Connected"/> once the handshake installs
    /// the SRTP keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session was built (no remote description, or a non-bundle one).</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        BundledMediaSession? session;
        lock (_sync)
        {
            session = _session;
            // The transport's receive loop now owns the media socket — candidate gathering (which shares
            // that socket) must not run after this point.
            _started = true;
        }
        if (session is null)
            throw new InvalidOperationException("Apply a BUNDLE remote description before starting the peer.");

        return session.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Sends one audio RTP payload on the peer's audio track (suppressed until the handshake keys the
    /// transport). The payload is an already-encoded RTP payload — the app owns the codec.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session was built.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var session = AcquireSendLease();
        try
        {
            await session.SendAudioAsync(payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Exit();
        }
    }

    /// <summary>
    /// Packetises and sends one encoded video frame on the peer's video track.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public async Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
    {
        var session = AcquireSendLease();
        try
        {
            await session.SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Exit();
        }
    }

    /// <summary>
    /// Packetises and sends one encoded video frame on a simulcast <paramref name="rid"/> layer (RFC 8853).
    /// The layer must have been offered via the peer's configured simulcast rids.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public async Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
    {
        var session = AcquireSendLease();
        try
        {
            await session.SendVideoFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Exit();
        }
    }

    /// <summary>
    /// Applies a remote ICE candidate that trickled in out-of-band (RFC 8838), given as an RFC 8829
    /// <c>candidate:</c> line. The highest-priority component-1 UDP candidate becomes the transport's send
    /// target (buffered until the session is built); the symmetric transport still latches the peer's real
    /// source address on the first received packet. A malformed or unusable candidate is ignored. This is
    /// candidate application, not full RFC 8445 multi-pair connectivity checking (a later slice).
    /// </summary>
    public Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (ParseTrickleCandidate(candidate) is not { } parsed)
        {
            _logger.LogDebug("Ignoring an unusable trickled ICE candidate.");
            return Task.CompletedTask;
        }

        BundledMediaSession? session;
        lock (_sync)
        {
            if (parsed.Priority <= _appliedRemotePriority)
                return Task.CompletedTask; // keep the higher-priority target already applied
            _appliedRemotePriority = parsed.Priority;
            _pendingRemoteCandidate = parsed.Endpoint;
            session = _session;
        }

        session?.SetRemoteEndPoint(parsed.Endpoint);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gathers server-reflexive ICE candidates (RFC 8445 §5.1.1) by querying each configured STUN server
    /// through the pre-bound media socket, emitting every discovered candidate on
    /// <see cref="LocalIceCandidateDiscovered"/> (RFC 8838 trickle). No-op without a STUN probe or servers.
    /// Call after the offer/answer is produced and BEFORE <see cref="StartAsync"/> — the query shares the
    /// media socket, which the transport's receive loop takes over once started.
    /// </summary>
    public async Task GatherCandidatesAsync(CancellationToken cancellationToken = default)
    {
        if (_stunProbe is null || _options.IceServers.Count == 0)
            return;

        var local = EnsureLocalMediaEndPoint();
        Socket socket;
        lock (_sync)
        {
            if (_started)
                throw new InvalidOperationException(
                    "Cannot gather ICE candidates after StartAsync — the media socket is owned by the transport's receive loop.");
            socket = _mediaSocket!.Client;
        }

        foreach (var server in _options.IceServers)
        {
            if (server.Type != IceServerType.Stun)
                continue;

            var reflexive = await _stunProbe
                .TryGetServerReflexiveEndPointAsync(local, server, socket, cancellationToken)
                .ConfigureAwait(false);
            if (reflexive is not null)
                RaiseCandidate(ServerReflexiveCandidate(reflexive, local));
        }
    }

    // Parses an RFC 8829 candidate string ("candidate:…", tolerating a leading "a=") into a component-1
    // UDP endpoint and its priority, or null when malformed/unusable (wrong component/transport, no port,
    // unparseable address).
    private static (IPEndPoint Endpoint, long Priority)? ParseTrickleCandidate(string candidate)
    {
        var value = candidate.Trim();
        if (value.StartsWith("a=", StringComparison.Ordinal))
            value = value[2..];
        if (value.StartsWith("candidate:", StringComparison.Ordinal))
            value = value["candidate:".Length..];

        if (SdpIceCandidate.TryParse(value) is not { } parsed
            || parsed.Component != 1
            || !parsed.Transport.Equals("udp", StringComparison.OrdinalIgnoreCase)
            || parsed.Port <= 0
            || parsed.Priority < 0 // RFC 8445 priority is a 31-bit unsigned; a negative value is malformed
            || !IPAddress.TryParse(parsed.Address, out var ip))
            return null;

        return (new IPEndPoint(ip, parsed.Port), parsed.Priority);
    }

    // Emits the local host candidate for the bound endpoint as an RFC 8829 candidate string (trickle).
    private void RaiseLocalCandidate(IPEndPoint local) => RaiseCandidate(LocalHostCandidate(local));

    // Emits a gathered local candidate as an RFC 8829 candidate string on the trickle event.
    private void RaiseCandidate(SdpIceCandidate candidate)
    {
        if (LocalIceCandidateDiscovered is not { } handler)
            return;

        var line = "candidate:" + candidate.Serialize();
        try
        {
            handler(line);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in WebRTC LocalIceCandidateDiscovered handler.");
        }
    }

    // Takes a drain lease for one send and returns the live session. The lease keeps DisposeAsync from
    // disposing the session until the send's Exit; a send begun after dispose is refused. Callers MUST
    // Exit the gate (the send methods do so in a finally) once the returned session is no longer used.
    private BundledMediaSession AcquireSendLease()
    {
        if (!_sendGate.TryEnter())
            throw new ObjectDisposedException(nameof(WebRtcPeerConnection));

        BundledMediaSession? session;
        lock (_sync) { session = _session; }
        if (session is null)
        {
            _sendGate.Exit();
            throw new InvalidOperationException("Apply a BUNDLE remote description before exchanging media.");
        }

        return session;
    }

    // Maps the transport's lifecycle onto the WebRTC connection state (RFC 8829): keys installed →
    // Connected, handshake failure or consent loss → Failed, a transient consent miss → Disconnected.
    // Inbound media is surfaced as the peer's own track events.
    private void WireSession(BundledMediaSession session)
    {
        session.Connected += () => TransitionTo(WebRtcConnectionState.Connected);
        session.HandshakeFailed += () => TransitionTo(WebRtcConnectionState.Failed);
        session.MediaConsentLost += () => TransitionTo(WebRtcConnectionState.Failed);
        session.MediaConnectivityDegraded += () => TransitionTo(WebRtcConnectionState.Disconnected);
        session.MediaConnectivityRecovered += () => TransitionTo(WebRtcConnectionState.Connected);
        session.AudioReceived += packet => AudioReceived?.Invoke(packet.Payload.ToArray());
        session.VideoFrameReceived += (frame, timestamp, isKeyFrame) => VideoFrameReceived?.Invoke(frame, timestamp, isKeyFrame);
    }

    // WebRTC is always BUNDLE + rtcp-mux (RFC 8843 / RFC 8834); the DTLS identity and ICE credentials
    // come from the local configuration. Used for both the offer and the answer.
    private SdpMediaOptions MediaOptions(IPEndPoint local) => new()
    {
        Dtls = _options.Dtls,
        Ice = new SdpIceParameters
        {
            Ufrag = _options.Ice.Ufrag,
            Pwd = _options.Ice.Pwd,
            Options = _options.Ice.Options,
            // Advertise our bound media address as a host candidate (RFC 8839) so the peer can reach us.
            // Early-bind gives us the real ephemeral port before the session exists, so a host candidate is
            // always emitted (no more zero-port disabled offer).
            Candidates = [LocalHostCandidate(local), .. _options.Ice.Candidates],
        },
        // All BUNDLE m-lines share the one bound transport port (the video m-line's own port is nominal).
        Video = _options.Video is { } video
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
        AudioMsid = new SdpMsid { StreamId = _mediaStreamId, TrackId = _audioTrackId },
        VideoMsid = _options.Video is not null
            ? new SdpMsid { StreamId = _mediaStreamId, TrackId = _videoTrackId }
            : null,
        Bundle = true,
        RtcpMux = true,
    };

    // Binds the shared media socket up front (Trickle-ICE early-bind) so the offer/answer advertise the real
    // ephemeral port and a host candidate before the session (transport) exists — fixing the zero-port
    // disabled offer. The transport takes ownership at session build; if the peer is disposed before that,
    // DisposeAsync disposes the socket.
    private IPEndPoint EnsureLocalMediaEndPoint()
    {
        lock (_sync)
        {
            if (_mediaSocket is null)
            {
                var socket = new UdpClient(AddressFamily.InterNetwork);
                socket.Client.ReceiveBufferSize = 8192;
                socket.Client.Bind(_options.LocalEndPoint);
                _mediaSocket = socket;
            }

            return (IPEndPoint)_mediaSocket.Client.LocalEndPoint!;
        }
    }

    // A host ICE candidate for the bound local media endpoint (RFC 8445 §5.1.2.1 priority: host type-pref
    // 126, local-pref 65535, RTP component 1). rtcp-mux shares component 1, so no RTCP candidate is needed.
    private static SdpIceCandidate LocalHostCandidate(IPEndPoint local) => new()
    {
        Foundation = "1",
        Component = 1,
        Transport = "udp",
        Priority = (126L << 24) | (65535L << 8) | 255L,
        Address = local.Address.ToString(),
        Port = local.Port,
        Type = "host",
    };

    // A server-reflexive candidate for the STUN-discovered public endpoint (RFC 8445 §5.1.2.1 priority:
    // srflx type-pref 100, local-pref 65535, RTP component 1). raddr/rport carry the local base (host).
    private static SdpIceCandidate ServerReflexiveCandidate(IPEndPoint reflexive, IPEndPoint host) => new()
    {
        Foundation = "2",
        Component = 1,
        Transport = "udp",
        Priority = (100L << 24) | (65535L << 8) | 255L,
        Address = reflexive.Address.ToString(),
        Port = reflexive.Port,
        Type = "srflx",
        RelatedAddress = host.Address.ToString(),
        RelatedPort = host.Port,
    };

    private void TransitionTo(WebRtcConnectionState next)
    {
        lock (_sync)
        {
            if (_state == next || _state == WebRtcConnectionState.Closed)
                return;
            _state = next;
        }

        try
        {
            ConnectionStateChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in WebRTC ConnectionStateChanged handler.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        BundledMediaSession? session;
        UdpClient? orphanSocket;
        lock (_sync)
        {
            session = _session;
            _session = null;
            // If the early-bound socket was never handed to a transport, this peer still owns it and must
            // dispose it; once handed over, the session/transport owns it. Null it out so a second dispose
            // never double-disposes.
            orphanSocket = _socketHandedOver ? null : _mediaSocket;
            _mediaSocket = null;
        }

        TransitionTo(WebRtcConnectionState.Closed);

        // Refuse new sends and wait for in-flight ones to finish before tearing down the session, so a
        // concurrent send never operates on a disposed media session (HARD-C6). Idempotent: a second
        // dispose sees a null session and an already-drained gate. Drain completion is bounded by the
        // in-flight sends: a send that never completes (unbounded blocking, an un-cancelled token) keeps
        // dispose waiting — callers wanting a bounded teardown must cancel pending sends first.
        await _sendGate.BeginDrainAsync().ConfigureAwait(false);
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        orphanSocket?.Dispose();
    }
}
