# ADR-014: DDD Layer Direction Enforced by Gated Shrink-Only Baselines

Status: Accepted
Date: 2026-07-08

## Context

The project mandates DDD with a hard "keine Schichtverletzungen" rule (`Domain → Application →
Infrastructure`, `Infrastructure/*` an internal implementation detail). A hand-maintained rule is
worthless without a mechanism that (a) fails new violations and (b) forces legacy debt to shrink
rather than persist. Two 2026-07 runs converged on the enforcement design: the A1 slice
(2026-07-07) removed unimplemented module surfaces from the SDK facade, and B.3 (2026-07-08) shrank
three baselines (layer-segment / nested-type / silent-catch) behaviour- and API-neutrally.

### Verified current state

- The gate is `tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs`
  (`.Domain_und_Application_halten_die_Schichtrichtung_ein()`, L22): it scans `src/Core/Domain` for
  any `using CalloraVoipSdk.Core.(Application|Infrastructure)` / `…Client`, and `src/Core/Application`
  for any `using …Infrastructure` / `…Client`, then calls `SourceScan.AssertMatchesBaseline(...)`.
- **`LayeringBaseline = []`** and **`LayerSegmentBaseline = []`** are both empty (L20, L63) — Domain
  and Application currently hold the layer direction with zero exceptions.
- The baseline mechanic ("Baselines dürfen nur schrumpfen; veraltete Einträge schlagen ebenfalls
  fehl") is normatively documented in `ENGINEERING_RULES.md` (Baseline-Mechanik, R1–R6) and the
  gates run in CI as a dedicated step **before** the rest of the suite
  (`.github/workflows/ci.yml`).
- Sibling gates in the same file enforce the wider rule family: namespace-segment = folder-layer
  (R2, `.Schicht_Segment_des_Namespace_passt_zur_Ordner_Schicht()` L65, HARD-G1 drift closed),
  ≤1000 lines (R3, L114), no private nested types (R4, L131), no silent catch (R5, L179), no
  sync-over-async (R6, L211).

## Decision

Encode every mechanical `ENGINEERING_RULES` rule as a source-tree scan in `EngineeringRulesTests`
compared against a **shrink-only baseline of known debt**:

1. New violations fail the build. Fixed baseline entries must be removed from the baseline, or the
   test fails on the stale entry — debt can only shrink, never linger silently.
2. The DDD layer-direction rule (R1) is the primary instance: its baseline is empty, so *any* new
   `Domain→Application/Infrastructure` or `Application→Infrastructure/Client` `using` fails CI.
3. Refactoring that reduces debt is scoped as behaviour- and API-neutral baseline shrink (B.3:
   `git mv` RTCP codec keeping namespace → zero consumer changes; extract nested `MediaActivity` /
   `LearnedPublicContact` to top-level `internal`; add logging to silent catches).
4. The SDK facade is kept honest by removing surfaces the Core does not implement (A1: dropped
   `IConferencingModule`/`IRealtimeModule`/`IWebSocketAudioTransportModule` from `IVoipClient`),
   rather than shipping dead abstraction layers.

### Crux design pieces

- **Shrink-only baseline.** Both directions of drift fail: a new violation *and* a stale baseline
  entry. This is what turns a documented rule into a ratchet.
- **Gate before suite.** Architecture gates run first in CI, so a layering regression fails fast and
  independently of behavioural tests.
- **Empty is the target.** `LayeringBaseline`/`LayerSegmentBaseline` reaching `[]` is the recorded
  end-state; the follow-up ADR (ICallRegistry / K4) is what got the layering baseline to empty.

## Consequences

Positive: the DDD layer direction is machine-checked on every push, not review-dependent; legacy
debt is inventoried and monotonically decreasing; the facade advertises only implemented capability.
The refactoring assessment (2026-07-17) independently measured "0 verifizierte Schichtverletzungen"
against the VoIP peer set, corroborating the gate.

Tradeoffs: the gate is a `using`-regex scan, not full semantic analysis — it catches namespace-level
layer leaks, not e.g. reflection-based coupling. Baseline maintenance is manual: a developer fixing
debt must also delete the baseline entry. R3's 1000-line scan targets `samples/` while the directory
is named `examples/`, so examples are effectively unscanned (known minor gap, documented in
`ENGINEERING_RULES.md`).

**Log↔code divergence:** the B.3 log cites "Core 142/142" and A1 cites a 10-test suite; both are
historical counts. The *baselines-empty* end-state and the gate structure are verified current.

## Guardrails

- `LayeringBaseline` and `LayerSegmentBaseline` stay empty — no new layer or namespace-segment leak.
- Baselines may only shrink; a fixed entry must be removed or the test fails.
- Architecture gates run in CI before the behavioural suite.
- Baseline-shrinking refactors stay behaviour- and API-neutral (or are split off as a Breaking/Major
  package with founder approval, per B.3b `DialOptions`).
- The SDK facade exposes only Core-implemented surfaces.

## Sources
- Logs: docs/archive/agent-log/2026-07-08-dev-b3-layer-hygiene.md,
  docs/archive/agent-log/2026-07-07-dev.md
- Code: tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs (LayeringBaseline L20,
  LayerSegmentBaseline L63, layer gate L22, namespace gate L65), tests/…/SourceScan.cs
  (AssertMatchesBaseline L97), ENGINEERING_RULES.md (R1–R6, Baseline-Mechanik),
  .github/workflows/ci.yml
- Marker: K3, HARD-G1, R1/R2/R3/R4/R5/R6
