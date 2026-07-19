using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CalloraVoipSdk.Hosting;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Dependency-injection entrypoint for the TURN/STUN server host. Standalone counterpart to
/// <c>AddCalloraVoip</c> / <c>AddCalloraWebRtc</c>: a host that runs a TURN server calls
/// <see cref="AddCalloraTurnServer"/> and receives a builder for optional overrides.
/// </summary>
public static class TurnServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the TURN/STUN server host (<see cref="ITurnServerHost"/> / <see cref="TurnServerHost"/>) with
    /// options-based configuration, returning a builder for fluent overrides (bind endpoint, realm, credentials,
    /// TLS certificate).
    /// </summary>
    public static CalloraTurnServerBuilder AddCalloraTurnServer(
        this IServiceCollection services, Action<TurnServerHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TurnServerHostOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ITurnServerHost>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TurnServerHostOptions>>().Value;
            var loggerFactory = options.LoggerFactory ?? sp.GetService<ILoggerFactory>();
            return new TurnServerHost(options.ToConfiguration(loggerFactory));
        });

        return new CalloraTurnServerBuilder(services);
    }
}
