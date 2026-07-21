# Phase 3 — Langzeit-Signaling-Soak (L3): Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** Den letzten Soak-Fokus abdecken — Langzeit-Signaling über den echten `SipLineChannel`-Registrierungs-Loop: viele beschleunigte Re-REGISTER-Zyklen (kurzer Expires), prüft Call-ID-Stabilität, CSeq-Monotonie, kein Ressourcen-Leak, kein Silent-Drop. Plus: den fehlenden Zeit-Abstraktions-Seam als Testbarkeits-Befund dokumentieren.

**Architecture:** L3-Fixture `SipRegisterLoopHarness` im InteropHarness verdrahtet den (internal) `SipLineChannel` mit schlanken Fakes (`ISipRegistrationService` mit kurzem Expires + Aufzeichnung, `NoopSignalingService`, `NoopSdpNegotiator`) und exponiert die Register-Historie public. Der Soak nutzt den echten Refresh-Loop (kein SDK-Seam nötig). Zeit-Achse: real ~20 s (kein echtes Zeit-Rafting — der fehlende `ITimeProvider`-Seam ist Befund F003).

**Tech Stack:** .NET 8/9/10, xUnit, bestehende `ResourceSampler`/`TrendAssertions`.

---

## Verifizierte Fakten (Ground-Truth, gelesen)

- `internal sealed class SipLineChannel : ILineChannel` (`src/Core/Infrastructure/Sip/Adapters/SipLineChannel.cs:19`). Internal Ctor @61:
  `SipLineChannel(SipAccount account, string userAgent, ISipRegistrationService registrationService, ISipCallSignalingService callSignalingService, ISdpNegotiator sdpNegotiator, ICallIceAgent? iceAgent, SrtpPolicy globalSrtpPolicy, ISipTelemetrySink? telemetry, ILoggerFactory loggerFactory, …optional)`.
- `StartRegistration(Action<LineState> onStateChange, Action<int>? onReconnecting = null, Action<ReregisterFailReason,int>? onReconnectFailed = null)` @109; `StopRegistration()` @157; `Dispose()` exists; class is `IDisposable`.
- Refresh-Loop treibt sich aus `EffectiveExpiresSeconds` der Registration-Response (kurz → schnelle Zyklen). Delay via hard-codiertem `Task.Delay` (kein injizierbarer Clock → Befund F003).
- Fake-Muster verifiziert in `tests/CalloraVoipSdk.Core.IntegrationTests/SipLineChannelUnregisterTests.cs:89-150`:
  - `ISipRegistrationService`: `Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest, ct)`, `UnregisterAsync`, `UnregisterAllAsync`, `FetchBindingsAsync`.
  - `SipRegistrationRequest` hat `StartCSeq` (int) + `ExistingCallId` (string?). `SipRegistrationResult` = `{ CallId, StatusCode, EffectiveExpiresSeconds, ContactUri, Authenticated, NextCSeq }`.
  - `ISipCallSignalingService`: events `IncomingInvite`/`OutboundCallStarted`, `InviteAsync`, `SubscribeAsync`, `Dispose`.
  - `ISdpNegotiator`: `BuildDefaultSdp`, `TryBuildNegotiatedAnswer`, `TryParseMediaParameters`, `IsRemoteHoldSdp`.
  - `SipAccount { Username, Password, SipServer }`; `SrtpPolicy.Optional`; `NullLoggerFactory.Instance`; `iceAgent: null`, `telemetry: null`.
- Alle diese Typen sind `internal` → das Harness (mit `InternalsVisibleTo`) kapselt sie und exponiert public API.

---

## File Structure

```text
tests/CalloraVoipSdk.InteropHarness/Signaling/RecordingRegistrationService.cs   NEU  Fake ISipRegistrationService (kurzer Expires + Historie)
tests/CalloraVoipSdk.InteropHarness/Signaling/NoopCallSignaling.cs              NEU  Fake ISipCallSignalingService + ISdpNegotiator
tests/CalloraVoipSdk.InteropHarness/Signaling/RegisterCycle.cs                  NEU  public Record (Zyklus-Beobachtung)
tests/CalloraVoipSdk.InteropHarness/Signaling/SipRegisterLoopHarness.cs         NEU  public Fixture (verdrahtet SipLineChannel)
tests/CalloraVoipSdk.SoakTests/Signaling/SipRegisterLoopHarnessTests.cs         NEU  Fixture-Durchstich
tests/CalloraVoipSdk.SoakTests/Soak/SignalingRefreshSoakTests.cs                NEU  der Soak
docs/audit/INTEROP_SOAK_AUDIT.md                                               MODIFY  F003 (Testbarkeits-Gap)
```

---

