using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.Asterisk;

/// <summary>
/// Startet einen Asterisk-Container (PJSIP, andrius/asterisk:22) mit einer minimalen SIP-Konfiguration
/// (UDP + TCP Transport, Endpoint 6001 mit Digest-Auth) und einem Dialplan für Non-Happy-Path-Calls.
/// Nur für Interop-Tests.
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
        "[6001]\n" +
        "type=endpoint\n" +
        "context=default\n" +
        "disallow=all\n" +
        "allow=ulaw,alaw,g722\n" +               // mehrere Codecs → Negotiation-Tests wählen per SDK-Präferenz
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
        "\n" +
        "[6002]\n" +
        "type=auth\n" +
        "auth_type=userpass\n" +
        "username=6002\n" +
        "password=secret\n" +
        "\n" +
        "[6002]\n" +
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
        "same => n,Playtones(dial)\n" +           // Dial-Ton als Early-Media-RTP vor dem 200 OK
        "same => n,Wait(4)\n" +                   // Early-Media-Fenster
        "same => n,Answer()\n" +                  // → 200 OK
        "same => n,Milliwatt()\n";                // Post-Answer-Media

    private readonly IContainer _container;
    private readonly FileInfo _pjsipConfFile;
    private readonly FileInfo _extensionsConfFile;

    /// <summary>Erstellt (noch nicht gestartet) den Asterisk-Container.</summary>
    public AsteriskContainer()
    {
        // Schreibe die Configs in temporäre Dateien, damit Testcontainers sie als reguläre
        // Dateien (nicht als Byte-Array-Artefakt) ins Container-Dateisystem kopiert.
        _pjsipConfFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_pjsipConfFile.FullName, PjsipConf);
        _extensionsConfFile = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(_extensionsConfFile.FullName, ExtensionsConf);

        _container = new ContainerBuilder("andrius/asterisk:22")
            .WithResourceMapping(_pjsipConfFile, new FileInfo("/etc/asterisk/pjsip.conf"))
            .WithResourceMapping(_extensionsConfFile, new FileInfo("/etc/asterisk/extensions.conf"))
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

    /// <summary>Docker-Host (meist 127.0.0.1/localhost) für den Port-gemappten UDP-Zugang.</summary>
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
    /// Baut eine Ziel-Request-URI für die im Dialplan definierten Test-Extensions
    /// (<c>answer</c> → 200 OK + Media, <c>busy</c>, <c>decline</c>, <c>noanswer</c>) bzw. eine
    /// unbekannte Extension (→ 404). Nur nach <see cref="StartAsync"/> gültig (Container-Bridge-IP).
    /// </summary>
    public string CallTargetUri(string extension) => $"sip:{extension}@{ContainerIpAddress}:5060";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
        try { _pjsipConfFile.Delete(); } catch { /* best effort */ }
        try { _extensionsConfFile.Delete(); } catch { /* best effort */ }
    }
}
