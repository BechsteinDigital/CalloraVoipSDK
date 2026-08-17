using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// GA guardrails on the WebRTC config surface: a TCP/TLS TURN entry is a silent trap (only UDP TURN gathers a
/// relay candidate today), so the builder rejects it up front instead of accepting it and gathering nothing.
/// </summary>
public sealed class WebRtcConfigGuardrailTests
{
    [Fact]
    public void WithTurnServer_rejects_a_non_udp_transport()
    {
        var builder = new ServiceCollection().AddCalloraWebRtc();

        var ex = Assert.Throws<ArgumentException>(
            () => builder.WithTurnServer("turn.example.com", "user", "pass", transport: IceTransport.Tcp));
        Assert.Contains("UDP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTurnServer_accepts_udp()
    {
        var builder = new ServiceCollection().AddCalloraWebRtc();

        // The default (UDP) path stays valid — no throw.
        builder.WithTurnServer("turn.example.com", "user", "pass");
    }

    [Fact]
    public void WithIceServers_rejects_a_non_udp_turn_entry()
    {
        var builder = new ServiceCollection().AddCalloraWebRtc();

        Assert.Throws<ArgumentException>(() => builder.WithIceServers(
            new IceServerConfiguration
            {
                Type = IceServerType.Turn,
                Host = "turn.example.com",
                Transport = IceTransport.Tls,
                Username = "user",
                Password = "pass",
            }));
    }

    /// <summary>
    /// #166 P2-7: the builder was the ONLY door that rejected a TCP/TLS TURN entry. Direct construction and
    /// the IOptions path accepted it, and since only UDP TURN gets an allocation probe the result was a client
    /// that gathered no relay candidate at all, silently. All three doors now agree.
    /// </summary>
    [Fact]
    public void Direct_construction_rejects_a_non_udp_turn_entry()
    {
        var ex = Assert.Throws<ArgumentException>(() => new WebRtcConfiguration
        {
            IceServers = [NonUdpTurn()],
        });

        Assert.Contains("UDP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_options_path_rejects_a_non_udp_turn_entry()
    {
        var options = new WebRtcOptions { IceServers = [NonUdpTurn()] };

        // Startup validation (ValidateOnStart) reports it...
        var result = new WebRtcOptionsValidator().Validate(name: null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("UDP", StringComparison.Ordinal));

        // ...and the mapping onto the configuration refuses it too, so a host that bypasses validation
        // cannot build a client whose relay silently never happens.
        Assert.Throws<ArgumentException>(() => options.ToConfiguration(loggerFactory: null));
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