## Task 1: L3-Register-Loop-Fixture (Fakes + Harness)

**Files:** die 4 NEU-Dateien unter `InteropHarness/Signaling/` + Test `SipRegisterLoopHarnessTests.cs`.

- [ ] **Step 1: Failing fixture test** — `tests/CalloraVoipSdk.SoakTests/Signaling/SipRegisterLoopHarnessTests.cs`:

```csharp
using CalloraVoipSdk.InteropHarness.Signaling;

namespace CalloraVoipSdk.SoakTests.Signaling;

public sealed class SipRegisterLoopHarnessTests
{
    [Fact]
    public async Task Run_ShortExpires_RefreshLoopFiresMultipleRegisters()
    {
        await using var harness = SipRegisterLoopHarness.Start(effectiveExpiresSeconds: 2);

        var cycles = await harness.RunAsync(TimeSpan.FromSeconds(6));

        Assert.True(cycles.Count >= 2, $"Zu wenige Register-Zyklen: {cycles.Count}");
        Assert.True(harness.ReachedRegistered, "LineState.Registered wurde nie erreicht.");
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test /home/dbechstein/Projekte/voip/.claude/worktrees/feat+interop-soak-audit/tests/CalloraVoipSdk.SoakTests/CalloraVoipSdk.SoakTests.csproj -f net10.0 --filter SipRegisterLoopHarnessTests`.

- [ ] **Step 3: `RegisterCycle`** — `tests/CalloraVoipSdk.InteropHarness/Signaling/RegisterCycle.cs`:

```csharp
namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>Beobachtung eines einzelnen REGISTER-Aufrufs im Refresh-Loop.</summary>
/// <param name="StartCSeq">CSeq, mit dem dieser REGISTER gesendet wurde.</param>
/// <param name="ExistingCallId">Wiederverwendete Call-ID (null beim ersten Zyklus).</param>
public readonly record struct RegisterCycle(int StartCSeq, string? ExistingCallId);
```

- [ ] **Step 4: `RecordingRegistrationService`** — `tests/CalloraVoipSdk.InteropHarness/Signaling/RecordingRegistrationService.cs` (internal; implementiert das internal `ISipRegistrationService`, zeichnet jeden Aufruf auf, antwortet mit kurzem Expires):

```csharp
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling.Registration;

namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>
/// Fake-<c>ISipRegistrationService</c> für den Signaling-Soak: antwortet sofort mit 200/OK und
/// einem kurzen <c>EffectiveExpiresSeconds</c> (treibt den echten Refresh-Loop schnell) und
/// zeichnet CSeq + Call-ID jedes Zyklus auf.
/// </summary>
internal sealed class RecordingRegistrationService : ISipRegistrationService
{
    private readonly int _expiresSeconds;
    private readonly List<RegisterCycle> _cycles = new();
    private readonly object _gate = new();

    public RecordingRegistrationService(int expiresSeconds) => _expiresSeconds = expiresSeconds;

    public IReadOnlyList<RegisterCycle> Cycles
    {
        get { lock (_gate) return _cycles.ToArray(); }
    }

    public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
    {
        lock (_gate) _cycles.Add(new RegisterCycle(request.StartCSeq, request.ExistingCallId));
        return Task.FromResult(new SipRegistrationResult
        {
            CallId = "soak-call-id",
            StatusCode = 200,
            EffectiveExpiresSeconds = _expiresSeconds,
            ContactUri = "sip:soak@127.0.0.1",
            Authenticated = true,
            NextCSeq = request.StartCSeq + 1,
        });
    }

    public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        Task.FromResult(new SipRegistrationResult
        {
            CallId = "soak-call-id", StatusCode = 200, EffectiveExpiresSeconds = 0,
            ContactUri = "sip:soak@127.0.0.1", Authenticated = true, NextCSeq = request.StartCSeq + 1,
        });

    public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        UnregisterAsync(request, ct);

    public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        RegisterAsync(request, ct);
}
```

- [ ] **Step 5: `NoopCallSignaling`** — `tests/CalloraVoipSdk.InteropHarness/Signaling/NoopCallSignaling.cs` (two internal fakes; a REGISTER-only soak never calls their methods):

```csharp
using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>Fake-Signaling-Service; im REGISTER-Soak nie aufgerufen.</summary>
internal sealed class NoopCallSignaling : ISipCallSignalingService
{
    public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }
    public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted { add { } remove { } }

    public Task<ISipCallSession> InviteAsync(SipInviteRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public void Dispose() { }
}

/// <summary>Fake-SDP-Negotiator; im REGISTER-Soak nie aufgerufen.</summary>
internal sealed class NoopSdpNegotiatorStub : ISdpNegotiator
{
    public string BuildDefaultSdp(IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? options = null) => "v=0";
    public string? TryBuildNegotiatedAnswer(string remoteOffer, IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? localOptions = null) => null;
    public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint) => null;
    public bool IsRemoteHoldSdp(string? sdp) => false;
}
```

