using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CalloraVoipSdk.Core.Infrastructure.Audio;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Dependency-injection entrypoint for CalloraVoipSdk.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers CalloraVoipSdk with options-based configuration.
    /// </summary>
    public static CalloraBuilder AddCallora(this IServiceCollection services, Action<SdkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SdkOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SdkOptions>, SdkOptionsValidator>());
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IVoipClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SdkOptions>>().Value;
            var loggerFactory = options.LoggerFactory ?? sp.GetService<ILoggerFactory>();

            var configuration = new SdkConfiguration
            {
                UserAgent = options.UserAgent,
                Tls = options.Tls,
                DefaultTransport = options.DefaultTransport,
                LoggerFactory = loggerFactory,
                SrtpPolicy = options.SrtpPolicy,
                Ice = options.Ice.ToConfiguration(),
                MaxConcurrentCallsPerLine = options.MaxConcurrentCallsPerLine,
                AudioDevice = options.AudioDevice ?? SilenceAudioDevice.Instance,
                EnableAutomaticAudioDeviceSelection = options.EnableAutomaticAudioDeviceSelection,
                PreferredAudioCodecs = options.PreferredAudioCodecs
            };

            return new VoipClient(configuration, sp);
        });

        services.TryAddSingleton(sp => (VoipClient)sp.GetRequiredService<IVoipClient>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CalloraHostedService>());

        return new CalloraBuilder(services);
    }
}
