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
}
