using System.Net;
using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.Modules;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the A2 follow-up: a module throwing during OnAttached must not leak
/// already constructed runtime resources out of a failed VoipClient constructor.
/// </summary>
public sealed class VoipClientModuleRegistrationSafetyTests
{
    [Fact]
    public void Throwing_module_surfaces_error_and_disposes_transport_runtime()
    {
        var factory = new RecordingTransportFactory();

        var services = new ServiceCollection();
        services.AddCallora(options =>
        {
            options.UserAgent = "CalloraVoipSdk.Core.IntegrationTests/1.0";
            options.EnableAutomaticAudioDeviceSelection = false;
        });
        services.AddSingleton<ISipTransportFactory>(factory);
        services.AddSingleton<IVoipClientModule>(new ThrowingModule());

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => { _ = provider.GetRequiredService<IVoipClient>(); });

        Assert.Equal("attach-boom", ex.Message);
        Assert.NotNull(factory.CreatedRuntime);
        Assert.True(factory.CreatedRuntime!.IsDisposed);
    }
}

internal sealed class ThrowingModule : IVoipClientModule
{
    public string ModuleId => "throwing-module";

    public void OnAttached(IVoipClient client) => throw new InvalidOperationException("attach-boom");
}

internal sealed class RecordingTransportFactory : ISipTransportFactory
{
    public RecordingTransportRuntime? CreatedRuntime { get; private set; }

    public ISipTransportRuntime Create(TlsConfiguration? tls, ILoggerFactory loggerFactory)
    {
        CreatedRuntime = new RecordingTransportRuntime(new SipTransportFactory().Create(tls, loggerFactory));
        return CreatedRuntime;
    }
}

internal sealed class RecordingTransportRuntime(ISipTransportRuntime inner) : ISipTransportRuntime
{
    public bool IsDisposed { get; private set; }

    public IPEndPoint LocalEndPoint => inner.LocalEndPoint;

    public IDisposable SubscribeRequests(Action<IPEndPoint, SipRequest> handler) => inner.SubscribeRequests(handler);

    public IDisposable SubscribeResponses(Action<IPEndPoint, SipResponse> handler) => inner.SubscribeResponses(handler);

    public Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default) =>
        inner.SendRequestAsync(method, requestUri, headers, body, remoteEndPoint, ct);

    public Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default) =>
        inner.SendResponseAsync(statusCode, reasonPhrase, headers, body, remoteEndPoint, ct);

    public Task<IPEndPoint> ResolveRemoteEndPointAsync(string host, int port, CancellationToken ct = default) =>
        inner.ResolveRemoteEndPointAsync(host, port, ct);

    public void Dispose()
    {
        IsDisposed = true;
        inner.Dispose();
    }
}
