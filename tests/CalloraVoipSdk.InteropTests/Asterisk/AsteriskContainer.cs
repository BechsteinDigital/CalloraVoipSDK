using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.Asterisk;

/// <summary>
/// Startet einen Asterisk-Container (PJSIP, andrius/asterisk:22) mit einer minimalen SIP-Konfiguration
/// (UDP/TCP/TLS-Transport; Endpoints 6001 Plain RTP, 6002 SRTP/SDES, 6003 Bridge/PCMU-only — je Digest-Auth)
/// und einem Dialplan für Non-Happy-Path- und Zwei-Bein-Bridge-Calls. Nur für Interop-Tests.
/// </summary>
public sealed class AsteriskContainer : IAsyncDisposable
{
    private const string SipPortWithProtocol = "5060/udp";

    // Minimale PJSIP-Konfiguration. WICHTIG: Kein führendes Leerzeichen — der Asterisk-Parser
    // erwartet Einträge am Zeilenanfang. TCP-Transport ist nötig, weil das SDK große INVITEs
    // (SDP + Auth > UDP-MTU) RFC 3261 §18.1.1-konform auf TCP eskaliert.
    private const string PjsipConf =
        "[transport-udp]\n" +
        "type=transport\n" +
        "protocol=udp\n" +
        "bind=0.0.0.0:5060\n" +
        "\n" +
        "[transport-tcp]\n" +
        "type=transport\n" +
        "protocol=tcp\n" +
        "bind=0.0.0.0:5060\n" +
        "\n" +
        "[transport-tls]\n" +
        "type=transport\n" +
        "protocol=tls\n" +
        "bind=0.0.0.0:5061\n" +
        "cert_file=/etc/asterisk/keys/asterisk.pem\n" +
        "priv_key_file=/etc/asterisk/keys/asterisk.key\n" +
        "method=tlsv1_2\n" +
        "\n" +
        "[6001]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw,alaw,g722\n" +               // mehrere Codecs → Negotiation-Tests wählen per SDK-Präferenz
        "auth=6001\n" +
        "aors=6001\n" +
        // direct_media=no: Asterisk bleibt im Medienpfad (RTP-Relay) statt die gebrückten Legs per
        // re-INVITE auf direkte Endpoint-zu-Endpoint-Media umzustellen — sonst empfängt kein Leg RTP
        // (Zwei-Bein-Test). No-op für app-basierte 6001-Calls (Milliwatt etc.), die Asterisk selbst bedient.
        "direct_media=no\n" +
        "\n" +
        "[6001]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6001\n" +
        "password=secret\n" +
        "\n" +
        "[6001]\n" +
        "type=aor\n" +
        "max_contacts=1\n" +
        "\n" +
        // Zweiter Endpoint mit erzwungener SRTP-SDES-Medienverschlüsselung (RFC 4568) für die
        // SRTP-Interop-Tests. 6001 bleibt bewusst Plain RTP, damit die Non-Happy-Path-/Happy-Path-/
        // Codec-Tests (SrtpPolicy.Disabled) unberührt bleiben.
        "[6002]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw,alaw,g722\n" +
        "media_encryption=sdes\n" +               // erzwingt RTP/SAVP + a=crypto (SDES)
        "auth=6002\n" +
        "aors=6002\n" +
        "direct_media=no\n" +
        "\n" +
        "[6002]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6002\n" +
        "password=secret\n" +
        "\n" +
        "[6002]\n" +
        "type=aor\n" +
        "max_contacts=1\n" +
        "\n" +
        // Dritter Endpoint: Plain RTP, PCMU-only. Ziel der Zwei-Bein-Bridge — PCMU auf beiden Legs
        // garantiert Same-Codec-Passthrough für die byte-exakte Inhaltsverifikation.
        "[6003]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw\n" +
        "auth=6003\n" +
        "aors=6003\n" +
        "direct_media=no\n" +                     // s. 6001: Relay erzwingen, sonst fließt kein Bridge-RTP
        "\n" +
        "[6003]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6003\n" +
        "password=secret\n" +
        "\n" +
        "[6003]\n" +
        "type=aor\n" +
        "max_contacts=1\n" +
        "\n" +
        // Vierter Endpoint: SRTP-SDES, PCMU-only — Callee-Bein der verschlüsselten Zwei-Bein-Bridge.
        "[6004]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw\n" +
        "media_encryption=sdes\n" +
        "auth=6004\n" +
        "aors=6004\n" +
        "direct_media=no\n" +
        "\n" +
        "[6004]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6004\n" +
        "password=secret\n" +
        "\n" +
        "[6004]\n" +
        "type=aor\n" +
        "max_contacts=1\n";

