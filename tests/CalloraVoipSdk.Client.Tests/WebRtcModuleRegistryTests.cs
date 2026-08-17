using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.Modules;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The WebRTC facade module registry (ADR-012 step 6, L3 plugin seam): <see cref="IWebRtcClient.Modules"/>
/// resolves programmatically registered <see cref="IWebRtcClientModule"/>s by feature contract, mirroring
/// the SIP module registry. Exercised through the public surface.
/// </summary>
public sealed class WebRtcModuleRegistryTests
{
    [Fact]
    public void Get_returns_a_module_registered_programmatically()
    {
        var rtc = new WebRtcClient();
        var module = new FakeWebRtcModule();

        rtc.Modules.Register(module);

        Assert.Same(module, rtc.Modules.Get<IFakeWebRtcFeature>());
    }

    [Fact]
    public void Get_throws_documented_exception_when_module_missing()
    {
        var rtc = new WebRtcClient();

        Assert.Throws<ModuleFeatureUnavailableException>(() => rtc.Modules.Get<IFakeWebRtcFeature>());
    }

    [Fact]
    public void TryGet_returns_false_when_module_missing()
    {
        var rtc = new WebRtcClient();

        Assert.False(rtc.Modules.TryGet<IFakeWebRtcFeature>(out var module));
        Assert.Null(module);
    }

    [Fact]
    public void Register_attaches_the_owning_client_to_the_module()
    {
        var rtc = new WebRtcClient();
        var module = new FakeWebRtcModule();

        rtc.Modules.Register(module);

        Assert.Same(rtc, module.AttachedClient);
    }

    [Fact]
    public void Register_rejects_null_module()
    {
        var rtc = new WebRtcClient();

        Assert.Throws<ArgumentNullException>(() => rtc.Modules.Register(null!));
    }

    /// <summary>
    /// #166 P3-13: registration must close with the owner. Attaching to a disposed client would hand the module
    /// a client that can no longer create peers, and leave it registered in a dead owner.
    /// </summary>
    [Fact]
    public async Task Register_after_the_owning_client_was_disposed_is_refused()
    {
        var rtc = new WebRtcClient();
        var registry = rtc.Modules;
        await rtc.DisposeAsync();

        var module = new FakeWebRtcModule();
        Assert.Throws<ObjectDisposedException>(() => registry.Register(module));
        Assert.Null(module.AttachedClient);
        Assert.False(registry.TryGet<IFakeWebRtcFeature>(out _));
    }

    [Fact]
    public async Task Modules_registered_before_disposal_stay_resolvable_during_the_teardown()
    {
        var rtc = new WebRtcClient();
        var module = new FakeWebRtcModule();
        rtc.Modules.Register(module);

        await rtc.DisposeAsync();

        Assert.Same(module, rtc.Modules.Get<IFakeWebRtcFeature>());
    }

    [Fact]
    public async Task DI_registered_modules_are_auto_attached_to_the_client()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc();
        services.AddSingleton<IWebRtcClientModule, FakeWebRtcModule>();

        // await using: the WebRtcClient singleton is async-disposable only (peers dispose asynchronously),
        // so the container must be torn down on the async path.
        await using var provider = services.BuildServiceProvider();
        var rtc = provider.GetRequiredService<IWebRtcClient>();

        Assert.True(rtc.Modules.TryGet<IFakeWebRtcFeature>(out var feature));
        Assert.NotNull(feature);
    }

    private interface IFakeWebRtcFeature;

    private sealed class FakeWebRtcModule : IWebRtcClientModule, IFakeWebRtcFeature
    {
        public string ModuleId => "fake-webrtc-feature";
        public IWebRtcClient? AttachedClient { get; private set; }
        public void OnAttached(IWebRtcClient client) => AttachedClient = client;
    }
}
