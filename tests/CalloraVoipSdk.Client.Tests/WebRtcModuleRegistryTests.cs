using CalloraVoipSdk.Modules;
using CalloraVoipSdk.WebRtc;
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

    private interface IFakeWebRtcFeature;

    private sealed class FakeWebRtcModule : IWebRtcClientModule, IFakeWebRtcFeature
    {
        public string ModuleId => "fake-webrtc-feature";
        public IWebRtcClient? AttachedClient { get; private set; }
        public void OnAttached(IWebRtcClient client) => AttachedClient = client;
    }
}
