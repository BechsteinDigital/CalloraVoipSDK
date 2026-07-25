# Spec: PBX-agnostische `IPbxFixture`-Abstraktion (Interop Phase B.1)

**Status:** Freigegeben (User 2026-07-25) · **Datum:** 2026-07-25 · **Branch:** `feat/interop-pbx-abstraction` (gestapelt auf `feat/interop-two-leg-scenarios`/Phase A) · **Teil von:** Interop-+Soak-+Audit-Paket, Phase B (Multi-PBX)

## 1. Kontext & Ziel

Die Asterisk-Interop-Media-Matrix ist vollständig (Zwei-Bein-Media Plain/SRTP/Codec-Mismatch, DTMF-E2E, Hold/Unhold, Attended-Transfer, Concurrent-Soak). Strategie (User): **statt pro PBX eine eigene Test-Suite → ein PBX-agnostisches Harness**, sodass dieselbe Media-Szenario-Matrix gegen mehrere Fremd-Stacks (Asterisk, FreeSWITCH, …) läuft.

**B.1-Ziel:** Die PBX-Fähigkeiten, die die Media-Matrix nutzt, hinter einem schmalen `IPbxFixture`-Interface kapseln; einen `AsteriskPbxFixture`-Adapter darauf bauen; die Media-Matrix-Tests auf eine peer-agnostische Basisklassen-Form umstellen (mit einer Asterisk-Subklasse). **Verhaltensgleich** — dieselben Tests, dieselben Assertions, weiter grün gegen echten Asterisk. Die Abstraktion wird erst durch FreeSWITCH (B.2) „bewiesen"; B.1 darf nichts brechen.

**Nicht-Ziel (B.1):** FreeSWITCH-Impl (B.2), Test-Parametrisierung-über-beide (fällt mit B.2 an), Migration der Register-/Transport-/Non-Happy-Path-Tests (bleiben Asterisk-spezifisch, stark config-gebunden). Keine SDK-`src/`-Änderungen.

## 2. Was die Media-Matrix von einer PBX braucht (erhoben)

Aus der Nutzungsanalyse aller Interop-Tests: Lifecycle (`StartAsync`/`Dispose`), SIP-Register-Adresse (Host + UDP-Port), registrierbare Endpunkt-Paare (plain 6001/6003, SDES 6002/6004, Soak-Paare), Bridge-Dial-Target (Caller wählt Extension → PBX brückt an registrierten Callee), eine Media-Playback-Extension (Milliwatt, für die Attended-Transfer-Konsultation), Diagnose-Logs. `ExecAsync` (Asterisk-CLI) wird von der Media-Matrix **nicht** gebraucht (nur der nicht-migrierte Inbound-Test nutzt `channel originate`).

## 3. Das Interface

```csharp
namespace CalloraVoipSdk.InteropTests.Pbx;

/// <summary>Ein Fremd-PBX-Peer für die Media-Szenario-Matrix (Asterisk, FreeSWITCH, …).</summary>
public interface IPbxFixture : IAsyncDisposable
{
    /// <summary>Startet den PBX-Container und wartet, bis er SIP-ready ist.</summary>
    Task StartAsync();

    /// <summary>Register-Ziel-Host (Container-Bridge-IP o. ä.). Nur nach StartAsync gültig.</summary>
    string SipHost { get; }

    /// <summary>Register-Ziel-UDP-Port.</summary>
    int SipUdpPort { get; }

    /// <summary>
    /// Ein gebrücktes Endpunkt-Paar: Caller- und Callee-Credentials plus die Dial-URI, die der Caller
    /// wählt, damit der PBX ihn an den registrierten Callee brückt. <paramref name="index"/> wählt
    /// eines der bereitgestellten Paare (0-basiert; für den Concurrent-Soak).
    /// </summary>
    PbxBridgePair BridgePair(PbxMediaMode mode, int index);

    /// <summary>Dial-URI einer Extension, die antwortet und Endlos-Media spielt (Transfer-Konsultation).</summary>
    string MediaPlaybackUri { get; }

    /// <summary>Kombinierte Container-Konsolen-Logs (Diagnose).</summary>
    Task<string> GetLogsAsync();
}

/// <summary>Medien-Sicherheitsmodus eines Bridge-Paars.</summary>
public enum PbxMediaMode { Plain, Sdes }

/// <summary>Digest-Credentials eines registrierbaren PBX-Endpunkts.</summary>
public sealed record PbxEndpoint(string Username, string Password);

/// <summary>Ein Caller/Callee-Paar plus die Bridge-Dial-URI, die den Caller an den Callee brückt.</summary>
public sealed record PbxBridgePair(PbxEndpoint Caller, PbxEndpoint Callee, string BridgeDialUri);
```

**Semantik:**
- **Plain-Modus:** Caller-Endpunkt erlaubt mehrere Codecs (ulaw/alaw/g722), Callee ist PCMU-only. Damit deckt EIN Plain-Paar sowohl Passthrough (Client pinnt PCMU) als auch **Codec-Mismatch** (Client pinnt G.722 → PBX transcodiert) ab — kein eigener Modus nötig.
- **Sdes-Modus:** beide Endpunkte `media_encryption=sdes` (PCMU-only), für die SRTP-Zwei-Bein-Variante.
- **Provisioning-Count:** wie viele Bridge-Paare bereitgestellt werden, geht in den **Konstruktor der konkreten Fixture** (nicht ins Interface). Der Soak braucht N; die übrigen Tests Paar `index 0`.

## 4. Migration

