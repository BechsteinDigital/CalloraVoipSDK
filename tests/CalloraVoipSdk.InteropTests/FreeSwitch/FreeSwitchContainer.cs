using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.FreeSwitch;

/// <summary>
/// Startet einen FreeSWITCH-Container (safarov/freeswitch:latest) mit injizierter Directory- und
/// Dialplan-XML (Config-Overlay über die Vanilla-Config): Endpoints 6001 (Plain, Multi-Codec),
/// 6003 (Plain, PCMU-only) als Zwei-Bein-Bridge-Paar, 6002/6004 (SRTP-SDES), plus optionale
/// Soak-Paare sc{i}/se{i}. FreeSWITCH ist B2BUA → immer im Medienpfad (kein direct_media nötig).
/// Nur für Interop-Tests. Zugriff über die Container-Bridge-IP:5060 (Linux).
/// </summary>
public sealed class FreeSwitchContainer : IAsyncDisposable
{
    // FreeSWITCH-Domain in der Vanilla-Config = $${domain} = Container-IP. Wir referenzieren sie im
    // Dialplan als $${domain}; Registrierungen landen via force-register-domain in dieser Domain.
    private const string DirectoryPath = "/etc/freeswitch/directory/default/zzz_callora.xml";
    private const string DialplanPath = "/etc/freeswitch/dialplan/default.xml";

    private readonly IContainer _container;
    private readonly FileInfo _directoryFile;
    private readonly FileInfo _dialplanFile;

