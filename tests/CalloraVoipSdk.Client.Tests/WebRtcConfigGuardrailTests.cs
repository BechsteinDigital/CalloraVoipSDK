using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Guardrails on the WebRTC config surface. A TURN entry may use any transport — UDP (media-socket relay) or
/// TCP/TLS (a stream relay on its own connection, ADR-073) — so every door (the builder, direct construction,
/// and the IOptions path) accepts it; the collection snapshotting (#166 P2-7) still holds.
/// </summary>
public sealed class WebRtcConfigGuardrailTests
{
    [Fact]
    public void WithTurnServer_accepts_a_tcp_tls_transport()
    {
        var builder = new ServiceCollection().AddCalloraWebRtc();

        // TCP/TLS TURN is a stream relay (ADR-073) — accepted, not rejected.
        builder.WithTurnServer("turn.example.com", "user", "pass", transport: IceTransport.Tcp);
        builder.WithTurnServer("turn.example.com", "user", "pass", port: 5349, transport: IceTransport.Tls);
    }

    [Fact]
    public void WithTurnServer_accepts_udp()
    {
        var builder = new ServiceCollection().AddCalloraWebRtc();

        // The default (UDP) path stays valid — no throw.
        builder.WithTurnServer("turn.example.com", "user", "pass");
    }

    [Fact]
    public void WithIceServers_accepts_a_tcp_tls_turn_entry()
    {
        var builder = new ServiceCollection().AddCalloraWebRtc();

        builder.WithIceServers(new IceServerConfiguration
        {
            Type = IceServerType.Turn,
            Host = "turn.example.com",
            Transport = IceTransport.Tls,
            Username = "user",
            Password = "pass",
        });
    }

    [Fact]
    public void Direct_construction_accepts_a_tcp_tls_turn_entry()
    {
        var config = new WebRtcConfiguration { IceServers = [NonUdpTurn()] };

        Assert.Single(config.IceServers);
        Assert.Equal(IceTransport.Tls, config.IceServers[0].Transport);
    }

    [Fact]
    public void The_options_path_accepts_a_tcp_tls_turn_entry()
    {
        var options = new WebRtcOptions { IceServers = [NonUdpTurn()] };

        // Startup validation (ValidateOnStart) passes it...
        var result = new WebRtcOptionsValidator().Validate(name: null, options);
        Assert.True(result.Succeeded);

        // ...and the mapping onto the configuration accepts it too.
        var config = options.ToConfiguration(loggerFactory: null);
        Assert.Single(config.IceServers);
        Assert.Equal(IceTransport.Tls, config.IceServers[0].Transport);
    }

    /// <summary>
    /// #166 P2-7: WebRtcConfiguration is documented as immutable but stored the caller's list reference, so a
    /// caller — or the mutable WebRtcOptions instance the DI path maps from — kept a live handle into a
    /// running client's configuration. Every collection property now snapshots what it is given.
    /// </summary>
    [Fact]
    public void The_configuration_snapshots_the_lists_it_is_given()
    {
        var audioCodecs = new List<string> { "opus" };
        var videoCodecs = new List<string> { "H264" };
        var simulcastLayers = new List<string> { "hi" };
        var iceServers = new List<IceServerConfiguration>
        {
            new() { Type = IceServerType.Stun, Host = "stun.example.com" },
        };

        var config = new WebRtcConfiguration
        {
            AudioCodecs = audioCodecs,
            VideoCodecs = videoCodecs,
            SimulcastLayers = simulcastLayers,
            IceServers = iceServers,
        };

        audioCodecs.Add("PCMU");
        videoCodecs.Add("VP8");
        simulcastLayers.Add("lo");
        iceServers.Add(NonUdpTurn());

        Assert.Equal(["opus"], config.AudioCodecs);
        Assert.Equal(["H264"], config.VideoCodecs);
        Assert.Equal(["hi"], config.SimulcastLayers);
        Assert.Single(config.IceServers);
    }

    [Fact]
    public void The_options_mapping_does_not_hand_the_mutable_options_list_to_the_configuration()
    {
        var iceServers = new List<IceServerConfiguration>
        {
            new() { Type = IceServerType.Stun, Host = "stun.example.com" },
        };
        var options = new WebRtcOptions { IceServers = iceServers };

        var config = options.ToConfiguration(loggerFactory: null);
        iceServers.Add(NonUdpTurn());

        Assert.Single(config.IceServers);
    }

    private static IceServerConfiguration NonUdpTurn() => new()
    {
        Type = IceServerType.Turn,
        Host = "turn.example.com",
        Transport = IceTransport.Tls,
        Username = "user",
        Password = "pass",
    };
}