    // Dialplan für Call-Tests. Kontext [default] passt zu context=default am Endpoint 6001.
    // Non-Happy-Path-Extensions bilden je einen definierten SIP-Fehler ab (App→SIP live verifiziert);
    // die answer-Extension beantwortet den Call und sendet aktiv Media (Milliwatt-Testton), sodass
    // SDK-seitig RTP-Empfang messbar ist. Unbekannte Extensions (kein Eintrag) → Asterisk 404.
    private const string ExtensionsConf =
        "[default]\n" +
        "exten => busy,1,Busy()\n" +              // → 486 Busy Here
        "exten => decline,1,Hangup(21)\n" +       // Q.850 cause 21 → Ablehnung
        "exten => noanswer,1,Ringing()\n" +       // ringt, ohne je zu antworten
        "same => n,Wait(3600)\n" +                // → aufrufer-seitiger Timeout / CANCEL
        "exten => answer,1,Answer()\n" +          // → 200 OK, Dialog etabliert
        "same => n,Milliwatt()\n" +               // endloser 1004-Hz-Testton → RTP fließt SDK-wärts
        "exten => dtmf,1,Answer()\n" +            // → 200 OK, dann RFC-4733-Ziffern senden
        "same => n,Wait(2)\n" +                   // Media etablieren, DTMF-Listener anhängen
        "same => n,SendDTMF(1234)\n" +            // sendet 1-2-3-4 als telephone-event
        "same => n,Wait(30)\n" +                  // Call offen halten für den Empfang
        "exten => earlymedia,1,Progress()\n" +    // → 183 Session Progress mit SDP (Early Media)
        "same => n,Playtones(dial)\n" +           // Dial-Ton als Early-Media-RTP vor dem 200 OK (Playtones
                                                  //   antwortet NICHT — Wait() hält das Fenster wirklich offen)
        "same => n,Wait(10)\n" +                  // Early-Media-Fenster. Empirisch (Slice 3e): Plain-RTP kommt
                                                  //   ~0,6 s nach dem 183, SDES-SRTP erst ~5,5 s (Krypto-Setup-
                                                  //   Latenz). 10 s geben BEIDEN Pfaden Puffer vor dem Answer;
                                                  //   Wait(4) ließ das SDES-Early-Media-Fenster kollabieren.
        "same => n,Answer()\n" +                  // → 200 OK
        "same => n,Milliwatt()\n" +               // Post-Answer-Media
        "exten => 6003,1,Dial(PJSIP/6003,30)\n" +   // brückt an den zweiten registrierten SDK-Endpoint (Plain)
        "exten => 6004,1,Dial(PJSIP/6004,30)\n";     // brückt an den SDES-Callee (verschlüsselte Zwei-Bein-Bridge)

    private readonly IContainer _container;
    private readonly FileInfo _pjsipConfFile;
    private readonly FileInfo _extensionsConfFile;
    private readonly FileInfo _tlsCertFile;
    private readonly FileInfo _tlsKeyFile;