    /// <summary>Erstellt (noch nicht gestartet) den FreeSWITCH-Container.</summary>
    /// <param name="extraBridgePairs">Zusätzliche Plain-RTP-Paare sc{i}/se{i} für den Soak (0 = Basis).</param>
    public FreeSwitchContainer(int extraBridgePairs = 0)
    {
        _directoryFile = WriteTemp(BuildDirectoryXml(extraBridgePairs));
        _dialplanFile = WriteTemp(BuildDialplanXml(extraBridgePairs));

        _container = new ContainerBuilder("safarov/freeswitch:latest")
            .WithResourceMapping(_directoryFile, new FileInfo(DirectoryPath))
            .WithResourceMapping(_dialplanFile, new FileInfo(DialplanPath))
            .WithExposedPort("5060/udp")
            .WithPortBinding("5060/udp", assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MSG Thread 0 Started"))
            .Build();
    }

    private static FileInfo WriteTemp(string content)
    {
        var f = new FileInfo(Path.GetTempFileName());
        File.WriteAllText(f.FullName, content);
        return f;
    }

    // ── Directory (registrierbare User) ──────────────────────────────────────────────────────────
    // Ein <include> mit mehreren <user>. Domain = die des einschließenden directory/default.xml
    // (= $${domain} = Container-IP). PCMU-Pin via absolute_codec_string; SDES via rtp_secure_media.
    private static string BuildDirectoryXml(int extraBridgePairs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<include>");
        AppendUser(sb, "6001", codec: null, sdes: false);   // Caller Plain, Multi-Codec (SDK pinnt)
        AppendUser(sb, "6003", codec: "PCMU", sdes: false);  // Callee Plain, PCMU-only
        AppendUser(sb, "6002", codec: null, sdes: true);     // Caller SDES
        AppendUser(sb, "6004", codec: "PCMU", sdes: true);   // Callee SDES, PCMU-only
        for (var i = 0; i < extraBridgePairs; i++)
        {
            AppendUser(sb, $"sc{i}", codec: "PCMU", sdes: false);
            AppendUser(sb, $"se{i}", codec: "PCMU", sdes: false);
        }
        sb.AppendLine("</include>");
        return sb.ToString();
    }

    private static void AppendUser(System.Text.StringBuilder sb, string id, string? codec, bool sdes)
    {
        sb.AppendLine($"  <user id=\"{id}\">");
        sb.AppendLine("    <params><param name=\"password\" value=\"secret\"/></params>");
        sb.AppendLine("    <variables>");
        sb.AppendLine("      <variable name=\"user_context\" value=\"default\"/>");
        if (codec is not null)
            sb.AppendLine($"      <variable name=\"absolute_codec_string\" value=\"{codec}\"/>");
        if (sdes)
            sb.AppendLine("      <variable name=\"rtp_secure_media\" value=\"mandatory\"/>");
        sb.AppendLine("    </variables>");
        sb.AppendLine("  </user>");
    }

    // ── Dialplan (Bridge + Media-Playback) ───────────────────────────────────────────────────────
    // Ersetzt die Vanilla-default.xml. Caller wählt die Callee-Extension → bridge an den User.
    // "answer" spielt endlosen 1004-Hz-Ton (Milliwatt-Äquivalent) für die Transfer-Konsultation.
    private static string BuildDialplanXml(int extraBridgePairs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<include>");
        sb.AppendLine("  <context name=\"default\">");
        AppendBridge(sb, "6003");
        AppendBridge(sb, "6004");
        sb.AppendLine("    <extension name=\"callora-media-playback\">");
        sb.AppendLine("      <condition field=\"destination_number\" expression=\"^answer$\">");
        sb.AppendLine("        <action application=\"answer\"/>");
        sb.AppendLine("        <action application=\"playback\" data=\"tone_stream://%(3600000,0,1004)\"/>");
        sb.AppendLine("      </condition>");
        sb.AppendLine("    </extension>");
        for (var i = 0; i < extraBridgePairs; i++)
            AppendBridge(sb, $"se{i}");
        sb.AppendLine("  </context>");
        sb.AppendLine("</include>");
        return sb.ToString();
    }

    private static void AppendBridge(System.Text.StringBuilder sb, string callee)
    {
        sb.AppendLine($"    <extension name=\"callora-bridge-{callee}\">");
        sb.AppendLine($"      <condition field=\"destination_number\" expression=\"^{callee}$\">");
        sb.AppendLine($"        <action application=\"bridge\" data=\"user/{callee}@$${{domain}}\"/>");
        sb.AppendLine("      </condition>");
        sb.AppendLine("    </extension>");
    }

    // ── IPbxFixture-relevante Accessoren ─────────────────────────────────────────────────────────

    /// <summary>
    /// Startet den Container und wartet bis FreeSWITCH SIP-ready ist.
    /// <para>
    /// Hintergrund: <c>MSG Thread 0 Started</c> (sofia.c:2290) erscheint ~2 s vor dem Ende der
    /// FreeSWITCH-Startup-Phase. Während dieser Phase antwortet Sofia auf eingehende REGISTER-Requests
    /// mit <c>503 Maximum Calls In Progress</c>. Der SDK bricht bei 503 sofort ab (kein Retry), daher
    /// brauchen wir einen kurzen Settle-Delay nach dem Log-Wait. 3 s überbrücken das 503-Fenster
    /// zuverlässig; gemessen: ~2,0 s Fenster über mehrere Container-Starts hinweg.
    /// </para>
    /// </summary>
    public async Task StartAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        // Settle-Delay: überbrückt das Post-Startup-503-Fenster (s. Klassen-Kommentar oben).
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }
    public string ContainerIpAddress => _container.IpAddress;

    public string Username => "6001";
    public string Password => "secret";
    public string BridgeUsername => "6003";
    public string BridgePassword => "secret";
    public string SdesUsername => "6002";
    public string SdesPassword => "secret";
    public string SdesBridgeUsername => "6004";
    public string SdesBridgePassword => "secret";
    public string SoakPassword => "secret";
    public string SoakCallerUser(int i) => $"sc{i}";
    public string SoakCalleeUser(int i) => $"se{i}";
    public string SoakBridgeExtension(int i) => $"se{i}";

    /// <summary>Ziel-Request-URI für eine Dialplan-Extension (Bridge-Callee oder "answer").</summary>
    public string CallTargetUri(string extension) => $"sip:{extension}@{ContainerIpAddress}:5060";

    public async Task<string> GetConsoleLogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
        return stdout + "\n" + stderr;
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
        try { _directoryFile.Delete(); } catch { /* best effort */ }
        try { _dialplanFile.Delete(); } catch { /* best effort */ }
    }
}
