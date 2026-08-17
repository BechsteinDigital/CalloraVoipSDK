using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Publications;
using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.Modules;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P2-8: overriding a public facade interface in the container must actually work. Both
/// registration helpers also registered a concrete alias built from a hard cast of the interface
/// registration — <c>(VoipClient)sp.GetRequiredService&lt;IVoipClient&gt;()</c> — so a host that pre-registered
/// a fake got an <see cref="InvalidCastException"/> out of the container, and the documented mockability of
/// <see cref="IVoipClient"/>/<see cref="IWebRtcClient"/> did not hold. The alias is now only registered while
/// the SDK owns the interface registration, and reports an actionable error when it cannot be satisfied.
/// </summary>
public sealed class FacadeOverrideMockabilityTests
{
    [Fact]
    public void A_pre_registered_fake_voip_client_survives_AddCalloraVoip()
    {
        var fake = new FakeVoipClient();
        var services = new ServiceCollection();
        services.AddSingleton<IVoipClient>(fake);

        services.AddCalloraVoip();

        using var provider = services.BuildServiceProvider();
        Assert.Same(fake, provider.GetRequiredService<IVoipClient>());
        // No concrete alias is registered on top of a foreign implementation — there is no concrete client.
        Assert.Null(provider.GetService<VoipClient>());
    }

    [Fact]
    public async Task The_hosted_lifecycle_runs_against_a_faked_client()
    {
        var fake = new FakeVoipClient();
        var services = new ServiceCollection();
        services.AddSingleton<IVoipClient>(fake);
        services.AddCalloraVoip();

        using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Assert.True(fake.Disposed);
    }

    [Fact]
    public void A_pre_registered_fake_webrtc_client_survives_AddCalloraWebRtc()
    {
        var fake = new FakeWebRtcClient();
        var services = new ServiceCollection();
        services.AddSingleton<IWebRtcClient>(fake);

        services.AddCalloraWebRtc();

        using var provider = services.BuildServiceProvider();
        Assert.Same(fake, provider.GetRequiredService<IWebRtcClient>());
        Assert.Null(provider.GetService<WebRtcClient>());
    }

    /// <summary>
    /// An override registered AFTER the SDK's registration still wins for the interface (last one wins), so the
    /// alias cannot be satisfied. It must say why instead of surfacing a bare cast failure.
    /// </summary>
    [Fact]
    public void A_late_override_makes_the_concrete_alias_report_an_actionable_error()
    {
        var services = new ServiceCollection();
        services.AddCalloraVoip();
        services.AddSingleton<IVoipClient>(new FakeVoipClient());

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<VoipClient>());

        Assert.IsNotType<InvalidCastException>(ex);
        Assert.Contains(nameof(FakeVoipClient), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IVoipClient), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sdk_owned_registration_still_exposes_the_concrete_client()
    {
        var services = new ServiceCollection();
        services.AddCalloraVoip();

        using var provider = services.BuildServiceProvider();
        var viaInterface = provider.GetRequiredService<IVoipClient>();
        var viaConcrete = provider.GetRequiredService<VoipClient>();

        Assert.Same(viaInterface, viaConcrete);
    }

    // A minimal stand-in for the public client contract: every member a test does not touch throws, so an
    // accidental SDK dependency on the concrete type shows up as a failure rather than passing silently.
    private sealed class FakeVoipClient : IVoipClient
    {
        public bool Disposed { get; private set; }

        public ICallManager Calls => throw new NotSupportedException();
        public IPhoneLineManager Lines => throw new NotSupportedException();
        public IMediaManager Media => throw new NotSupportedException();
        public IPlaybackModule PlaybackManager => throw new NotSupportedException();
        public IRecordingModule RecordingManager => throw new NotSupportedException();
        public IModuleManager ModuleManager => throw new NotSupportedException();
        public IModuleRegistry Modules => throw new NotSupportedException();
        public ISessionManager SessionManager => throw new NotSupportedException();
        public IDeviceManager DeviceManager => throw new NotSupportedException();
        public IQualityManager QualityManager => throw new NotSupportedException();
        public IPolicyManager PolicyManager => throw new NotSupportedException();
        public ITelemetryManager TelemetryManager => throw new NotSupportedException();

        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<IncomingMessageEventArgs>? IncomingMessage { add { } remove { } }
        public event EventHandler<CallStateChangedEventArgs>? CallStateChanged { add { } remove { } }

        [Obsolete("Mirrors the deprecated facade member so the fake satisfies the contract.")]
        public Task<ConnectResult> RegisterAndWaitAsync(SipAccount account, ConnectOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ConnectResult> ConnectAsync(SipAccount account, ConnectOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DialResult> DialAndWaitUntilConnectedAsync(IPhoneLine line, string targetUri, DialWaitOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendMessageAsync(string targetUri, string body, string contentType = "text/plain", CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PublishResult> RefreshPublicationAsync(string eventType, string etag, int expiresSeconds = 3600, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PublishResult> ModifyPublicationAsync(string eventType, string etag, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RemovePublicationAsync(string eventType, string etag, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AttachDefaultAudioAsync(ICall call, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DetachDefaultAudioAsync(ICall call, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AttachDefaultVideoAsync(ICall call, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DetachDefaultVideoAsync(ICall call, CancellationToken ct = default) => throw new NotSupportedException();

        public IReadOnlyList<AudioDeviceDescriptor> GetAvailableInputAudioDevices() => throw new NotSupportedException();
        public IReadOnlyList<AudioDeviceDescriptor> GetAvailableOutputAudioDevices() => throw new NotSupportedException();
        public AudioDeviceRuntimeSnapshot GetAudioDeviceRuntimeSnapshot() => throw new NotSupportedException();
        public void SwitchAudioInputDevice(string? deviceId) => throw new NotSupportedException();
        public void SwitchAudioOutputDevice(string? deviceId) => throw new NotSupportedException();
        public void SetAudioInputVolume(float volume) => throw new NotSupportedException();
        public void SetAudioOutputVolume(float volume) => throw new NotSupportedException();
        public void SetAudioInputMuted(bool isMuted) => throw new NotSupportedException();
        public void SetAudioOutputMuted(bool isMuted) => throw new NotSupportedException();
        public void UpdateAudioFormat(AudioDeviceFormat format) => throw new NotSupportedException();
        public IDisposable OnIncomingCall(Func<ICall, Task> handler) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeWebRtcClient : IWebRtcClient
    {
        public IPeerConnection CreatePeer() => throw new NotSupportedException();
        public IPeerConnectionManager Peers => throw new NotSupportedException();
        public IWebRtcModuleRegistry Modules => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
