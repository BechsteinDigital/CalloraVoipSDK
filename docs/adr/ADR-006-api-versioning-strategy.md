# ADR-006: API Versioning and Compatibility Strategy

Status: Accepted
Date: 2026-04-14
Owners: Core SDK Team
Related: CORE-114, CORE-215

## Context

The SDK is approaching the first official `1.0` release and will be consumed as a modular product.
Breaking changes after adoption must be intentional, documented and detectable before merge.

## Decision

1. Versioning model
- The repository uses Semantic Versioning (`MAJOR.MINOR.PATCH`).
- Until `1.0.0`, `0.x` may evolve quickly, but all public API changes must still be explicit and reviewed.
- `1.0.0` is the first stable contract release.

2. Breaking-change definition
- Removing or renaming a public type/member is breaking.
- Changing method signatures, parameter types/order, return type, or behavior-contract in incompatible form is breaking.
- Tightening nullability in a way that invalidates existing consumer code is treated as breaking.

3. Deprecation workflow
- Preferred path: mark old API with `[Obsolete(..., false)]` first.
- Keep obsolete API for at least one minor cycle before removal.
- Removal of obsolete API increments `MAJOR` (after `1.0`).

4. API surface gate
- Intent: any public API change is detectable before merge and requires an explicit, reviewed baseline update in the same PR.
- **IMPLEMENTED (2026-07-28):** `PublicApiSurfaceTests` in the `CalloraVoipSdk.ArchitectureTests`
  project reflects over the exported (public/protected) API and diffs it against the checked-in
  baseline `tests/CalloraVoipSdk.ArchitectureTests/PublicApi.approved.txt`. It runs in the same
  dedicated CI step as the other architecture gates.
  - **Scope — captured:** the two assemblies an external consumer references,
    `CalloraVoipSdk.Client` (public facade: `VoipClient`/`IVoipClient`, `WebRtc`, `Hosting`,
    `DependencyInjection`, `Modules`) and `CalloraVoipSdk.Core` (public domain/application/config
    types reachable through the facade). Per exported type one `TYPE …` line plus one line per
    public/protected member (methods with parameter+return types, properties with type+accessors,
    events, fields, enum values), sorted `OrdinalIgnoreCase` for reproducibility. Compiler-generated
    members (property/event accessors, `<…>`/backing-field names, record `EqualityContract`/
    `PrintMembers`) are filtered out. Record-emitted public API such as `Deconstruct` is kept, since
    a consumer can call it.
  - **Scope — NOT captured:** internal types (the test project is deliberately absent from Core's
    `InternalsVisibleTo`, so it sees exactly the consumer view), attribute usage/`[Obsolete]` flags,
    XML-doc text, nullability annotations, and optional plug-in assemblies (`CalloraVoipSdk.Audio.*`).
    It is a *name+signature* surface diff, not a full binary-compatibility analyzer.
  - **Baseline update (intentional API change):**
    `UPDATE_PUBLIC_API=1 dotnet test tests/CalloraVoipSdk.ArchitectureTests/CalloraVoipSdk.ArchitectureTests.csproj`
    regenerates `PublicApi.approved.txt`; the regenerated file is reviewed in the same PR. Without
    the env var the test compares and fails, listing added (additive) and removed/changed
    (potentially breaking, §2) signatures.
- Complementary governance (unchanged): the rest of the `CalloraVoipSdk.ArchitectureTests` suite
  (`EngineeringRulesTests` + `SourceScan`, shrink-only baselines) enforces the engineering rules
  (layering, file size, silent-catch, sync-over-async); `[Obsolete]` discipline + CHANGELOG remain
  the deprecation-workflow controls that the surface diff itself does not encode.

5. Changelog discipline
- `CHANGELOG.md` is mandatory for consumer-visible changes.
- Breaking, deprecated, and additive API changes are recorded per release.
- `main` is versioned as the next `MINOR` prerelease (`X.Y.0-preview`, set in `src/Directory.Build.props`), so
  no build off `main` is mistaken for a stable release; the stable version is pinned only by the release tag.
- The `[Unreleased]` section is maintained continuously — every consumer-visible change lands there in the
  same PR that introduces it, not retroactively at release time.

6. Feature-claim gating
- A capability that spans multiple internal slices (e.g. SDP negotiation + media transport + public API) is
  announced as a consumer-visible **feature** — in `CHANGELOG.md`, docs, or marketing — only once **all**
  layers work together end-to-end and the public API a consumer would call is present.
- Partial internal groundwork (some slices merged, not yet usable) is recorded under an explicit
  "Internal / in progress (not yet consumer-visible)" heading, never as a shipped feature.
- Rationale: a consumer must never read a feature claim for something they cannot yet use. Example: multi-track
  WebRTC is not claimed until offer, answer, and the media runtime interoperate (SDP-only groundwork is not a
  feature). The DEV/PO `DONE`-claim rules (code + tests + scope coverage) apply on top of this gate.

## Consequences

- API drift is visible immediately in tests and code review.
- Additive changes stay possible, but are explicit.
- Existing consumers get predictable migration windows via `[Obsolete]` before removals.

## Errata (2026-07-27)

During the docs consolidation / ADR backfill, §4's claimed automated API-surface gate
(`PublicApiSurfaceTests` vs. `PublicApi.approved.txt`) was verified against the code and found
**not implemented** (no such test, baseline, or `CalloraVoipSdk.Core.Tests` project). The
decision's *intent* stands, but the enforcement described was aspirational. §4 was corrected
to state the actual governance (review + `[Obsolete]` + CHANGELOG; `ArchitectureTests` for
engineering-rule gates). Building a real public-API-surface gate remained open follow-up work.

## Errata (2026-07-28)

The follow-up work from the 2026-07-27 erratum is now done. A reflection-based
`PublicApiSurfaceTests` and its baseline `PublicApi.approved.txt` were added to the existing
`CalloraVoipSdk.ArchitectureTests` project (rather than a new `CalloraVoipSdk.Core.Tests` project —
the architecture-test project already hosts the shrink-only governance gates and now carries the
first `ProjectReference`s to `CalloraVoipSdk.Client`/`CalloraVoipSdk.Core` for the reflection).
§4 above now describes the gate as built, including its exact scope and the
`UPDATE_PUBLIC_API=1` regeneration workflow. The initial baseline captured 180 exported types /
1122 members across the two consumer-facing assemblies. Observation recorded during baseline
generation (not a change made here): seven `CalloraVoipSdk.Core.Infrastructure.*` types are
currently exported publicly (e.g. `TlsConfiguration`, `SipDomainCertificateValidator`, the SIP
observability records/`ISipTelemetrySink`, `AesGcmRecordingEncryptionProvider`), which is in
tension with the "Infrastructure stays an internal implementation detail" rule; the gate now makes
any further such drift visible. Deciding whether to `internal`-ise or intentionally keep them is
separate follow-up work.