- [ ] **Step 6: `SipRegisterLoopHarness`** — `tests/CalloraVoipSdk.InteropHarness/Signaling/SipRegisterLoopHarness.cs` (public fixture; wires the real `SipLineChannel`, hides internals):

```csharp
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>
/// L3-Fixture: treibt den echten <see cref="SipLineChannel"/>-Registrierungs-Loop mit einem
/// kurzen Expires (schnelle Re-REGISTER-Zyklen) gegen einen aufzeichnenden Fake-Registrar.
/// Exponiert die beobachteten Zyklen und den erreichten Registrierungs-Zustand public.
/// </summary>
public sealed class SipRegisterLoopHarness : IAsyncDisposable
{
    private readonly SipLineChannel _channel;
    private readonly RecordingRegistrationService _registrar;
    private volatile bool _reachedRegistered;

    private SipRegisterLoopHarness(SipLineChannel channel, RecordingRegistrationService registrar)
    {
        _channel = channel;
        _registrar = registrar;
    }

    /// <summary>True, sobald der Loop mindestens einmal <see cref="LineState.Registered"/> meldete.</summary>
    public bool ReachedRegistered => _reachedRegistered;

    /// <summary>Startet den Registrierungs-Loop; <paramref name="effectiveExpiresSeconds"/> steuert die Zyklus-Rate.</summary>
    public static SipRegisterLoopHarness Start(int effectiveExpiresSeconds)
    {
        var registrar = new RecordingRegistrationService(effectiveExpiresSeconds);
        var channel = new SipLineChannel(
            new SipAccount { Username = "soak", Password = "p", SipServer = "sipconnect.example" },
            "InteropHarness/1.0",
            registrar,
            new NoopCallSignaling(),
            new NoopSdpNegotiatorStub(),
            iceAgent: null,
            SrtpPolicy.Optional,
            telemetry: null,
            NullLoggerFactory.Instance);

        var harness = new SipRegisterLoopHarness(channel, registrar);
        channel.StartRegistration(state =>
        {
            if (state == LineState.Registered) harness._reachedRegistered = true;
        });
        return harness;
    }

    /// <summary>Lässt den Loop <paramref name="duration"/> lang laufen und gibt die beobachteten Zyklen zurück.</summary>
    public async Task<IReadOnlyList<RegisterCycle>> RunAsync(TimeSpan duration)
    {
        await Task.Delay(duration);
        return _registrar.Cycles;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.StopRegistration();
        await Task.Delay(50); // best-effort Abschluss des Unregister-Round-Trips
        _channel.Dispose();
    }
}
```

- [ ] **Step 7: Run fixture test → PASS.** `... --filter SipRegisterLoopHarnessTests`. Expected: ≥2 cycles in 6 s, `ReachedRegistered` true. If cycle count is 0/1, the refresh delay is longer than expected — report the observed count (tells us the real `ComputeRefreshDelay` clamp), do not hack.

