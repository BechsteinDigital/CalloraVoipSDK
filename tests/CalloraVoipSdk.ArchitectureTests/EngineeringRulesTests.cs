using System.Text.RegularExpressions;
using Xunit;

namespace CalloraVoipSdk.ArchitectureTests;

/// <summary>
/// Mechanische Gates fuer ENGINEERING_RULES.md. Jede Regel prueft den gesamten
/// Quellbaum und vergleicht gegen eine Baseline bekannter Altlasten
/// (Voll-Audit 2026-07-08, docs/agent-log/2026-07-08-full-sdk-code-review.md).
/// Baselines duerfen nur schrumpfen: neue Verstoesse schlagen fehl,
/// behobene Eintraege muessen aus der Baseline entfernt werden.
/// </summary>
public sealed class EngineeringRulesTests
{
    // --- Regel: DDD-Schichtung — Domain haengt von niemandem, Application nicht von Infrastructure/Client ---

    // K4 vollstaendig behoben: DialOptions ist Domain (Core.Domain.Calls); PhoneLine haengt
    // nur noch an der Domain-Abstraktion ICallRegistry (von CallManager implementiert) statt
    // am Application-CallManager. Kein Domain->Application/Infrastructure-Leak mehr.
    private static readonly string[] LayeringBaseline = [];

    [Fact]
    public void Domain_und_Application_halten_die_Schichtrichtung_ein()
    {
        var violations = new List<string>();

        foreach (var file in SourceScan.CsFiles("src/Core/Domain"))
        {
            var content = File.ReadAllText(file);
            if (Regex.IsMatch(content, @"^\s*using\s+CalloraVoipSdk\.Core\.(Application|Infrastructure)", RegexOptions.Multiline) ||
                Regex.IsMatch(content, @"^\s*using\s+CalloraVoipSdk\.Client", RegexOptions.Multiline))
            {
                violations.Add(SourceScan.Relative(file));
            }
        }

        foreach (var file in SourceScan.CsFiles("src/Core/Application"))
        {
            var content = File.ReadAllText(file);
            if (Regex.IsMatch(content, @"^\s*using\s+CalloraVoipSdk\.Core\.Infrastructure", RegexOptions.Multiline) ||
                Regex.IsMatch(content, @"^\s*using\s+CalloraVoipSdk\.Client", RegexOptions.Multiline))
            {
                violations.Add(SourceScan.Relative(file));
            }
        }

        SourceScan.AssertMatchesBaseline("DDD-Schichtrichtung", violations, LayeringBaseline);
    }

    // --- Regel: Das Schicht-Segment des Namespace muss zur Ordner-Schicht passen ---
    // Eine Datei unter Domain/ (bzw. Application/, Infrastructure/) MUSS ihr eigenes
    // Schicht-Segment im Namespace tragen UND darf kein fremdes Schicht-Segment tragen.
    // Untergeordnete Ordner (z. B. Contracts/) duerfen weiterhin in den Elternnamespace
    // klappen — das behaelt das Schicht-Segment und bleibt erlaubt. Nicht erlaubt ist die
    // Layer-Omission (frueher Core.Security unter Domain/Security/ bzw. Infrastructure/Security/):
    // der urspruengliche Foreign-Layer-Only-Check (HARD-G1) uebersah genau diesen Drift.

    // K3 behoben (B.3): die Application.Media.Rtcp.*-Dateien liegen jetzt unter
    // src/Core/Application/Media/Rtcp/ (Namespace unveraendert). Nur die echte Impl
    // RtcpPacketCodec.cs (Infrastructure.Rtcp.Wire) bleibt in Infrastructure/.
    // HARD-G1: SrtpPolicy/SrtpDecisionReasonCodes -> Core.Domain.Security,
    // TlsConfiguration/SipDomainCertificateValidator -> Core.Infrastructure.Security.
    private static readonly string[] LayerSegmentBaseline = [];

    [Fact]
    public void Schicht_Segment_des_Namespace_passt_zur_Ordner_Schicht()
    {
        var violations = new List<string>();

        foreach (var file in SourceScan.CsFiles("src/Core/Domain", "src/Core/Application", "src/Core/Infrastructure"))
        {
            var declared = SourceScan.DeclaredNamespace(File.ReadAllText(file));
            if (declared is null)
            {
                continue;
            }

            var relative = SourceScan.Relative(file);
            if (SourceScan.LayerSegmentViolation(relative, declared))
            {
                violations.Add(relative);
            }
        }

        SourceScan.AssertMatchesBaseline("Schicht-Segment = Ordner-Schicht", violations, LayerSegmentBaseline);
    }

