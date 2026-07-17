using Microsoft.Extensions.DependencyInjection;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.WebRtc;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Builder for optional CalloraVoipSdk dependency overrides.
/// </summary>
public sealed class CalloraBuilder
{
    private readonly IServiceCollection _services;

    internal CalloraBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Registers a concrete audio device implementation.
    /// </summary>
    public CalloraBuilder WithAudioDevice<T>() where T : class, IAudioDevice
    {
        _services.AddSingleton<IAudioDevice, T>();
        return this;
    }

    /// <summary>
    /// Registers a concrete SIP telemetry sink implementation.
    /// </summary>
    public CalloraBuilder WithTelemetrySink<T>() where T : class, ISipTelemetrySink
    {
        _services.AddSingleton<ISipTelemetrySink, T>();
        return this;
    }

    /// <summary>
    /// Applies additional ICE configuration.
    /// </summary>
    public CalloraBuilder WithIce(Action<IceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _services.PostConfigure<SdkOptions>(options => configure(options.Ice));
        return this;
    }

    /// <summary>
    /// Sets the default SIP signaling transport for outbound requests
    /// (see <see cref="SdkConfiguration.DefaultTransport"/>).
    /// </summary>
    public CalloraBuilder WithTransport(SipTransport transport)
    {
        _services.PostConfigure<SdkOptions>(options => options.DefaultTransport = transport);
        return this;
    }

    /// <summary>
    /// Composes the WebRTC facade onto the same host (ADR-012, two-facade composition). Equivalent to a
    /// standalone <see cref="WebRtcServiceCollectionExtensions.AddCalloraWebRtc"/>, letting a host
    /// configure both facades in one chain: <c>AddCallora(...).AddWebRtc(...)</c>. Returns the WebRTC
    /// builder so WebRTC-specific overrides continue the chain.
    /// </summary>
    public CalloraWebRtcBuilder AddWebRtc(Action<WebRtcOptions>? configure = null)
        => _services.AddCalloraWebRtc(configure);
}
