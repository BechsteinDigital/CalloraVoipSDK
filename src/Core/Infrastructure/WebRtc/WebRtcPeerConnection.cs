using System.Net;
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
/// </summary>
internal sealed class WebRtcPeerConnection : IAsyncDisposable
{
    private readonly WebRtcPeerOptions _options;
    private readonly ISdpOfferAnswerNegotiator _negotiator;
    private readonly ISdpSessionParser _parser;
    private readonly ISdpSessionSerializer _serializer;
    private readonly IDtlsSrtpHandshaker _handshaker;
    private readonly DtlsCertificate _certificate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebRtcPeerConnection> _logger;
    private readonly object _sync = new();

    private WebRtcConnectionState _state = WebRtcConnectionState.New;
    private string? _remoteDescription;
    private string? _localDescription;
    private BundledMediaSession? _session;

    /// <summary>Raised when the connection state changes (RFC 8829 <c>connectionstatechange</c>).</summary>
    public event Action<WebRtcConnectionState>? ConnectionStateChanged;

    /// <summary>Raised with each inbound audio RTP payload (the app owns the codec — transport-only).</summary>
    public event Action<byte[]>? AudioReceived;

    /// <summary>Raised with each reassembled inbound video frame (frame, RTP timestamp, is-key-frame).</summary>
    public event Action<byte[], uint, bool>? VideoFrameReceived;

    public WebRtcPeerConnection(
        WebRtcPeerOptions options,
        ISdpOfferAnswerNegotiator negotiator,
        ISdpSessionParser parser,
        ISdpSessionSerializer serializer,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory)
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

    /// <summary>
    /// Applies the peer's SDP offer and produces the local answer (RFC 8829 setRemoteDescription →
    /// createAnswer, folded into one signalling-neutral step). The answer is a WebRTC answer — BUNDLE,
    /// DTLS-SRTP, rtcp-mux, and the sdes:mid extension — built by the SDP negotiator, and becomes
    /// <see cref="LocalDescription"/>. Moves the peer to <see cref="WebRtcConnectionState.Connecting"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The remote description is missing or not valid SDP.</exception>
    /// <exception cref="InvalidOperationException">No answer could be negotiated for the offer.</exception>
    public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteSdp);
        cancellationToken.ThrowIfCancellationRequested();

        SdpSessionDescription offer;
        try
        {
            offer = _parser.Parse(remoteSdp);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The remote description is not valid SDP.", nameof(remoteSdp), ex);
        }

        var result = _negotiator.NegotiateAnswer(
            offer, _options.LocalEndPoint, _options.AudioCodecs, SdpMediaDirection.SendRecv, AnswerOptions());
        if (!result.Success || result.Answer is null)
        {
            TransitionTo(WebRtcConnectionState.Failed);
            throw new InvalidOperationException("Could not negotiate an answer for the remote description.");
        }

        var answer = _serializer.Serialize(result.Answer);

        // Build the shared media transport from the negotiated descriptions (WebRTC is DTLS-SRTP over one
        // BUNDLE group). A non-bundle exchange yields no session — the answer is still returned, but the
        // peer has no media transport (logged), which StartAsync then surfaces.
        var session = WebRtcSessionFactory.TryCreate(
            offer, result.Answer, _options, _handshaker, _certificate, _loggerFactory);
        if (session is null)
            _logger.LogWarning("The remote description did not negotiate a BUNDLE media session; no transport was built.");
        else
            WireSession(session);

        lock (_sync)
        {
            _remoteDescription = remoteSdp;
            _localDescription = answer;
            _session = session;
        }

        TransitionTo(WebRtcConnectionState.Connecting);
        return Task.FromResult(answer);
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
        lock (_sync) { session = _session; }
        if (session is null)
            throw new InvalidOperationException("Apply a BUNDLE remote description before starting the peer.");

        return session.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Sends one audio RTP payload on the peer's audio track (suppressed until the handshake keys the
    /// transport). The payload is an already-encoded RTP payload — the app owns the codec.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session was built.</exception>
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SessionOrThrow().SendAudioAsync(payload, cancellationToken: cancellationToken);

    /// <summary>
    /// Packetises and sends one encoded video frame on the peer's video track.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track.</exception>
    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => SessionOrThrow().SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken);

    private BundledMediaSession SessionOrThrow()
    {
        BundledMediaSession? session;
        lock (_sync) { session = _session; }
        return session ?? throw new InvalidOperationException("Apply a BUNDLE remote description before exchanging media.");
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
    // come from the local configuration.
    private SdpMediaOptions AnswerOptions() => new()
    {
        Dtls = _options.Dtls,
        Ice = _options.Ice,
        Video = _options.Video,
        Bundle = true,
        RtcpMux = true,
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
        lock (_sync) { session = _session; _session = null; }

        TransitionTo(WebRtcConnectionState.Closed);
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }
}
