# ADR-058: Layered L0–L4 Test Model with Interop/Soak Harness and a Document-Don't-Fix Register

Status: Accepted
Date: 2026-07-21

## Context

The in-process integration suite covers SIP/RTP/SRTP/SDP/ICE/RTCP deeply, but only "in-process
gegen den eigenen Stack und Fakes" — there was **no real foreign-stack interop and no
duration/load harness** (`2026-07-21-interop-soak-audit-design.md` §1: `SoakTests` existed but was
empty, only `Skeleton_IsWired`). Two blind spots follow: (1) wire-conformance against real peers
(Asterisk/FreeSWITCH/3CX/Fritzbox) is unproven, and (2) stability over time/load (leaks, quality
drift, concurrency, long-lived signaling) is untested. A flat "facade E2E" approach would find
symptoms but not locate them: a media defect surfacing through `VoipClient` gives no line to fix.

### Verified current state

- **The layer taxonomy is codified.** `ENGINEERING_RULES.md` K8 defines L0 Wire → L1 Security →
  L2 Media → L3 Signaling → L4 Facade/Interop, with the full table in
  `docs/audit/2026-07-21-interop-soak-audit-design.md` §4.1. New functionality is tested at the
  lowest sensible level so a defect is isolated where it originates.
- **The trait taxonomy is real and CI-wired.** `[Trait("Category", …)]` uses `SoakShort`,
  `SoakLong`, and `Interop` (e.g. `tests/CalloraVoipSdk.SoakTests/Soak/MediaQualityDriftSoakTests.cs`,
  `tests/CalloraVoipSdk.InteropTests/**`). CI runs them on separate tracks: `ci.yml` main suite
  filters `Category!=SoakLong&Category!=Interop&…!~ArchitectureTests` (L49); a dedicated
  `ci.yml` `interop` job runs `Category=Interop` against Dockerised Asterisk (L60/L83); `soak.yml`
  runs `Category=SoakLong` nightly with artifact upload.
- **The harness is a shared, layered foundation.** `tests/CalloraVoipSdk.InteropHarness/`
  provides level fixtures with fault-injection hooks, a metric sampler (RAM/handles/threads/sockets
  + RTCP) that asserts on **trends not snapshots** (`Metrics/TrendAssertions.cs`), scenario
  building blocks, media verifiers, and an audit sink (`Audit/SoakArtifactSink.cs`) — reused by
  both `SoakTests` and `InteropTests`.
- **The deliverable is a living register, git-tracked.** `docs/audit/INTEROP_SOAK_AUDIT.md` is the
  primary artifact (design §2), versioned via the `!docs/audit/` `.gitignore` exception. It has
  driven 11+ findings (F001–F011) with typed root causes: `Interop-Abweichung`, `Soak-Leak`,
  `Media-Defekt`, `Wire-Robustheit`, `Facade-Coupling-Gap`.