    /// <summary>Erstellt (noch nicht gestartet) den Asterisk-Container.</summary>
    /// <param name="extraBridgePairs">
    /// Anzahl zusätzlicher Plain-RTP-Endpoint-Paare für den Concurrent-Call-Soak.
    /// Paar <c>i</c> besteht aus Caller <c>sc{i}</c> und Callee <c>se{i}</c>, beide PCMU-only.
    /// 0 (Standard) → Konfiguration byte-identisch mit dem Basis-Setup.
    /// </param>
    public AsteriskContainer(int extraBridgePairs = 0)
    {
        // Generiere bei Bedarf zusätzliche Endpoint-Paare und hänge sie an die Basis-Configs.
        var pjsipContent = extraBridgePairs > 0
            ? PjsipConf + BuildSoakPjsipConf(extraBridgePairs)
            : PjsipConf;
        var extensionsContent = extraBridgePairs > 0
            ? ExtensionsConf + BuildSoakExtensionsConf(extraBridgePairs)
            : ExtensionsConf;

        // Schreibe die Configs in temporäre Dateien, damit Testcontainers sie als reguläre
        // Dateien (nicht als Byte-Array-Artefakt) ins Container-Dateisystem kopiert.
        _pjsipConfFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_pjsipConfFile.FullName, pjsipContent);
        _extensionsConfFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_extensionsConfFile.FullName, extensionsContent);

        // Self-signed TLS-Zertifikat für [transport-tls]; der SDK vertraut ihm im Test über
        // TlsConfiguration.AcceptUntrustedCertificates.
        using (var rsa = RSA.Create(2048))
        {
            var certRequest = new CertificateRequest("CN=asterisk", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            _tlsCertFile = new FileInfo(Path.GetTempFileName());
            File.WriteAllText(_tlsCertFile.FullName, cert.ExportCertificatePem());
            _tlsKeyFile = new FileInfo(Path.GetTempFileName());
            File.WriteAllText(_tlsKeyFile.FullName, rsa.ExportPkcs8PrivateKeyPem());
        }

        _container = new ContainerBuilder("andrius/asterisk:22")
            .WithResourceMapping(_pjsipConfFile, new FileInfo("/etc/asterisk/pjsip.conf"))
            .WithResourceMapping(_extensionsConfFile, new FileInfo("/etc/asterisk/extensions.conf"))
            .WithResourceMapping(_tlsCertFile, new FileInfo("/etc/asterisk/keys/asterisk.pem"))
            .WithResourceMapping(_tlsKeyFile, new FileInfo("/etc/asterisk/keys/asterisk.key"))
            .WithExposedPort(SipPortWithProtocol)
            .WithPortBinding(SipPortWithProtocol, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Asterisk Ready."))
            .Build();
    }

    /// <summary>SIP-Account-Benutzername des konfigurierten Endpoints.</summary>
    public string Username => "6001";

    /// <summary>Passwort des konfigurierten Endpoints (Digest-Auth).</summary>
    public string Password => "secret";

    /// <summary>Benutzername des zweiten Endpoints mit erzwungener SRTP-SDES-Medienverschlüsselung.</summary>
    public string SdesUsername => "6002";

    /// <summary>Passwort des SDES-Endpoints (Digest-Auth).</summary>
    public string SdesPassword => "secret";

    /// <summary>Benutzername des dritten Plain-RTP-Endpoints (PCMU-only), Ziel der Zwei-Bein-Bridge.</summary>
    public string BridgeUsername => "6003";

    /// <summary>Passwort des Bridge-Endpoints (Digest-Auth).</summary>
    public string BridgePassword => "secret";

    /// <summary>Benutzername des vierten Endpoints (SRTP-SDES, PCMU-only), Callee-Bein der verschlüsselten Bridge.</summary>
    public string SdesBridgeUsername => "6004";

    /// <summary>Passwort des SDES-Bridge-Endpoints (Digest-Auth).</summary>
    public string SdesBridgePassword => "secret";

    /// <summary>Docker-Host (meist 127.0.0.1/localhost) für den Port-gemappten UDP-Zugang.</summary>
    public string Host => _container.Hostname;

    /// <summary>Auf den Host gemappter SIP/UDP-Port.</summary>
    public ushort SipUdpPort => _container.GetMappedPublicPort(SipPortWithProtocol);

    /// <summary>
    /// Interne Docker-Bridge-IP des Containers — für direkten Zugriff ohne NAT/Port-Mapping.
    /// Nur nach <see cref="StartAsync"/> gültig.
    /// </summary>
    public string ContainerIpAddress => _container.IpAddress;

    /// <summary>Fester TLS-SIP-Port des Containers (über die Bridge-IP erreichbar).</summary>
    public int SipTlsPort => 5061;

    // ── Soak-Accessoren ──────────────────────────────────────────────────────────────────────────
    // Verfügbar nur, wenn der Container mit extraBridgePairs > 0 erstellt wurde.

    /// <summary>Soak-Passwort (identisch für alle generierten Endpoint-Paare).</summary>
    public string SoakPassword => "secret";

    /// <summary>Benutzername des Soak-Caller-Endpoints für Paar <paramref name="i"/>.</summary>
    public string SoakCallerUser(int i) => $"sc{i}";

    /// <summary>Benutzername des Soak-Callee-Endpoints für Paar <paramref name="i"/>.</summary>
    public string SoakCalleeUser(int i) => $"se{i}";

    /// <summary>Dialplan-Extension des Soak-Callee-Endpoints für Paar <paramref name="i"/>.</summary>
    public string SoakBridgeExtension(int i) => $"se{i}";

    // ── Hilfsmethoden für die Konfigurationsgenerierung ──────────────────────────────────────────

    /// <summary>
    /// Generiert PJSIP-Konfiguration für <paramref name="n"/> zusätzliche Plain-RTP-Endpoint-Paare
    /// (sc{i}/se{i}, i=0..n−1). Wird an <see cref="PjsipConf"/> angehängt.
    /// </summary>
    private static string BuildSoakPjsipConf(int n)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        for (var i = 0; i < n; i++)
        {
            var caller = $"sc{i}";
            var callee = $"se{i}";
            foreach (var user in new[] { caller, callee })
            {
                sb.AppendLine($"[{user}]");
                sb.AppendLine("type=endpoint");
                sb.AppendLine("context=default");
                sb.AppendLine("disallow=all");
                sb.AppendLine("allow=ulaw");
                sb.AppendLine($"auth={user}");
                sb.AppendLine($"aors={user}");
                sb.AppendLine("direct_media=no");
                sb.AppendLine();
                sb.AppendLine($"[{user}]");
                sb.AppendLine("type=auth");
                sb.AppendLine("auth_type=userpass");
                sb.AppendLine($"username={user}");
                sb.AppendLine("password=secret");
                sb.AppendLine();
                sb.AppendLine($"[{user}]");
                sb.AppendLine("type=aor");
                sb.AppendLine("max_contacts=1");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Generiert Dialplan-Extensions, die jeden Soak-Callee <c>se{i}</c> über
    /// <c>Dial(PJSIP/se{i})</c> brücken. Wird an <see cref="ExtensionsConf"/> angehängt.
    /// </summary>
    private static string BuildSoakExtensionsConf(int n)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        for (var i = 0; i < n; i++)
            sb.AppendLine($"exten => se{i},1,Dial(PJSIP/se{i},30)");
        return sb.ToString();
    }

    /// <summary>Startet den Container und wartet, bis Asterisk SIP-ready ist.</summary>
    public Task StartAsync() => _container.StartAsync();

    /// <summary>
    /// Führt ein Kommando im Container aus (z. B. die Asterisk-CLI via <c>asterisk -rx …</c>) und
    /// gibt dessen Standardausgabe zurück. Nur nach <see cref="StartAsync"/> gültig.
    /// </summary>
    public async Task<string> ExecAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command).ConfigureAwait(false);
        return result.Stdout;
    }

    /// <summary>
    /// Liefert die kombinierte Container-Standardausgabe (Asterisk-Konsole) — z. B. um bei aktivem
    /// <c>pjsip set logger on</c> die ausgetauschten SIP-Nachrichten zu inspizieren.
    /// </summary>
    public async Task<string> GetConsoleLogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
        return stdout + "\n" + stderr;
    }

    /// <summary>
    /// Baut eine Ziel-Request-URI für die im Dialplan definierten Test-Extensions
    /// (<c>answer</c> → 200 OK + Media, <c>busy</c>, <c>decline</c>, <c>noanswer</c>) bzw. eine
    /// unbekannte Extension (→ 404). <paramref name="port"/> muss zum Signalisierungs-Transport passen
    /// (5060 für UDP/TCP, <see cref="SipTlsPort"/> für TLS). Nur nach <see cref="StartAsync"/> gültig.
    /// </summary>
    public string CallTargetUri(string extension, int port = 5060) => $"sip:{extension}@{ContainerIpAddress}:{port}";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
        try { _pjsipConfFile.Delete(); } catch { /* best effort */ }
        try { _extensionsConfFile.Delete(); } catch { /* best effort */ }
        try { _tlsCertFile.Delete(); } catch { /* best effort */ }
        try { _tlsKeyFile.Delete(); } catch { /* best effort */ }
    }
}
