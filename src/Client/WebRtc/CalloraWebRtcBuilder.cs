using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.WebRtc;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Builder for optional WebRTC-facade dependency overrides (Level 3). Mirrors <see cref="CalloraBuilder"/>
/// for the SIP facade and is returned by <see cref="WebRtcServiceCollectionExtensions.AddCalloraWebRtc"/>
/// and by <see cref="CalloraBuilder.AddWebRtc"/>.
/// </summary>
public sealed class CalloraWebRtcBuilder
{
    private readonly IServiceCollection _services;

    internal CalloraWebRtcBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Enables video negotiation and, when <paramref name="codecs"/> is non-empty, sets the ordered
    /// video codec preference (see <see cref="WebRtcOptions.VideoCodecs"/>).
    /// </summary>
    public CalloraWebRtcBuilder WithVideo(params string[] codecs)
    {
        _services.PostConfigure<WebRtcOptions>(options =>
        {
            options.EnableVideo = true;
            if (codecs is { Length: > 0 })
            {
                options.VideoCodecs = codecs;
            }
        });
        return this;
    }

    /// <summary>
    /// Enables video negotiation for end-to-end encrypted frames: the app encrypts each frame before handing it
    /// over (WebRTC Encoded Transform / SFrame, RFC 9605) and the SDK never reads the content (#223, ADR-068).
    /// Sets <see cref="WebRtcOptions.EnableVideo"/> and <see cref="WebRtcOptions.OpaqueVideoFrames"/>, and — when
    /// <paramref name="codecs"/> is non-empty — the ordered codec preference, exactly as <see cref="WithVideo"/>.
    /// </summary>
    /// <remarks>
    /// Key-frame detection is off on this path (the flag is always <see langword="false"/> — "unknown", not
    /// "no"), and the opaque H.264 framing is not what a browser emits: see
    /// <see cref="WebRtcConfiguration.OpaqueVideoFrames"/> for the full semantics and interop scope.
    /// </remarks>
    public CalloraWebRtcBuilder WithOpaqueVideo(params string[] codecs)
    {
        WithVideo(codecs);
        _services.PostConfigure<WebRtcOptions>(options => options.OpaqueVideoFrames = true);
        return this;
    }

    /// <summary>
    /// Pins the DTLS-SRTP identity certificate used for every peer (ECDSA P-256 with an exportable
    /// private key); see <see cref="WebRtcOptions.DtlsCertificate"/>.
    /// </summary>
    public CalloraWebRtcBuilder WithDtlsCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _services.PostConfigure<WebRtcOptions>(options => options.DtlsCertificate = certificate);
        return this;
    }

    /// <summary>Overrides the logger factory used for WebRTC diagnostics.</summary>
    public CalloraWebRtcBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _services.PostConfigure<WebRtcOptions>(options => options.LoggerFactory = loggerFactory);
        return this;
    }

    /// <summary>
    /// Adds a STUN server for server-reflexive (srflx) candidate gathering (RFC 8445). Accumulates with any
    /// servers already configured; see <see cref="WebRtcOptions.IceServers"/>.
    /// </summary>
    /// <param name="host">The STUN server hostname or IP address.</param>
    /// <param name="port">Optional explicit port; the STUN default is used when null.</param>
    public CalloraWebRtcBuilder WithStunServer(string host, int? port = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return AddIceServer(new IceServerConfiguration { Type = IceServerType.Stun, Host = host, Port = port });
    }

    /// <summary>
    /// Adds a TURN server for relay candidate gathering (RFC 8656), with the long-term credentials the
    /// allocation authenticates with. Accumulates with any servers already configured. The relay is gathered
    /// over <see cref="IceTransport.Udp"/> (on the shared media socket) or over <see cref="IceTransport.Tcp"/> /
    /// <see cref="IceTransport.Tls"/> (a stream relay on its own connection to the server, ADR-073). TLS gives a
    /// last-resort path across firewalls that allow only outbound 443.
    /// </summary>
    /// <param name="host">The TURN server hostname or IP address.</param>
    /// <param name="username">The long-term credential username.</param>
    /// <param name="password">The long-term credential password.</param>
    /// <param name="port">Optional explicit port; the TURN default for the transport is used when null (3478 for UDP/TCP, 5349 for TLS).</param>
    /// <param name="transport">The transport to reach the server on (UDP, TCP or TLS).</param>
    public CalloraWebRtcBuilder WithTurnServer(
        string host, string username, string password, int? port = null, IceTransport transport = IceTransport.Udp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return AddIceServer(new IceServerConfiguration
        {
            Type = IceServerType.Turn,
            Host = host,
            Port = port,
            Transport = transport,
            Username = username,
            Password = password,
        });
    }

    /// <summary>
    /// Adds one or more fully-specified ICE servers (STUN/TURN), accumulating with any already configured. A
    /// TURN entry may use any <see cref="IceTransport"/> — UDP (media-socket relay) or TCP/TLS (a stream relay
    /// on its own connection, ADR-073).
    /// </summary>
    /// <param name="servers">The ICE server entries to add.</param>
    public CalloraWebRtcBuilder WithIceServers(params IceServerConfiguration[] servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        foreach (var server in servers)
            ArgumentNullException.ThrowIfNull(server);

        _services.PostConfigure<WebRtcOptions>(options => options.IceServers = [.. options.IceServers, .. servers]);
        return this;
    }

    // Appends one ICE server to the accumulated list (PostConfigure runs after the caller's own configuration).
    private CalloraWebRtcBuilder AddIceServer(IceServerConfiguration server)
    {
        _services.PostConfigure<WebRtcOptions>(options => options.IceServers = [.. options.IceServers, server]);
        return this;
    }
}