- **Real interop is proven, not aspirational.** The Asterisk matrix runs "29 grün, 0 Skip" against
  `andrius/asterisk:22` via Testcontainers (register/call/codec/SRTP-SDES/DTMF/hold/transfer/
  session-timer/early-media), plus a bidirectional two-leg bridged-media suite ("8 harte
  `[DockerRequiredFact]`, 0 Skip") with byte-exact content verification both directions.

### Coverage-honesty on record

The register states its own limits: audio is injected via `IMediaSender` (no microphone, no
codec-encode) so it measures the transport/media path, not acoustic quality; MOS is `null` against
Asterisk (no RTCP-XR); one DTMF-in-early-dialog claim is SDK-side-only, not peer-confirmed. These
are recorded as coverage caveats, not swept under a green run.

## Decision

Adopt a **layered test architecture** with a shared harness and a document-don't-fix register:

1. **Test at the lowest sensible layer (L0–L4).** L0–L3 are deterministic loopback + fault
   injection (CI-friendly); L4 is real foreign-stack interop. A defect is isolated at the layer it
   originates, with a zeilen-genaue Fundstelle — not diagnosed through the facade.
2. **Non-happy-path is first-class at every layer** — reject 486/603, auth 401/407, CANCEL before
   answer, timeout, BYE-race, malformed SDP, refused re-INVITE — designed into the matrix, not
   appended.
3. **The living register (`INTEROP_SOAK_AUDIT.md`) is the deliverable, and it is document-only.**
   No autonomous bugfixing inside the audit package: every finding is `FID` + evidence + symptom +
   root-cause category + `Datei:Zeile` + fix-proposal + severity + status; every SDK fix is a
   separate, individually approved package.
4. **CI runs the tiers on separate tracks by trait.** PR CI runs loopback + short soak + Dockerised
   interop and excludes `SoakLong`/`Interop`/`ArchitectureTests` from the main suite; long soaks are
   nightly (`soak.yml`); the arch gate is its own step. Public repo → standard runners, no minute
   cost; Docker-Hub rate limits avoided via GHCR mirroring.
5. **Soak asserts on trends, not snapshots** — resource sockels stay flat over N calls; jitter/loss
   do not drift; concurrency shows no deadlock/race — measured by the harness metric sampler.
6. **Facade-Coupling-Gaps are a named finding type.** Wiring the facade implicitly provides
   (media↔signaling coupling, SDP handover, dispose ordering, DI defaults) is surfaced by building
   sub-facade fixtures and recorded — documented, not fixed.

### Crux design pieces

- **Isolate at the layer, prove at the edge.** L0–L3 give line-precise root cause; L4 proves real
  wire conformance. Both are needed and are different jobs.
- **Register is the product, tests are the evidence.** The audit package ships findings +
  reproducing tests; it deliberately does not ship fixes, so a defect and its fix stay separately
  reviewable and scope stays honest (ENGINEERING_RULES).
- **Trait-gated CI tiers.** `SoakShort`/`SoakLong`/`Interop` let a PR stay fast while nightly and
  Docker jobs carry the heavy/slow coverage — no all-or-nothing suite.
- **Trend over snapshot.** A soak that passed a single end-state check would miss a slow leak; the
  harness asserts monotonic-flatness (`TrendAssertions.NoUpwardDrift`).

## Consequences

Positive: defects land with a layer and a line, not just a facade symptom; real-peer interop is
proven with a green Asterisk matrix and byte-exact two-leg media; PR CI stays cheap while nightly/
Docker tracks carry the slow coverage; the register is a durable, git-tracked backlog of typed
findings that fed real fixes (F005/F006/F008/F009/F010/F011). The two harness projects reuse one
foundation instead of forking fixtures.

Tradeoffs: the harness deliberately breaches encapsulation to test under the facade —
`InternalsVisibleTo` for `InteropHarness` (F001) — a documented coupling cost. Some capabilities are
layer-bound and cannot be measured low: live RTT is an L3-orchestrator feature, so it reads as a
static hint at bare-L2 (F004) — RTT assertions belong on L3+. Signaling soaks cannot be truly
time-warped without an `ITimeProvider` seam the signaling layer lacks (F003), so long signaling
runs are only real-time-accelerated. External peers (3CX/Fritzbox) are opt-in/local, not CI. The
register is a judgement/documentation layer, not a gate — its findings do not fail the build.

## Guardrails

- New functionality is tested at the lowest sensible layer (L0–L4, K8); non-happy-path is part of
  the matrix, not an afterthought.
- The interop/soak audit package is **document-only** — no autonomous fixing; each fix is a
  separate approved package.
- Every register finding carries `FID` / evidence / symptom / root-cause / `Datei:Zeile` /
  fix-proposal / severity / status; coverage limits are stated, not implied.
- CI tiers stay trait-gated: PR = loopback + short soak + Docker interop; `SoakLong` nightly;
  `Interop` on its own job; arch gate a separate step; external peers opt-in/local.
- Soak assertions are trend-based (flat resource sockel, no quality drift), not single snapshots.
- The register stays git-tracked under the `!docs/audit/` exception.

## Sources
- Docs: docs/audit/2026-07-21-interop-soak-audit-design.md (§1 Ist-Zustand, §2 register, §3
  Nicht-Ziele, §4.1 L0–L4, §4.2 Facade-Coupling-Gap, §5 trait gating, §8 soak),
  docs/audit/INTEROP_SOAK_AUDIT.md (F001–F011, coverage notes), ENGINEERING_RULES.md (K8 L0–L4)
- Code: tests/CalloraVoipSdk.InteropHarness/ (Metrics/TrendAssertions.cs, Audit/SoakArtifactSink.cs),
  tests/CalloraVoipSdk.SoakTests/Soak/*.cs (SoakShort/SoakLong traits),
  tests/CalloraVoipSdk.InteropTests/**/*.cs (Interop trait, Asterisk matrix + two-leg media),
  .github/workflows/ci.yml (L45 arch gate, L49 main-suite filter, L60/L83 interop job),
  .github/workflows/soak.yml (nightly SoakLong), .gitignore (L104 `!docs/audit/`)
- Marker / finding types: F001–F011; Interop-Abweichung / Soak-Leak / Media-Defekt /
  Wire-Robustheit / Facade-Coupling-Gap