- [ ] **Step 8: Commit:**
```bash
git add tests/CalloraVoipSdk.InteropHarness/Signaling/ tests/CalloraVoipSdk.SoakTests/Signaling/
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 3.1 — L3 SipRegisterLoopHarness (echter SipLineChannel-Refresh-Loop, Fake-Registrar)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 2: Langzeit-Signaling-Soak

**Files:** Test `tests/CalloraVoipSdk.SoakTests/Soak/SignalingRefreshSoakTests.cs`.

- [ ] **Step 1: Soak test:**

```csharp
using CalloraVoipSdk.InteropHarness.Metrics;
using CalloraVoipSdk.InteropHarness.Signaling;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class SignalingRefreshSoakTests
{
    [Fact]
    public async Task LongRegisterLoop_CallIdStable_CSeqMonotonic_NoLeak_NoSilentDrop()
    {
        var sampler = new ResourceSampler();
        var before = sampler.Capture();

        IReadOnlyList<RegisterCycle> cycles;
        await using (var harness = SipRegisterLoopHarness.Start(effectiveExpiresSeconds: 2))
        {
            cycles = await harness.RunAsync(TimeSpan.FromSeconds(20));

            // Kein Silent-Drop: der Loop hat wiederholt registriert und den Registered-Zustand erreicht.
            Assert.True(harness.ReachedRegistered, "LineState.Registered nie erreicht.");
        }

        Assert.True(cycles.Count >= 5, $"Zu wenige Re-REGISTER-Zyklen: {cycles.Count}");

        // CSeq streng monoton steigend (RFC 3261 §10.2.4).
        for (var i = 1; i < cycles.Count; i++)
            Assert.True(cycles[i].StartCSeq > cycles[i - 1].StartCSeq,
                $"CSeq nicht monoton: {cycles[i - 1].StartCSeq} → {cycles[i].StartCSeq}");

        // Call-ID stabil ab dem 2. Zyklus (erster ist frisch/null, danach wiederverwendet).
        for (var i = 1; i < cycles.Count; i++)
            Assert.Equal("soak-call-id", cycles[i].ExistingCallId);

        // Kein Ressourcen-Leak über die Zyklen (Sockel nach Dispose vs. vor Start).
        var after = sampler.Capture();
        var trend = TrendAssertions.NoUpwardDrift(
            new[] { before, after }, s => s.ManagedBytes, toleranceRatio: 0.50);
        Assert.False(trend.HasDrift, trend.Detail);
    }
}
```

- [ ] **Step 2: Run.** ~20 s. Expected green: ≥5 cycles, CSeq monotonic, Call-ID stable, no leak.

- [ ] **Step 3 — CRITICAL if a real anomaly (CSeq resets, Call-ID churns, silent stop, leak):** Do NOT weaken assertions. A genuine signaling defect is a finding — report DONE_WITH_CONCERNS with the cycle dump, record it as `Interop-Abweichung`/`Soak-Leak` in the register. Only commit if genuinely green.

- [ ] **Step 4: Commit (only if green):**
```bash
git add tests/CalloraVoipSdk.SoakTests/Soak/SignalingRefreshSoakTests.cs
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 3.2 — Langzeit-Signaling-Soak (Re-REGISTER-Zyklen: Call-ID/CSeq/kein-Leak)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 3: Testbarkeits-Befund F003 (fehlender Zeit-Seam) dokumentieren

**Files:** `docs/audit/INTEROP_SOAK_AUDIT.md` (eine neue Zeile).

- [ ] **Step 1: Add F003 row** to the table in `docs/audit/INTEROP_SOAK_AUDIT.md` (after F002):

```markdown
| F003 | Facade-Coupling-Gap | Phase 3 (Signaling-Soak-Recon) | Langzeit-Signaling ist nur real-time-beschleunigt testbar (kurzer Expires), NICHT echt zeitgerafft — `SipLineChannel`-Refresh-Loop und `SipSessionTimerManager` nutzen hart `Task.Delay`, es gibt keine `ITimeProvider`/Clock-Abstraktion | Fehlende Zeit-Abstraktion im Signaling-Layer → Soaks mit 100+ Zyklen bräuchten reale Stunden | `src/Core/Infrastructure/Sip/Adapters/SipLineChannel.cs` (RegisterAsync-Loop, `Task.Delay`) · `src/Core/Infrastructure/Sip/Signaling/SessionTimers/SipSessionTimerManager.cs` (`RunScheduleAsync`, `Task.Delay`) | Kein Fix — dokumentiert. Optionaler `ITimeProvider`-Seam (~60 Z., 2 Injektionspunkte) würde echtes Zeit-Rafting ermöglichen; separates Paket | Info (Testbarkeit) | dokumentiert |
```

- [ ] **Step 2: Commit:**
```bash
git add docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "$(cat <<'MSG'
docs(interop-soak): F003 — Testbarkeits-Gap (kein ITimeProvider-Seam im Signaling-Layer)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Phase-3-Abschluss

- [ ] Voller Build → 0/0. · Volle SoakTests (net9+net10) → alle grün (+ ggf. 1 skipped aus F002).
- [ ] `origin/main`-Check + ggf. mergen.

**Damit:** alle vier Soak-Fokusse abgedeckt (Leaks, Concurrency, Media-Qualität, Langzeit-Signaling — letzterer beschleunigt, mit dokumentiertem Zeit-Seam-Gap F003). Nächste große Ebene: **echte Interop (Asterisk L4)**.

## Spec-Abdeckung (Self-Review)

- Soak-Fokus „Langzeit-Signaling" (§8) → Task 2. · L3-Ebenen-Fixture (§4.1) → Task 1. · Facade-Coupling-Gap-Doku (§4.2) → Task 3 (F003).
- Kein autonomes Fixen (§3): F003 nur dokumentiert; der `ITimeProvider`-Seam wird NICHT gebaut (SDK-Änderung). · Kein Test-Fudging → Task 2 Step 3.
- **Nicht in Phase 3:** echtes Zeit-Rafting (braucht SDK-Seam), Session-Timer-Soak (optional später), Interop-Ebenen L4.
