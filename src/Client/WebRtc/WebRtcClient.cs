using System.Security.Cryptography;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The WebRTC peer facade (Level 1). Builds signalling-neutral <see cref="IPeerConnection"/>s that run
/// ICE, DTLS-SRTP, BUNDLE and RTP/RTCP internally; the app owns signalling and the codec (transport
/// only). Zero-config <c>new WebRtcClient()</c> offers Opus over an ephemeral loopback endpoint with a
/// fresh per-peer DTLS identity. See ADR-012 for the four-level design and the manager/DI tiers to come.
/// </summary>
public sealed class WebRtcClient : IWebRtcClient
{
    private readonly WebRtcConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly WebRtcModuleRegistry _modules;
    private readonly PeerConnectionManager _peers = new();

    /// <summary>Creates a client with the given configuration, or all defaults when omitted.</summary>
    public WebRtcClient(WebRtcConfiguration? config = null) : this(config, services: null)
    {
    }

    /// <summary>
    /// Creates a client and auto-registers every <see cref="IWebRtcClientModule"/> resolvable from
    /// <paramref name="services"/> (the DI construction path used by <c>AddCalloraWebRtc</c>).
    /// </summary>
    internal WebRtcClient(WebRtcConfiguration? config, IServiceProvider? services)
    {
        _config = config ?? new WebRtcConfiguration();
        _loggerFactory = _config.LoggerFactory ?? NullLoggerFactory.Instance;

        // Module registration is the last construction step so OnAttached sees a fully built client.
        _modules = new WebRtcModuleRegistry(this);
        if (services?.GetService(typeof(IEnumerable<IWebRtcClientModule>)) is IEnumerable<IWebRtcClientModule> injected)
        {
            foreach (var module in injected)
            {
                _modules.Register(module);
            }
        }
    }

    /// <inheritdoc />
    public IWebRtcModuleRegistry Modules => _modules;

    /// <inheritdoc />
    public IPeerConnectionManager Peers => _peers;

    /// <inheritdoc />
    public IPeerConnection CreatePeer()
    {
        // A fresh DTLS identity per peer is the WebRTC privacy default (unless the app pins one).
        var certificate = _config.DtlsCertificate is { } pinned
            ? DtlsCertificate.FromX509(pinned)
            : DtlsCertificate.GenerateEcdsaP256();

        var options = new WebRtcPeerOptions
        {
            LocalEndPoint = _config.LocalEndPoint,
            AudioCodecs = ResolveCodecs(_config.AudioCodecs, WebRtcCodecCatalog.Audio),
            Video = _config.EnableVideo
                ? new SdpVideoMediaOptions
                {
                    Port = _config.LocalEndPoint.Port,
                    Codecs = ResolveVideoCodecs(_config.VideoCodecs),
                    SimulcastSendRids = _config.SimulcastLayers,
                }
                : null,
            Dtls = new SdpDtlsParameters
            {
                Algorithm = certificate.Fingerprint.Algorithm,
                Fingerprint = certificate.Fingerprint.Value,
            },
            // Advertise trickle support (RFC 8838/8840 a=ice-options:trickle) so a peer knows candidates may
            // arrive after the offer/answer; the SDK gathers host+srflx and supports out-of-band trickle.
            Ice = new SdpIceParameters { Ufrag = GenerateUfrag(), Pwd = GeneratePassword(), Options = "trickle" },
            IceServers = _config.IceServers,
        };

        // A STUN probe is built only when servers are configured — a zero-config peer gathers host-only.
        var stunProbe = _config.IceServers.Count > 0
            ? new StunIceProbe(
                new StunClient(new StunMessageCodec(), _loggerFactory.CreateLogger<StunClient>()),
                _loggerFactory)
            : null;

        var peer = new WebRtcPeerConnection(
            options,
            new SdpOfferAnswerNegotiator(),
            new SdpSessionParser(),
            new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(_loggerFactory.CreateLogger<DtlsSrtpHandshaker>()),
            certificate,
            _loggerFactory,
            stunProbe);

        var connection = new PeerConnection(peer, _loggerFactory.CreateLogger<PeerConnection>(), _peers.Untrack);
        _peers.Track(connection);
        return connection;
    }

    private static IReadOnlyList<SdpCodecDefinition> ResolveCodecs(
        IReadOnlyList<string> names,
        Func<string, SdpCodecDefinition> resolve)
        => names.Select(resolve).ToArray();

    // Video goes through the mature VideoCodecCatalog: it assigns distinct payload types (VP8=96, H264=97),
    // and the negotiator adds the matching RTX repair codecs, fmtp and RTCP feedback. Unknown names fail
    // fast, consistent with the audio path.
    private static IReadOnlyList<SdpCodecDefinition> ResolveVideoCodecs(IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (!VideoCodecCatalog.IsSupported(name))
            {
                throw new ArgumentException($"Unknown WebRTC video codec '{name}'.", nameof(names));
            }
        }

        return VideoCodecCatalog.Resolve(names);
    }

    // RFC 8445 §5.1.2 style credentials: an ICE ufrag/pwd generated fresh per peer.
    private static string GenerateUfrag() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
    private static string GeneratePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// Maps WebRTC audio codec names to their wire definitions with the standard payload types (RFC 3551
/// static PTs; Opus on its conventional dynamic PT). Video codecs are resolved through the shared
/// <see cref="VideoCodecCatalog"/> instead. Transport-only: the SDK packetises, the app owns the codec.
/// </summary>
internal static class WebRtcCodecCatalog
{
    public static SdpCodecDefinition Audio(string name) => name.Trim().ToLowerInvariant() switch
    {
        "opus" => new SdpCodecDefinition { PayloadType = 111, Name = "opus", ClockRate = 48000, Channels = 2 },
        "pcmu" => new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 },
        "pcma" => new SdpCodecDefinition { PayloadType = 8, Name = "PCMA", ClockRate = 8000 },
        "g722" => new SdpCodecDefinition { PayloadType = 9, Name = "G722", ClockRate = 8000 },
        _ => throw new ArgumentException($"Unknown WebRTC audio codec '{name}'.", nameof(name)),
    };
}
