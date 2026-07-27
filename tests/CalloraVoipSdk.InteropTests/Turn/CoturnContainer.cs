using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.Turn;

/// <summary>
/// Startet einen echten coturn-TURN-Server (<c>coturn/coturn:latest</c>) mit Long-Term-Credential-
/// Mechanismus (RFC 5389 §10.2) für die TURN-Relay-E2E-Tests. Anders als der in-process-<c>TurnServer</c>-Fake
/// spricht dieser Container das echte coturn-Wire-Protokoll gegen den SDK-TURN-Client.
/// <para>
/// <b>Networking:</b> Der Container läuft im <c>--network host</c>-Modus (Linux-only, wie der Asterisk-
/// Browser-Safe-Pfad). Nur so ist die von coturn vergebene Relay-Adresse (eine reale, geroutete
/// Host-IP, kein NAT-isoliertes Container-interne Adresse) vom Host aus erreichbar — die Voraussetzung
/// dafür, dass ein zweiter (Offerer-)Client Datagramme an die Relay-Adresse des ersten (Answerer-)Clients
/// senden kann und coturn sie zurück durch die Allocation weiterleitet. Relay-/External-IP und die
/// Peer-Whitelist werden auf die zur Laufzeit erkannte primäre Host-IP gepinnt, damit coturn dieselbe
/// (nicht per Default gesperrte) IP als Relay-Adresse annonciert und Permissions dafür zulässt.
/// </para>
/// </summary>
public sealed class CoturnContainer : IAsyncDisposable
{
    private const int TurnPort = 3478;

    // coturn belegt Relay-Ports dynamisch aus diesem Bereich (RFC 8656 §7). Ein enger, fester Bereich
    // hält die Host-Netz-Belegung überschaubar und deterministisch.
    private const int RelayPortMin = 49160;
    private const int RelayPortMax = 49200;

    /// <summary>Long-Term-Credential-Benutzername (muss zur <c>user=</c>-Zeile der Config passen).</summary>
    public const string TurnUsername = "testuser";

    /// <summary>Long-Term-Credential-Passwort.</summary>
    public const string TurnPassword = "testpassword";

    /// <summary>Authentifizierungs-Realm (muss zur <c>realm=</c>-Zeile der Config passen).</summary>
    public const string TurnRealm = "test.callora.local";

    private readonly IContainer _container;
    private readonly FileInfo _confFile;
    private readonly string _hostIp;
    private CoturnHostNetworkLease? _lease;

    /// <summary>Erstellt (noch nicht gestartet) den coturn-Container.</summary>
    public CoturnContainer()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Host-Networking (nötig für die Relay-Erreichbarkeit) unterstützt Testcontainers nur unter Linux.
            throw new PlatformNotSupportedException(
                "Der coturn-Relay-E2E-Test benötigt Docker-Host-Networking und läuft nur unter Linux.");
        }

        _hostIp = DetectPrimaryHostIp();

        // coturn liest per Default-Entrypoint /etc/coturn/turnserver.conf. relay-ip/external-ip auf die
        // erkannte Host-IP gepinnt: so annonciert coturn eine host-erreichbare, nicht per Default gesperrte
        // Relay-Adresse, und allowed-peer-ip lässt Permissions für genau diese IP zu (sonst 403 Forbidden IP).
        var conf =
            $"listening-port={TurnPort}\n" +
            "listening-ip=0.0.0.0\n" +
            $"relay-ip={_hostIp}\n" +
            $"external-ip={_hostIp}\n" +
            $"realm={TurnRealm}\n" +
            $"user={TurnUsername}:{TurnPassword}\n" +
            "lt-cred-mech\n" +          // Long-Term-Credential-Mechanismus (401-Challenge → MESSAGE-INTEGRITY)
            "no-tls\n" +
            "no-dtls\n" +
            "no-stun\n" +              // reiner TURN-Server (kein STUN-Binding), analog Produktions-Deployment
            $"min-port={RelayPortMin}\n" +
            $"max-port={RelayPortMax}\n" +
            $"allowed-peer-ip={_hostIp}\n" +
            "verbose\n";

        _confFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_confFile.FullName, conf);

        _container = new ContainerBuilder("coturn/coturn:latest")
            .WithResourceMapping(_confFile, new FileInfo("/etc/coturn/turnserver.conf"))
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                parameters.HostConfig.NetworkMode = "host";
            })
            // coturn protokolliert diese Zeile, sobald der UDP-Listener (der Transport, den die Tests nutzen)
            // gebunden ist und die Relay-Ports initialisiert sind — der zuverlässige SIP-/TURN-Ready-Marker.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("UDP listener opened on"))
            .Build();
    }

    /// <summary>Die zur Laufzeit erkannte primäre Host-IP, die coturn als Relay-Adresse annonciert.</summary>
    public string HostIp => _hostIp;

    /// <summary>
    /// Der Endpunkt, gegen den der SDK-TURN-Client (ein loopback-gebundener Socket) die Signalisierung fährt.
    /// coturn lauscht auf <c>0.0.0.0:3478</c> — also auch auf der Loopback-Adresse. Ein loopback-gebundener
    /// Client MUSS coturn über <c>127.0.0.1</c> erreichen (ein Socket auf <c>127.0.0.1</c> kann keine Antworten
    /// empfangen, die an die Host-LAN-IP adressiert sind). Die per Relay annoncierte Adresse bleibt davon
    /// unberührt auf der Host-LAN-IP (siehe <see cref="HostIp"/>) und damit host-erreichbar für den Peer.
    /// </summary>
    public IPEndPoint ServerEndPoint => new(IPAddress.Loopback, TurnPort);

    /// <summary>Startet den Container und wartet, bis coturn TURN-ready ist.</summary>
    public async Task StartAsync()
    {
        // Serialisiert Host-Netz-coturn-Instanzen über parallele Target-Framework-Testprozesse hinweg,
        // damit sie den festen Port 3478 und den Relay-Bereich konfliktfrei belegen.
        _lease = await CoturnHostNetworkLease.AcquireAsync().ConfigureAwait(false);
        try
        {
            await _container.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            _lease.Dispose();
            _lease = null;
            throw;
        }
    }

    /// <summary>Liefert die kombinierte coturn-Konsolenausgabe (für Diagnose/Beleg).</summary>
    public async Task<string> GetConsoleLogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
        return stdout + "\n" + stderr;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lease?.Dispose();
            _lease = null;
            try { _confFile.Delete(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Ermittelt die primäre ausgehende Host-IPv4 — dieselbe reale, geroutete Adresse, die coturn per
    /// <c>detect-external-ip</c> wählen würde, aber deterministisch und ohne den WAN-Umweg (der eine per
    /// Default gesperrte öffentliche IP liefern kann). Ein verbundener (nicht sendender) UDP-Socket zu einer
    /// externen Adresse lässt das OS die Quell-IP des Standard-Interface auswählen.
    /// </summary>
    private static string DetectPrimaryHostIp()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 65530));
        var local = (IPEndPoint)socket.LocalEndPoint!;
        return local.Address.ToString();
    }
}