    // Diskriminierender Regressionsschutz fuer die verschaerfte Regel (HARD-G1): der frueheres
    // Foreign-Layer-Only-Check haette die Layer-Omission (Core.Security) durchgelassen.
    [Theory]
    // Layer-Omission — genau der behobene Drift: unter Domain/ bzw. Infrastructure/ ohne eigenes Segment.
    [InlineData("src/Core/Domain/Security/SrtpPolicy.cs", "CalloraVoipSdk.Core.Security", true)]
    [InlineData("src/Core/Infrastructure/Security/TlsConfiguration.cs", "CalloraVoipSdk.Core.Security", true)]
    // Fremdes Schicht-Segment.
    [InlineData("src/Core/Application/Media/Foo.cs", "CalloraVoipSdk.Core.Infrastructure.Media", true)]
    // Korrekt nach dem Fix.
    [InlineData("src/Core/Domain/Security/SrtpPolicy.cs", "CalloraVoipSdk.Core.Domain.Security", false)]
    [InlineData("src/Core/Infrastructure/Security/TlsConfiguration.cs", "CalloraVoipSdk.Core.Infrastructure.Security", false)]
    // Contracts/ klappt in den Elternnamespace — behaelt das Schicht-Segment, bleibt erlaubt.
    [InlineData("src/Core/Infrastructure/Sip/Signaling/Contracts/ISipCallSession.cs", "CalloraVoipSdk.Core.Infrastructure.Sip.Signaling", false)]
    public void LayerSegmentViolation_erkennt_Omission_und_Foreign_Layer(string relative, string declared, bool expected)
    {
        Assert.Equal(expected, SourceScan.LayerSegmentViolation(relative, declared));
    }

    // --- Regel: max. 1000 Zeilen pro Datei ---

    private static readonly string[] FileLengthBaseline =
    [
        // Empty: the two former 1000+-line dialog files were split into collaborators
        // (e.g. SipForkedInviteHandler) and no longer exceed the cap.
    ];

    [Fact]
    public void Keine_Datei_ueberschreitet_1000_Zeilen()
    {
        var violations = SourceScan.CsFiles("src", "tests", "samples")
            .Where(f => File.ReadLines(f).Count() > 1000)
            .Select(SourceScan.Relative)
            .ToList();

        SourceScan.AssertMatchesBaseline("max. 1000 Zeilen", violations, FileLengthBaseline);
    }

    // --- Regel: keine verschachtelten Typen (private/protected class|interface|record in Typen) ---

    // B.3 behoben: MediaActivity und LearnedPublicContact sind jetzt Top-Level-internal-Typen
    // (eigene Dateien im jeweiligen Namespace).
    private static readonly string[] NestedTypeBaseline = [];

    [Fact]
    public void Keine_privaten_verschachtelten_Typen()
    {
        var pattern = new Regex(@"^\s*(private|protected)(\s+\w+)*\s+(class|interface|record)\s", RegexOptions.Multiline);

        var violations = SourceScan.CsFiles("src")
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(SourceScan.Relative)
            .ToList();

        SourceScan.AssertMatchesBaseline("keine verschachtelten Typen", violations, NestedTypeBaseline);
    }

    // --- Regel: kein stummer catch (leerer oder nur-Kommentar-Body ohne Logging) ---

