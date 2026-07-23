using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Hosting;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Builder for optional TURN/STUN server-host overrides (Level 3). Returned by
/// <see cref="TurnServerServiceCollectionExtensions.AddCalloraTurnServer"/>; mirrors <c>CalloraWebRtcBuilder</c>.
/// </summary>
public sealed class CalloraTurnServerBuilder
{
    private readonly IServiceCollection _services;

    internal CalloraTurnServerBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>Sets the endpoint the server binds to (use port <c>0</c> for an ephemeral port).</summary>
    public CalloraTurnServerBuilder WithBindEndPoint(IPEndPoint bindEndPoint)
    {
        ArgumentNullException.ThrowIfNull(bindEndPoint);
        _services.PostConfigure<TurnServerHostOptions>(options => options.BindEndPoint = bindEndPoint);
        return this;
    }

    /// <summary>Sets the listen transport (UDP, TCP, or TLS). TLS also needs <see cref="WithTlsCertificate"/>.</summary>
    public CalloraTurnServerBuilder WithTransport(IceTransport transport)
    {
        _services.PostConfigure<TurnServerHostOptions>(options => options.Transport = transport);
        return this;
    }

    /// <summary>Sets the realm returned in 401 challenges and used for long-term key derivation.</summary>
    public CalloraTurnServerBuilder WithRealm(string realm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        _services.PostConfigure<TurnServerHostOptions>(options => options.Realm = realm);
        return this;
    }

    /// <summary>Adds one long-term credential the server accepts. Accumulates with any already configured.</summary>
    public CalloraTurnServerBuilder WithCredential(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        _services.PostConfigure<TurnServerHostOptions>(options =>
            options.Credentials.Add(new TurnServerCredential { Username = username, Password = password }));
        return this;
    }

    /// <summary>Disables authentication — the server accepts unauthenticated allocations (test/lab use).</summary>
    public CalloraTurnServerBuilder WithoutAuthentication()
    {
        _services.PostConfigure<TurnServerHostOptions>(options => options.RequireAuthentication = false);
        return this;
    }

    /// <summary>Pins the TLS server certificate (required for a TLS transport).</summary>
    public CalloraTurnServerBuilder WithTlsCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _services.PostConfigure<TurnServerHostOptions>(options => options.TlsCertificate = certificate);
        return this;
    }

    /// <summary>
    /// Sets the public IP address advertised to clients in XOR-RELAYED-ADDRESS (RFC 8656 §7.2). Use this in
    /// NAT'd, multi-homed, or cloud deployments where the relay socket's bound/routed address is not the
    /// address remote peers must reach.
    /// </summary>
    public CalloraTurnServerBuilder WithPublicRelayAddress(IPAddress publicRelayAddress)
    {
        ArgumentNullException.ThrowIfNull(publicRelayAddress);
        _services.PostConfigure<TurnServerHostOptions>(options => options.PublicRelayAddress = publicRelayAddress);
        return this;
    }

    /// <summary>Overrides the logger factory used for server diagnostics.</summary>
    public CalloraTurnServerBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _services.PostConfigure<TurnServerHostOptions>(options => options.LoggerFactory = loggerFactory);
        return this;
    }
}
