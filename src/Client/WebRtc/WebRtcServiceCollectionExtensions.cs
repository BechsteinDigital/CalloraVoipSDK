using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CalloraVoipSdk.WebRtc;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Dependency-injection entrypoint for the WebRTC facade. Standalone counterpart to
/// <see cref="ServiceCollectionExtensions.AddCalloraVoip"/>: a pure-WebRTC host calls only
/// <see cref="AddCalloraWebRtc"/>; a host that wants both facades chains
/// <c>AddCalloraVoip(...).AddWebRtc(...)</c> (ADR-012, two-facade composition).
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

        // Validate at host start (symmetry with AddCalloraVoip): a malformed ICE-server entry surfaces here
        // instead of only when the first peer is created.
        services.AddOptions<WebRtcOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebRtcOptions>, WebRtcOptionsValidator>());
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // #166 P2-8: a host that pre-registers its own IWebRtcClient (a fake, or a decorator) keeps that
        // registration through TryAddSingleton — so the concrete alias must not be layered on top of it, where
        // it would resolve the foreign implementation and fail the cast at resolution time.
        var hostOwnsClient = services.Any(descriptor => descriptor.ServiceType == typeof(IWebRtcClient));

        services.TryAddSingleton<IWebRtcClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WebRtcOptions>>().Value;
            var loggerFactory = options.LoggerFactory ?? sp.GetService<ILoggerFactory>();

            // Pass the provider so DI-registered IWebRtcClientModule services are auto-attached.
            return new WebRtcClient(options.ToConfiguration(loggerFactory), sp);
        });

        if (!hostOwnsClient)
        {
            services.TryAddSingleton(ConcreteFacadeAlias.Resolve<IWebRtcClient, WebRtcClient>);
        }

        return new CalloraWebRtcBuilder(services);
    }
}