- **`AsteriskPbxFixture : IPbxFixture`** (neu, `Pbx/`): hält eine `AsteriskContainer`, mappt die Interface-Operationen (`SipHost`→`ContainerIpAddress`, `SipUdpPort`→5060, `BridgePair(Plain,0)`→{6001,6003,`CallTargetUri("6003")`}, `BridgePair(Sdes,0)`→{6002,6004,`CallTargetUri("6004")`}, `BridgePair(Plain,i>0)`→Soak-Paare `sc{i}`/`se{i}`, `MediaPlaybackUri`→`CallTargetUri("answer")`, `GetLogsAsync`→`GetConsoleLogsAsync`). Konstruktor nimmt `bridgePairs`-Count und reicht ihn als `extraBridgePairs` an `AsteriskContainer` durch. **`AsteriskContainer` bleibt unangetastet** (nur adaptiert).
- **`TwoLegBridgedCall`**: `StartAsync(IPbxFixture pbx, PbxMediaMode mode = PbxMediaMode.Plain, int pairIndex = 0)`. Der `TwoLegProfile` wird aus `pbx.BridgePair(mode, pairIndex)` + `pbx.SipHost`/`SipUdpPort` abgeleitet (Caller/Callee-Creds, Bridge-Dial-URI, `SrtpPolicy` je Modus, Codec-Pin je Test). `DialCallerConsultationAsync` nutzt `pbx.MediaPlaybackUri`. Die bestehende `StartAsync(AsteriskContainer)`-Signatur wird durch die neue ersetzt; alle Aufrufer wandern auf `IPbxFixture`.
- **Media-Matrix-Tests** → **abstrakte Basisklassen** mit `protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);` und `[DockerRequiredFact]`-Tests, die `CreatePbx()` nutzen. Je eine `Asterisk…`-Subklasse überschreibt `CreatePbx(n) => new AsteriskPbxFixture(n)`. Betroffen: die Zwei-Bein-Media-Tests (Plain/SRTP/Codec-Mismatch), DTMF, Hold, Attended-Transfer, Concurrent-Soak. Die Endpoint-/SRTP-Details (SDES-Keys, Codec-Pins) bleiben in `TwoLegBridgedCall`/den Tests, jetzt über die Bridge-Paare des Interfaces.

## 5. Test-Parametrisierung (Zielbild, greift mit B.2)

```csharp
public abstract class TwoLegMediaMatrix
{
    protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);

    [DockerRequiredFact]
    public async Task BridgedCall_FlowsRtpInBothDirections()
    {
        await using var pbx = CreatePbx();
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(pbx);
        // … dieselben Assertions wie heute …
    }
}

public sealed class AsteriskTwoLegMediaMatrix : TwoLegMediaMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}
```
FreeSWITCH (B.2) fügt nur `FreeSwitchPbxFixture` + `FreeSwitch…`-Subklassen hinzu — die Matrix läuft „gratis" gegen beide.

## 6. Fehlerbehandlung & Verhaltensbewahrung

- **Verhaltensgleich:** dieselben Tests/Assertions; keine Assertion geschwächt. Nach dem Umbau muss die volle Media-Matrix + Soak gegen echten Asterisk grün bleiben, und die nicht-migrierten Interop-Tests (Register/Transport/Non-Happy-Path/Inbound/Codec-Negotiation/Session-Timer) unverändert grün.
- **Per-PBX-Verfügbarkeit** (relevant ab B.2): eine Subklasse kann ihren Peer skippen, wenn dessen Image/Docker fehlt (via `[DockerRequiredFact]` bzw. eigener Skip-Logik im `CreatePbx`).
- **Keine `src/`-Änderung** — reine Testinfrastruktur.

## 7. Scope & Slice-Skizze (Übergabe an writing-plans)

1. Interface + Records/Enum (`IPbxFixture`, `PbxEndpoint`, `PbxBridgePair`, `PbxMediaMode`) unter `tests/CalloraVoipSdk.InteropTests/Pbx/`.
2. `AsteriskPbxFixture`-Adapter (wrappt `AsteriskContainer`, mappt alle Ops) + Unit-/Smoke-Nachweis (Register über den Adapter).
3. `TwoLegBridgedCall` auf `IPbxFixture` umstellen (StartAsync + Konsultation), Aufrufer nachziehen.
4. Zwei-Bein-Media-Tests → abstrakte Basis + `AsteriskTwoLegMediaMatrix`-Subklasse (verhaltensgleich, grün).
5. DTMF/Hold/Attended-Transfer → abstrakte Basis + Asterisk-Subklasse.
6. Concurrent-Soak → abstrakte Basis + Asterisk-Subklasse (Provisioning-Count via `CreatePbx(n)`).
7. Voll-Regression: Media-Matrix + Soak grün gegen Asterisk; restliche Interop-Suite unberührt.

## 8. Entscheidungen

- `// DECISION:` Schmales Interface aus dem tatsächlichen Bedarf abgeleitet (kein `ExecAsync`/Originate in B.1 — Media-Matrix braucht sie nicht).
- `// DECISION:` `AsteriskContainer` adaptieren statt ersetzen (Register/Transport-Tests bleiben unberührt).
- `// DECISION:` Codec-Mismatch = client-seitiger Pin auf einem Plain-Paar (kein eigener Modus).
- `// DECISION:` Provisioning-Count via Konstruktor + `CreatePbx(int bridgePairs = 1)` in der Basisklasse.
- `// DECISION:` Parametrisierung via abstrakte Basisklasse + Subklasse pro PBX (nicht `[Theory]`), für sauberes Per-PBX-Reporting/Skip.
