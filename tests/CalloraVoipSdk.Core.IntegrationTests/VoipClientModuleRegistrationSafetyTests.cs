using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.Modules;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the A2 follow-up: a module throwing during OnAttached must not leak
/// already constructed runtime resources out of a failed VoipClient constructor.
/// Issue #18.1 widens this to <em>any</em> mid-constructor failure — not only the last (module) step —
/// so an early throw after the transport is built still disposes it.
/// </summary>
public sealed class VoipClientModuleRegistrationSafetyTests
{
    [Fact]
    public void Throwing_module_surfaces_error_and_disposes_transport_runtime()
    {
        var factory = new RecordingTransportFactory();

        var services = new ServiceCollection();
        services.AddCalloraVoip(options =>
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

    // #18.1: the DTLS-identity step runs after the transport is bound but well before module registration —
    // the stage the pre-fix inner try/catch never covered. A non-ECDSA certificate makes DtlsCertificate.FromX509
    // throw there, so the transport socket would leak unless the whole constructor is guarded. Asserting the
    // transport was disposed pins that the guard now covers an early-stage failure, not just the module step.
    [Fact]
    public void Early_constructor_failure_after_transport_build_disposes_transport_runtime()
    {
        var factory = new RecordingTransportFactory();
        using var rsaCertificate = CreateRsaCertificate(); // not ECDSA → FromX509 rejects it

        var services = new ServiceCollection();
        services.AddCalloraVoip(options =>
        {
            options.UserAgent = "CalloraVoipSdk.Core.IntegrationTests/1.0";
            options.EnableAutomaticAudioDeviceSelection = false;
            options.DtlsCertificate = rsaCertificate;
        });
        services.AddSingleton<ISipTransportFactory>(factory);

        using var provider = services.BuildServiceProvider();

        // FromX509 throws ArgumentException for a non-ECDSA certificate mid-constructor.
        Assert.Throws<ArgumentException>(() => { _ = provider.GetRequiredService<IVoipClient>(); });

        Assert.NotNull(factory.CreatedRuntime);
        Assert.True(factory.CreatedRuntime!.IsDisposed);
    }

    private static X509Certificate2 CreateRsaCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=callora-test-rsa", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
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

    public SipTransportProtocol? LastDefaultTransport { get; private set; }

    public ISipTransportRuntime Create(
        TlsConfiguration? tls,
        ILoggerFactory loggerFactory,
        SipTransportProtocol defaultTransport = SipTransportProtocol.Udp)
    {
        LastDefaultTransport = defaultTransport;
        CreatedRuntime = new RecordingTransportRuntime(
            new SipTransportFactory().Create(tls, loggerFactory, defaultTransport));
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

    public Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct = default) =>
        inner.SendRequestAsync(method, requestUri, headers, body, remoteEndPoint, transport, ct);

    public Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default) =>
        inner.SendResponseAsync(statusCode, reasonPhrase, headers, body, remoteEndPoint, ct);

    public Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct = default) =>
        inner.SendResponseAsync(statusCode, reasonPhrase, headers, body, remoteEndPoint, transport, ct);

    public Task<IPEndPoint> ResolveRemoteEndPointAsync(string host, int port, CancellationToken ct = default) =>
        inner.ResolveRemoteEndPointAsync(host, port, ct);

    public Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        CancellationToken ct = default) =>
        inner.ResolveRemoteEndPointAsync(host, port, transport, ct);

    public IPEndPoint GetLocalEndPoint(SipTransportProtocol transport) => inner.GetLocalEndPoint(transport);

    public void Dispose()
    {
        IsDisposed = true;
        inner.Dispose();
    }
}