    private static readonly string[] SilentCatchBaseline =
    [
        // Vollstaendige Ist-Inventur (Stand 2026-07-08). Der Regex erfasst leere/
        // nur-Kommentar-Catch-Bloecke — darunter echte Verstoesse UND akzeptable
        // Shutdown-/Fallback-Catches. Ziel: Liste schrumpft; die echten Verstoesse
        // aus dem Audit zuerst (MediaConnection, MediaReceiver, SipCoreCallChannel).
        // Ein Catch mit Logging faellt automatisch aus der Liste und muss dann hier raus.
        // B.3/B.4-Findings: SipCoreCallChannel, MediaConnection und MediaReceiver entschaerft
        // (Logger injiziert, alle Catches loggen jetzt). Rest = legitime Shutdown-/Fallback-Catches.
        "src/Core/Application/Media/CallRtcpQualityMonitor.cs",
        "src/Core/Infrastructure/Common/Network/LocalEndPointAdvertisementResolver.cs",
        "src/Core/Infrastructure/Media/Mp3AudioFileCodec.cs",
        "src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs",
        "src/Core/Infrastructure/Rtp/Session/RtpSession.cs",
        "src/Core/Infrastructure/Sip/Signaling/Contracts/SipSubscriptionHandle.cs",
        "src/Core/Infrastructure/Sip/Signaling/Dialogs/SipCallSession.cs",
        "src/Core/Infrastructure/Sip/Signaling/Dialogs/SipCallSessionUtilities.cs",
        "src/Core/Infrastructure/Sip/Signaling/SessionTimers/SipSessionTimerManager.cs",
        "src/Core/Infrastructure/Sip/Signaling/Subscriptions/SipSubscriptionLifecycleManager.cs",
        "src/Core/Infrastructure/Sip/Transactions/Server/SipServerTransactionEngine.cs",
        "src/Core/Infrastructure/Sip/Transactions/SipClientTransactionExecutor.cs",
        "src/Core/Infrastructure/Sip/Transport/SipStreamConnection.cs",
        "src/Core/Infrastructure/Sip/Transport/SipTransportRuntime.cs",
        "src/Core/Infrastructure/Sip/Transport/SipWebSocketConnection.cs",
        "src/Core/Infrastructure/Stun/Client/DnsSrvQuery.cs",
        "src/Core/Infrastructure/Stun/Server/StunServer.cs",
        "src/Core/Infrastructure/Turn/Client/TurnTcpDataConnection.cs",
        "src/Core/Infrastructure/Turn/Server/TurnServer.cs",
        "src/Core/Infrastructure/Turn/Server/TurnServerAllocation.cs",
        "src/Core/Infrastructure/Turn/Server/TurnTcpConnectionBroker.cs",
        "src/Core/Infrastructure/Turn/Server/TurnTcpPendingConnection.cs",
    ];

    [Fact]
    public void Keine_stummen_catch_Bloecke()
    {
        // catch [(...)] { } mit Body aus nur Whitespace und Kommentaren
        var pattern = new Regex(
            @"catch\s*(\([^)]*\))?\s*\{(\s|//[^\n]*|/\*.*?\*/)*\}",
            RegexOptions.Singleline);

        var violations = SourceScan.CsFiles("src")
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(SourceScan.Relative)
            .Distinct()
            .ToList();

        SourceScan.AssertMatchesBaseline("kein stummer catch", violations, SilentCatchBaseline);
    }

    // --- Regel: kein Sync-over-Async auf Runtime-Pfaden ---

    private static readonly string[] SyncOverAsyncBaseline =
    [
        // Ist-Bestand 2026-07-08. B.4: CallMediaOrchestrator behoben — ICE-Selektion laeuft
        // jetzt auf einem Background-Task statt den SIP-Signaling-Thread zu blockieren.
        // Die uebrigen sind Dispose-/Transport-Pfade, in denen sync-over-async oft
        // legitim ist (IDisposable erlaubt kein await) — pro Eintrag durch Auditor/Reviewer
        // zu bewerten.
        "src/Core/Application/Media/MediaConnection.cs",
        "src/Core/Infrastructure/Media/Mp3TranscodingWriter.cs",
        "src/Core/Infrastructure/Sip/Transport/SipStreamConnection.cs",
        "src/Core/Infrastructure/Sip/Transport/SipWebSocketConnection.cs",
    ];

    [Fact]
    public void Kein_GetAwaiter_GetResult_im_Produktcode()
    {
        // Toleriert Zeilenumbrueche zwischen den fluent-Aufrufen (.GetAwaiter()\n.GetResult()).
        var pattern = new Regex(@"\.GetAwaiter\(\)\s*\.GetResult\(\)", RegexOptions.Singleline);

        var violations = SourceScan.CsFiles("src")
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(SourceScan.Relative)
            .ToList();

        SourceScan.AssertMatchesBaseline("kein Sync-over-Async", violations, SyncOverAsyncBaseline);
    }
}
