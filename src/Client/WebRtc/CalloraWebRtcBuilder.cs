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
    /// allocation authenticates with. Accumulates with any servers already configured. Only UDP TURN is
    /// supported for relay gathering — a TCP/TLS transport is rejected up front (the TCP/TLS relay data path
    /// is not yet wired into the WebRTC media bundle), so a misconfiguration fails loudly instead of silently
    /// gathering no relay candidate.
    /// </summary>
    /// <param name="host">The TURN server hostname or IP address.</param>
    /// <param name="username">The long-term credential username.</param>
    /// <param name="password">The long-term credential password.</param>
    /// <param name="port">Optional explicit port; the TURN default (3478) is used when null.</param>
    /// <param name="transport">The transport to reach the server on; only <see cref="IceTransport.Udp"/> is supported.</param>
    /// <exception cref="ArgumentException"><paramref name="transport"/> is not UDP (TCP/TLS TURN is not yet implemented).</exception>
    public CalloraWebRtcBuilder WithTurnServer(
        string host, string username, string password, int? port = null, IceTransport transport = IceTransport.Udp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (transport != IceTransport.Udp)
            throw new ArgumentException(
                $"TURN transport '{transport}' is not supported for WebRTC relay gathering — only UDP is implemented. " +
                "Pass IceTransport.Udp (the default) or omit the transport argument.",
                nameof(transport));
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
    /// Adds one or more fully-specified ICE servers (STUN/TURN), accumulating with any already configured.
    /// A TURN entry with a non-UDP transport is rejected — only UDP TURN is supported for relay gathering.
    /// </summary>
    /// <param name="servers">The ICE server entries to add.</param>
    /// <exception cref="ArgumentException">A TURN entry uses a non-UDP transport (TCP/TLS TURN is not yet implemented).</exception>
    public CalloraWebRtcBuilder WithIceServers(params IceServerConfiguration[] servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        foreach (var server in servers)
        {
            ArgumentNullException.ThrowIfNull(server);
            if (server.Type == IceServerType.Turn && server.Transport != IceTransport.Udp)
                throw new ArgumentException(
                    $"TURN server '{server.Host}' uses transport '{server.Transport}'; only UDP TURN is supported " +
                    "for WebRTC relay gathering. Use IceTransport.Udp.",
                    nameof(servers));
        }

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
