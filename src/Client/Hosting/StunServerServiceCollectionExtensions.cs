using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CalloraVoipSdk.Hosting;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Dependency-injection entrypoint for the STUN server host. Standalone counterpart to
/// <see cref="TurnServerServiceCollectionExtensions.AddCalloraTurnServer"/> for a pure-STUN (Binding) deployment.
/// </summary>
public static class StunServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the STUN server host (<see cref="IStunServerHost"/> / <see cref="StunServerHost"/>) with
    /// options-based configuration, returning a builder for fluent overrides.
    /// </summary>
    public static CalloraStunServerBuilder AddCalloraStunServer(
        this IServiceCollection services, Action<StunServerHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<StunServerHostOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IStunServerHost>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StunServerHostOptions>>().Value;
            var loggerFactory = options.LoggerFactory ?? sp.GetService<ILoggerFactory>();
            return new StunServerHost(options.ToConfiguration(loggerFactory));
        });

        return new CalloraStunServerBuilder(services);
    }
}

/// <summary>
/// Builder for optional STUN server-host overrides (Level 3). Returned by
/// <see cref="StunServerServiceCollectionExtensions.AddCalloraStunServer"/>.
/// </summary>
public sealed class CalloraStunServerBuilder
{
    private readonly IServiceCollection _services;

    internal CalloraStunServerBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>Sets the endpoint the server binds to (use port <c>0</c> for an ephemeral port).</summary>
    public CalloraStunServerBuilder WithBindEndPoint(IPEndPoint bindEndPoint)
    {
        ArgumentNullException.ThrowIfNull(bindEndPoint);
        _services.PostConfigure<StunServerHostOptions>(options => options.BindEndPoint = bindEndPoint);
        return this;
    }

    /// <summary>Sets the listen transport (UDP, TCP, or TLS). TLS also needs <see cref="WithTlsCertificate"/>.</summary>
    public CalloraStunServerBuilder WithTransport(IceTransport transport)
    {
        _services.PostConfigure<StunServerHostOptions>(options => options.Transport = transport);
        return this;
    }

    /// <summary>Pins the TLS server certificate (required for a TLS transport).</summary>
    public CalloraStunServerBuilder WithTlsCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _services.PostConfigure<StunServerHostOptions>(options => options.TlsCertificate = certificate);
        return this;
    }

    /// <summary>Overrides the logger factory used for server diagnostics.</summary>
    public CalloraStunServerBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _services.PostConfigure<StunServerHostOptions>(options => options.LoggerFactory = loggerFactory);
        return this;
    }
}
