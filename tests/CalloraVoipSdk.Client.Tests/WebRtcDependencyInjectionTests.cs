using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The WebRTC facade's DI entrypoint (ADR-012, step 3): <see cref="WebRtcServiceCollectionExtensions.AddCalloraWebRtc"/>
/// registers a usable <see cref="IWebRtcClient"/> from mutable <see cref="WebRtcOptions"/>, mirroring the SIP
/// <c>AddCalloraVoip</c>. The two facades <em>compose</em> (<c>AddCalloraVoip(...).AddWebRtc(...)</c>) rather than
/// sharing one configuration object. Exercised through the public surface only.
/// </summary>
public sealed class WebRtcDependencyInjectionTests
{
    [Fact]
    public async Task AddCalloraWebRtc_registers_a_client_that_creates_peers()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc();

        using var provider = services.BuildServiceProvider();
        var rtc = provider.GetRequiredService<IWebRtcClient>();
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        Assert.Contains("a=fingerprint:", offer, StringComparison.Ordinal);
        Assert.Contains("UDP/TLS/RTP/SAVPF", offer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Options_flow_from_the_container_to_the_peer()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc(options => options.AudioCodecs = ["PCMU"]);

        using var provider = services.BuildServiceProvider();
        var rtc = provider.GetRequiredService<IWebRtcClient>();
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        Assert.Contains("PCMU", offer, StringComparison.Ordinal);   // configured codec reached the wire
        Assert.DoesNotContain("opus", offer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithVideo_builder_override_enables_a_video_track()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc().WithVideo("VP8");

        using var provider = services.BuildServiceProvider();
        var rtc = provider.GetRequiredService<IWebRtcClient>();
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        Assert.Contains("m=video", offer, StringComparison.Ordinal);
        Assert.Contains("VP8", offer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddCalloraVoip_then_AddWebRtc_composes_both_facades()
    {
        var services = new ServiceCollection();
        services
            .AddCalloraVoip(options =>
            {
                options.UserAgent = "CalloraVoipSdk.Client.Tests/1.0";
                options.EnableAutomaticAudioDeviceSelection = false;
            })
            .AddWebRtc();

        using var provider = services.BuildServiceProvider();
        using var voip = provider.GetRequiredService<IVoipClient>();
        var rtc = provider.GetRequiredService<IWebRtcClient>();
        await using var peer = rtc.CreatePeer();

        Assert.NotNull(voip);                                        // SIP facade present
        Assert.Contains("a=fingerprint:", peer.CreateOffer(), StringComparison.Ordinal);   // WebRTC facade usable
    }

    [Fact]
    public void An_unknown_codec_configured_via_options_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc(options => options.AudioCodecs = ["nope"]);

        using var provider = services.BuildServiceProvider();
        var rtc = provider.GetRequiredService<IWebRtcClient>();

        Assert.Throws<ArgumentException>(() => rtc.CreatePeer());
    }
}
