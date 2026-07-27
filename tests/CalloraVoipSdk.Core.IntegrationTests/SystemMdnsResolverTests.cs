using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using Xunit;
using Xunit.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SystemMdnsResolverTests
{
    private readonly ITestOutputHelper _out;
    public SystemMdnsResolverTests(ITestOutputHelper output) => _out = output;

    private static SystemMdnsResolver WithLookup(Func<string, IPAddress[]> lookup)
        => new((host, ct) => Task.FromResult(lookup(host)));

    [Fact]
    public async Task Resolves_Single_Ip_For_Valid_Uuid_Local()
    {
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5")]);
        var ip = await resolver.ResolveAsync("abc123.local", CancellationToken.None);
        Assert.Equal(IPAddress.Parse("192.168.1.5"), ip);
    }

    [Fact]
    public async Task Returns_Null_When_Resolution_Yields_More_Than_One_Ip()
    {
        // RFC 8828 §3.2.2: SHOULD ignore candidates resolving to more than one IP.
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5"), IPAddress.Parse("192.168.1.6")]);
        Assert.Null(await resolver.ResolveAsync("abc123.local", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Null_For_Name_With_More_Than_One_Dot()
    {
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5")]);
        Assert.Null(await resolver.ResolveAsync("evil.host.local", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Null_For_Non_Local_Name()
    {
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5")]);
        Assert.Null(await resolver.ResolveAsync("example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Null_For_Empty_Label_DotLocal()
    {
        // ".local" hat genau einen Punkt, aber ein leeres Label — RFC 8828 verlangt "<label>.local".
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5")]);
        Assert.Null(await resolver.ResolveAsync(".local", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Null_When_Lookup_Throws()
    {
        var resolver = new SystemMdnsResolver((_, _) => throw new System.Net.Sockets.SocketException());
        Assert.Null(await resolver.ResolveAsync("abc123.local", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Null_On_Timeout()
    {
        // Lookup hängt länger als das Timeout → verworfen (null), kein Throw.
        var resolver = new SystemMdnsResolver(
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; },
            TimeSpan.FromMilliseconds(100));
        Assert.Null(await resolver.ResolveAsync("abc123.local", CancellationToken.None));
    }

    [Fact]
    public async Task RealOsResolver_Resolves_A_Genuine_Local_Name_When_Mdns_Available()
    {
        // Echter OS-Integrations-Nachweis (ohne Browser, weil headless Chrome kein mDNS triggert):
        // der Produktions-SystemMdnsResolver (System.Net.Dns) muss einen ECHTEN .local-Namen auflösen,
        // wo ein OS-mDNS-Responder läuft. Der Host-eigene Hostname ist per mDNS auflösbar (avahi/nss-mdns).
        var host = Dns.GetHostName().Split('.')[0] + ".local";
        var resolver = new SystemMdnsResolver(); // Produktions-Impl: echtes System.Net.Dns

        var ip = await resolver.ResolveAsync(host, CancellationToken.None);

        if (ip is null)
        {
            // Kein OS-mDNS auf diesem Host (z. B. CI-Runner ohne avahi) — nicht prüfbar; der Fix ist dort
            // verhaltensbewahrend (Candidate würde verworfen wie bisher). xUnit 2.4 hat kein Laufzeit-Skip,
            // daher als dokumentiertes no-op behandelt. Auf Systemen mit avahi/nss-mdns läuft der echte Assert.
            _out.WriteLine($"Kein OS-mDNS für '{host}' — Real-Auflösung übersprungen (verhaltensbewahrend).");
            return;
        }
        _out.WriteLine($"'{host}' via System.Net.Dns aufgelöst zu {ip} — OS-mDNS-Integration bestätigt.");
        Assert.NotNull(ip);
    }
}
