# ADR-006: API Versioning and Compatibility Strategy

- Status: Accepted
- Date: 2026-04-14
- Owners: Core SDK Team
- Related: CORE-114, CORE-215

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
- `PublicApiSurfaceTests` compares the current API against `tests/CalloraVoipSdk.Core.Tests/PublicApi.approved.txt`.
- Any public API change requires explicit baseline update in the same PR.

5. Changelog discipline
- `CHANGELOG.md` is mandatory for consumer-visible changes.
- Breaking, deprecated, and additive API changes are recorded per release.

## Consequences

- API drift is visible immediately in tests and code review.
- Additive changes stay possible, but are explicit.
- Existing consumers get predictable migration windows via `[Obsolete]` before removals.
