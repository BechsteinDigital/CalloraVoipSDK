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
    private readonly IReadOnlyList<string> _simulcastRecvLayers = [];
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
    /// Treats this peer's video frames as opaque: the app encrypts them end to end before handing them over
    /// (WebRTC Encoded Transform / SFrame, RFC 9605), so the SDK must never read the content. Default
    /// <see langword="false"/> keeps the clear-media payload format, which parses the frame to detect key frames.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With the switch on, both halves of the video payload format work from the RTP framing alone: the frame is
    /// carried and reassembled verbatim, and <see cref="EncodedFrame.IsKeyFrame"/> is always
    /// <see langword="false"/> — "unknown", not "no", until the key-frame signal moves into a plaintext header
    /// extension (#223 follow-up). This is what makes "the provider cannot see the content" a property of the
    /// code rather than of its intentions (Anlage 31b BMV-Ä § 2 Abs. 3/4).
    /// </para>
    /// <para>
    /// Interop scope, stated rather than assumed: the opaque H.264 framing is self-consistent between two SDK
    /// endpoints and through a relay that forwards payloads untouched. It is <b>not</b> what a browser emits — a
    /// browser transform keeps the NAL headers in the clear — so do not enable it for a browser peer. See
    /// ADR-068.
    /// </para>
    /// </remarks>
    public bool OpaqueVideoFrames { get; init; }

    /// <summary>
    /// Send-side simulcast layers to offer (RFC 8853), by <c>a=rid</c> id in send order, e.g.
    /// <c>["hi", "mid", "lo"]</c>. Empty (default) offers a single video stream. When set, the app sends
    /// each layer's encoded frames via <see cref="IPeerConnection.SendVideoFrameAsync(string, System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>
    /// — the SDK packetises each on its own SSRC with the RID header extension (RFC 8852). Requires
    /// <see cref="EnableVideo"/>. Advertised whether this peer offers (<c>a=simulcast:send</c>) or answers a
    /// peer's <c>a=simulcast:recv</c> (#369). Fewer than two <em>distinct</em> ids is not simulcast — a lone
    /// <c>a=rid</c> is dropped (RFC 8853; Chrome strips it), so such a value falls back to a single stream and
    /// logs a warning.
    /// </summary>
    /// <exception cref="ArgumentNullException">The assigned list is null.</exception>
    public IReadOnlyList<string> SimulcastLayers
    {
        get => _simulcastLayers;
        init => _simulcastLayers = Copy(value, nameof(SimulcastLayers));
    }

    /// <summary>
    /// Receive-side simulcast layers to ask the peer for (RFC 8853 §5.3), by <c>a=rid</c> id, e.g.
    /// <c>["hi", "mid", "lo"]</c>. Empty (default) asks for a single video stream. When set, the offer
    /// advertises <c>a=simulcast:recv</c> with the matching <c>a=rid … recv</c> and the RID header extension
    /// (RFC 8852), so the peer it asked can tag each layer it sends back — each arriving layer then carries its
    /// rid on the received frame. Requires <see cref="EnableVideo"/>. This peer must be the offerer for the
    /// request to be advertised (as the answerer, an offered <c>a=simulcast:send</c> is received automatically,
    /// #369). Fewer than two <em>distinct</em> ids is not simulcast — a lone <c>a=rid</c> is dropped (RFC 8853),
    /// so such a value falls back to a single stream and logs a warning.
    /// </summary>
    /// <exception cref="ArgumentNullException">The assigned list is null.</exception>
    public IReadOnlyList<string> SimulcastRecvLayers
    {
        get => _simulcastRecvLayers;
        init => _simulcastRecvLayers = Copy(value, nameof(SimulcastRecvLayers));
    }

    /// <summary>
    /// STUN/TURN servers for gathering server-reflexive and relay ICE candidates (RFC 8445 §5.1.1). Empty
    /// (default) gathers only the host candidate. STUN entries are queried through the media socket when the
    /// app calls <see cref="IPeerConnection.GatherCandidatesAsync"/> — the discovered candidates surface on
    /// <see cref="IPeerConnection.LocalIceCandidateDiscovered"/> to trickle out (RFC 8838).
    /// </summary>
    /// <remarks>
    /// TURN relay is gathered over UDP (on the shared media socket) and over TCP/TLS (a stream relay on its own
    /// connection, ADR-073), so any <see cref="IceTransport"/> is accepted for a TURN entry.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The assigned list is null.</exception>
    public IReadOnlyList<IceServerConfiguration> IceServers
    {
        get => _iceServers;
        init
        {
            var servers = Copy(value, nameof(IceServers));
            foreach (var server in servers)
            {
                ArgumentNullException.ThrowIfNull(server, nameof(IceServers));
            }

            _iceServers = servers;
        }
    }

    /// <summary>
    /// DTLS-SRTP identity for the peer's certificate/fingerprint (must carry an exportable ECDSA P-256
    /// private key); <see langword="null"/> generates a fresh ephemeral identity per peer — the WebRTC
    /// privacy default.
    /// </summary>
    /// <summary>
    /// Playout delay in milliseconds for buffering <em>inbound</em> audio before it is raised, or 0 (the
    /// default) to raise packets the moment they arrive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Leave this at 0 unless the app mixes.</b> A peer that forwards audio — an SFU, a recorder writing
    /// what it gets — wants arrivals raised immediately, because the browser at the far end runs its own
    /// jitter buffer (NetEQ) and a second one here only adds latency to the same job.
    /// </para>
    /// <para>
    /// A peer whose consumer <em>mixes</em> is in the opposite position: it must produce one frame every
    /// frame interval from whatever each source has delivered by then, and it cannot wait. Handed raw
    /// arrivals it reads a burst as a single usable frame and the rest as silence — audible as audio that
    /// cuts out after every pause and returns seconds later. Opus DTX makes that the normal case rather
    /// than the exception, because a browser sends nothing while nobody speaks and the packets after the
    /// silence arrive together. Setting this puts an adaptive jitter buffer in front of the receive event
    /// so the mixer gets a steady cadence; 60 is a reasonable starting point, and the buffer adapts from
    /// there.
    /// </para>
    /// </remarks>
    public int AudioReceivePlayoutDelayMs { get; init; }

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
