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

        services.TryAddSingleton<IVoipClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VoipOptions>>().Value;
            var loggerFactory = options.LoggerFactory ?? sp.GetService<ILoggerFactory>();

            return new VoipClient(options.ToConfiguration(loggerFactory), sp);
        });

        services.TryAddSingleton(sp => (VoipClient)sp.GetRequiredService<IVoipClient>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CalloraHostedService>());

        return new CalloraBuilder(services);
    }
}
