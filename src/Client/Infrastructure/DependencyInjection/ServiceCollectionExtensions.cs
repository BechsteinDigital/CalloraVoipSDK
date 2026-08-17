using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Dependency-injection entrypoint for CalloraVoipSdk.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers CalloraVoipSdk with options-based configuration.
    /// </summary>
    public static CalloraBuilder AddCalloraVoip(this IServiceCollection services, Action<VoipOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<VoipOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<VoipOptions>, VoipOptionsValidator>());
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // #166 P2-8: a host may bring its own IVoipClient — a fake in a test, or a decorator. TryAddSingleton
        // keeps that registration, so the concrete alias below must not be added on top of it: it would resolve
        // the foreign implementation and fail the cast, turning the facade's documented mockability into an
        // InvalidCastException at resolution time.
        var hostOwnsClient = services.Any(descriptor => descriptor.ServiceType == typeof(IVoipClient));

        services.TryAddSingleton<IVoipClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VoipOptions>>().Value;
            var loggerFactory = options.LoggerFactory ?? sp.GetService<ILoggerFactory>();

            return new VoipClient(options.ToConfiguration(loggerFactory), sp);
        });

        if (!hostOwnsClient)
        {
            // Same instance as the interface registration; only registered while the SDK owns that
            // registration. An override that arrives AFTER this call still wins for IVoipClient, so the alias
            // reports an actionable error rather than an InvalidCastException.
            services.TryAddSingleton(ConcreteFacadeAlias.Resolve<IVoipClient, VoipClient>);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CalloraHostedService>());

        return new CalloraBuilder(services);
    }
}
