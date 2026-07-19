using System.Net;
using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The TURN/STUN server-hosting facade (server-side counterpart to the client facades): <c>AddCalloraTurnServer</c>
/// registers a resolvable <see cref="ITurnServerHost"/> from mutable options, the builder configures realm and
/// credentials fluently, and authentication misconfiguration fails fast. Exercised through the public surface.
/// </summary>
public sealed class TurnServerHostTests
{
    [Fact]
    public async Task AddCalloraTurnServer_resolves_a_bound_host()
    {
        var services = new ServiceCollection();
        services.AddCalloraTurnServer(o => o.BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .WithoutAuthentication();

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<ITurnServerHost>();

        Assert.Equal(IPAddress.Loopback, host.LocalEndPoint.Address);
        Assert.NotEqual(0, host.LocalEndPoint.Port); // the ephemeral :0 bind resolved a real port
    }

    [Fact]
    public async Task The_builder_configures_realm_and_credentials_for_the_auth_path()
    {
        var services = new ServiceCollection();
        services.AddCalloraTurnServer(o => o.BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .WithRealm("callora.example")
            .WithCredential("user", "secret");

        await using var provider = services.BuildServiceProvider();

        // Resolving builds the host on the authenticated path (realm + credential present) without throwing.
        var host = provider.GetRequiredService<ITurnServerHost>();
        Assert.NotEqual(0, host.LocalEndPoint.Port);
    }

    [Fact]
    public void Authentication_without_a_realm_or_credentials_fails_fast()
    {
        // RequireAuthentication defaults true; a host with no realm/credentials must not be constructible.
        var config = new TurnServerHostConfiguration { BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0) };
        Assert.Throws<ArgumentException>(() => new TurnServerHost(config));
    }

    [Fact]
    public void A_tls_transport_without_a_certificate_fails_fast()
    {
        var config = new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = IceTransport.Tls,
            RequireAuthentication = false,
        };
        Assert.Throws<ArgumentException>(() => new TurnServerHost(config));
    }

    [Fact]
    public async Task AddCalloraStunServer_resolves_a_bound_host()
    {
        var services = new ServiceCollection();
        services.AddCalloraStunServer(o => o.BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IStunServerHost>();

        Assert.NotEqual(0, host.LocalEndPoint.Port); // the ephemeral :0 bind resolved a real port
    }
}
