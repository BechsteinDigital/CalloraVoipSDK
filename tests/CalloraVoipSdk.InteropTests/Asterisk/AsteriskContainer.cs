using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.Asterisk;

/// <summary>
/// Startet einen Asterisk-Container (PJSIP, andrius/asterisk:22) mit einer minimalen
/// REGISTER-Konfiguration und exponiert den gemappten SIP/UDP-Port. Nur für Interop-Tests.
/// </summary>
public sealed class AsteriskContainer : IAsyncDisposable
{
    private const string SipPortWithProtocol = "5060/udp";

    // Minimale PJSIP-Konfiguration: UDP-Transport + Endpoint 6001 mit Digest-Auth.
    // WICHTIG: Kein führendes Leerzeichen / keine Einrückung — Asterisk config-Parser
    // erwartet Einträge am Zeilenanfang.
    private const string PjsipConf =
        "[transport-udp]\n" +
        "type=transport\n" +
        "protocol=udp\n" +
        "bind=0.0.0.0:5060\n" +
        "\n" +
        "[6001]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw\n" +
        "auth=6001\n" +
        "aors=6001\n" +
        "\n" +
        "[6001]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6001\n" +
        "password=secret\n" +
        "\n" +
        "[6001]\n" +
        "type=aor\n" +
        "max_contacts=1\n";

    private readonly IContainer _container;
    private readonly FileInfo _pjsipConfFile;

    /// <summary>Erstellt (noch nicht gestartet) den Asterisk-Container.</summary>
    public AsteriskContainer()
    {
        // Schreibe pjsip.conf in eine temporäre Datei, damit Testcontainers sie als
        // reguläre Datei (nicht als Byte-Array-Artefakt) ins Container-Dateisystem kopiert.
        _pjsipConfFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_pjsipConfFile.FullName, PjsipConf);

        _container = new ContainerBuilder("andrius/asterisk:22")
            .WithResourceMapping(_pjsipConfFile, new FileInfo("/etc/asterisk/pjsip.conf"))
            .WithExposedPort(SipPortWithProtocol)
            .WithPortBinding(SipPortWithProtocol, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Asterisk Ready."))
            .Build();
    }

    /// <summary>SIP-Account-Benutzername des konfigurierten Endpoints.</summary>
    public string Username => "6001";

    /// <summary>Passwort des konfigurierten Endpoints (Digest-Auth).</summary>
    public string Password => "secret";

    /// <summary>
    /// Docker-Host (meist 127.0.0.1/localhost) für den Port-gemappten UDP-Zugang.
    /// </summary>
    public string Host => _container.Hostname;

    /// <summary>Auf den Host gemappter SIP/UDP-Port.</summary>
    public ushort SipUdpPort => _container.GetMappedPublicPort(SipPortWithProtocol);

    /// <summary>
    /// Interne Docker-Bridge-IP des Containers — für direkten Zugriff ohne NAT/Port-Mapping.
    /// Nur nach <see cref="StartAsync"/> gültig.
    /// </summary>
    public string ContainerIpAddress => _container.IpAddress;

    /// <summary>Startet den Container und wartet, bis Asterisk SIP-ready ist.</summary>
    public Task StartAsync() => _container.StartAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
        try { _pjsipConfFile.Delete(); } catch { /* best effort */ }
    }
}
