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
    private readonly ILogger<WebRtcPeerConnection> _logger;
    private readonly object _sync = new();

    private WebRtcConnectionState _state = WebRtcConnectionState.New;
    private string? _remoteDescription;
    private string? _localDescription;

    /// <summary>Raised when the connection state changes (RFC 8829 <c>connectionstatechange</c>).</summary>
    public event Action<WebRtcConnectionState>? ConnectionStateChanged;

    public WebRtcPeerConnection(
        WebRtcPeerOptions options,
        ISdpOfferAnswerNegotiator negotiator,
        ISdpSessionParser parser,
        ISdpSessionSerializer serializer,
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
        ArgumentNullException.ThrowIfNull(loggerFactory);
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
        lock (_sync)
        {
            _remoteDescription = remoteSdp;
            _localDescription = answer;
        }

        TransitionTo(WebRtcConnectionState.Connecting);
        return Task.FromResult(answer);
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
    public ValueTask DisposeAsync()
    {
        TransitionTo(WebRtcConnectionState.Closed);
        return ValueTask.CompletedTask;
    }
}
