using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CalloraVoipSdk.WebRtc;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Dependency-injection entrypoint for the WebRTC facade. Standalone counterpart to
/// <see cref="ServiceCollectionExtensions.AddCallora"/>: a pure-WebRTC host calls only
/// <see cref="AddCalloraWebRtc"/>; a host that wants both facades chains
/// <c>AddCallora(...).AddWebRtc(...)</c> (ADR-012, two-facade composition).
/// </summary>
public static class WebRtcServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WebRTC facade (<see cref="IWebRtcClient"/> / <see cref="WebRtcClient"/>) with
    /// options-based configuration, returning a builder for optional dependency overrides.
    /// </summary>
    public static CalloraWebRtcBuilder AddCalloraWebRtc(this IServiceCollection services, Action<WebRtcOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WebRtcOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IWebRtcClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WebRtcOptions>>().Value;
            var loggerFactory = options.LoggerFactory ?? sp.GetService<ILoggerFactory>();

            return new WebRtcClient(options.ToConfiguration(loggerFactory));
        });

        services.TryAddSingleton(sp => (WebRtcClient)sp.GetRequiredService<IWebRtcClient>());

        return new CalloraWebRtcBuilder(services);
    }
}
