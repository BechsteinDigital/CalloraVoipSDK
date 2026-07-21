using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.Asterisk;

/// <summary>
/// Startet einen Asterisk-Container (PJSIP) mit einer minimalen REGISTER-Konfiguration und
/// exponiert den gemappten SIP/UDP-Port. Nur für Interop-Tests.
/// </summary>
public sealed class AsteriskContainer : IAsyncDisposable
{
    private const string SipPortWithProtocol = "5060/udp";

    private const string PjsipConf = """
        [transport-udp]
        type=transport
        protocol=udp
        bind=0.0.0.0:5060

        [6001]
        type=endpoint
        context=default
        disallow=all
        allow=ulaw
        auth=6001
        aors=6001

        [6001]
        type=auth
        auth_type=userpass
        username=6001
        password=secret

        [6001]
        type=aor
        max_contacts=1
        """;

    private readonly IContainer _container;

    /// <summary>Erstellt (noch nicht gestartet) den Asterisk-Container.</summary>
    public AsteriskContainer()
    {
        _container = new ContainerBuilder("andrius/asterisk:22")
            .WithResourceMapping(Encoding.UTF8.GetBytes(PjsipConf), "/etc/asterisk/pjsip.conf")
            .WithExposedPort(SipPortWithProtocol)
            .WithPortBinding(SipPortWithProtocol, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Asterisk Ready."))
            .Build();
    }

    /// <summary>SIP-Account-Benutzername des konfigurierten Endpoints.</summary>
    public string Username => "6001";

    /// <summary>Passwort des konfigurierten Endpoints (Digest-Auth).</summary>
    public string Password => "secret";

    /// <summary>Docker-Host (meist 127.0.0.1/localhost).</summary>
    public string Host => _container.Hostname;

    /// <summary>Auf den Host gemappter SIP/UDP-Port.</summary>
    public ushort SipUdpPort => _container.GetMappedPublicPort(SipPortWithProtocol);

    /// <summary>Startet den Container und wartet, bis Asterisk SIP-ready ist.</summary>
    public Task StartAsync() => _container.StartAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
