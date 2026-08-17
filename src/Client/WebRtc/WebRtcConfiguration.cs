using System.Net;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Immutable configuration for a <see cref="WebRtcClient"/> (the direct-construction surface; the
/// DI/options path projects <see cref="WebRtcOptions"/> onto it). All fields are optional — a
/// zero-config <c>new WebRtcClient()</c> binds an ephemeral loopback endpoint, offers Opus audio, and
/// uses a fresh per-peer DTLS identity.
/// </summary>
/// <remarks>
/// Immutable means immutable in fact, not by convention (#166 P2-7): every collection property takes a
/// defensive copy of the list it is given, so a caller that keeps and later mutates its own list — including
/// the mutable <see cref="WebRtcOptions"/> instance the DI path maps from — cannot reach into a live client's
/// configuration. This is also the boundary where an unusable ICE-server entry is rejected, so the direct,
/// options and builder paths agree instead of one failing fast and the others accepting silently.
/// </remarks>
public sealed class WebRtcConfiguration
{
    private readonly IReadOnlyList<string> _audioCodecs = ["opus"];
    private readonly IReadOnlyList<string> _videoCodecs = ["H264"];
    private readonly IReadOnlyList<string> _simulcastLayers = [];
    private readonly IReadOnlyList<IceServerConfiguration> _iceServers = [];

    /// <summary>
    /// Local media endpoint the peer binds for RTP/RTCP/ICE/DTLS. Default is an ephemeral loopback
    /// port; production deployments set a reachable address. (Host-candidate advertisement and trickle
    /// ICE for remote reachability arrive in a later slice — see ADR-012.)
    /// </summary>
    public IPEndPoint LocalEndPoint { get; init; } = new(IPAddress.Loopback, 0);

    /// <summary>Audio codecs to offer, by name (<c>opus</c>, <c>PCMU</c>, <c>PCMA</c>, <c>G722</c>). Default: Opus.</summary>
    /// <exception cref="ArgumentNullException">The assigned list is null.</exception>
    public IReadOnlyList<string> AudioCodecs
    {
        get => _audioCodecs;
        init => _audioCodecs = Copy(value, nameof(AudioCodecs));
    }

    /// <summary>Whether to offer a video m-line.</summary>
    public bool EnableVideo { get; init; }

    /// <summary>
    /// Makes a fixed 1+1 peer offer numeric MIDs from the first offer instead of the historic semantic
    /// <c>audio</c>/<c>video</c> MIDs. Runtime-added tracks (<c>AddAudioTrack</c>/<c>AddVideoTrack</c>) always
    /// use stable, append-only numeric MIDs regardless of this flag (RFC 8829 — existing m-lines never move or
    /// change MID), so it only affects the fixed 1+1 case. Default <see langword="false"/> keeps the
    /// byte-identical historic 1+1 SDP.
    /// </summary>
    public bool UseStableNumericMediaIds { get; init; }

    /// <summary>Video codecs to offer when <see cref="EnableVideo"/> is set, by name (<c>H264</c>, <c>VP8</c>). Default: H264.</summary>
    /// <exception cref="ArgumentNullException">The assigned list is null.</exception>
    public IReadOnlyList<string> VideoCodecs
    {
        get => _videoCodecs;
        init => _videoCodecs = Copy(value, nameof(VideoCodecs));
    }

    /// <summary>
    /// Send-side simulcast layers to offer (RFC 8853), by <c>a=rid</c> id in send order, e.g.
    /// <c>["hi", "mid", "lo"]</c>. Empty (default) offers a single video stream. When set, the app sends
    /// each layer's encoded frames via <see cref="IPeerConnection.SendVideoFrameAsync(string, System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>
    /// — the SDK packetises each on its own SSRC with the RID header extension (RFC 8852). Requires
    /// <see cref="EnableVideo"/>. This peer must be the offerer for the simulcast to be advertised.
    /// </summary>
    /// <exception cref="ArgumentNullException">The assigned list is null.</exception>
    public IReadOnlyList<string> SimulcastLayers
    {
        get => _simulcastLayers;
        init => _simulcastLayers = Copy(value, nameof(SimulcastLayers));
    }

    /// <summary>
    /// STUN/TURN servers for gathering server-reflexive and relay ICE candidates (RFC 8445 §5.1.1). Empty
    /// (default) gathers only the host candidate. STUN entries are queried through the media socket when the
    /// app calls <see cref="IPeerConnection.GatherCandidatesAsync"/> — the discovered candidates surface on
    /// <see cref="IPeerConnection.LocalIceCandidateDiscovered"/> to trickle out (RFC 8838).
    /// </summary>
    /// <remarks>
    /// Only UDP TURN is supported for relay gathering; a TURN entry on TCP/TLS is rejected here rather than
    /// accepted into a client that would then silently gather no relay candidate (#166 P2-7, feature: #155).
    /// </remarks>
    /// <exception cref="ArgumentNullException">The assigned list is null.</exception>
    /// <exception cref="ArgumentException">A TURN entry uses a non-UDP transport.</exception>
    public IReadOnlyList<IceServerConfiguration> IceServers
    {
        get => _iceServers;
        init
        {
            var servers = Copy(value, nameof(IceServers));
            foreach (var server in servers)
            {
                ArgumentNullException.ThrowIfNull(server, nameof(IceServers));
                if (WebRtcIceServerPolicy.IsUnsupportedTurnTransport(server))
                {
                    throw new ArgumentException(
                        WebRtcIceServerPolicy.UnsupportedTurnTransportMessage(server), nameof(IceServers));
                }
            }

            _iceServers = servers;
        }
    }

    /// <summary>
    /// DTLS-SRTP identity for the peer's certificate/fingerprint (must carry an exportable ECDSA P-256
    /// private key); <see langword="null"/> generates a fresh ephemeral identity per peer — the WebRTC
    /// privacy default.
    /// </summary>
    public X509Certificate2? DtlsCertificate { get; init; }

    /// <summary>Logger factory for diagnostics; <see langword="null"/> disables logging.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    // Snapshots the caller's list so a later mutation of it cannot change this configuration. An already
    // frozen list still gets copied: the type alone (IReadOnlyList) does not prove the instance is immutable,
    // and the lists are short one-time configuration data, not a hot path.
    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> value, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(value, propertyName);
        return value.Count == 0 ? [] : [.. value];
    }
}
