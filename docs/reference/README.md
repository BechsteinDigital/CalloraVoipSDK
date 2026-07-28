# Reference

Consolidated, current technical reference for CalloraVoipSdk. These are the durable factual
references an engineer needs; the *why* behind the architecture lives in the ADRs
([`../adr/README.md`](../adr/README.md)), the maintainer's operating manual in
[`../../MAINTAINING.md`](../../MAINTAINING.md), and the consumer documentation under
[`../portal/`](../portal/index.md).

| Document | What it covers |
|----------|----------------|
| [decision-inventory.md](decision-inventory.md) | Mapping of the 113 archived engineering logs to decision clusters — the provenance index behind ADR-013…061. |
| [semver-policy.md](semver-policy.md) | Versioning scheme, increment rules, release channels, per-package versioning (current version `4.6.0`). See ADR-006. |
| [plugin-contract.md](plugin-contract.md) | Plugin/module contract v1 (extension points, module registry). See ADR-007, ADR-008, ADR-059. |
| [websocket-protocol.md](websocket-protocol.md) | Realtime WebSocket protocol surface. |

> The current module map lives in [`../portal/architecture/overview.md`](../portal/architecture/overview.md)
> and MAINTAINING.md §1; the RFC-conformance picture is the [interop matrix](../portal/interop/matrix.md)
> plus the per-RFC references carried in the ADRs and in [`../../CHANGELOG.md`](../../CHANGELOG.md) —
> deliberately not duplicated here, where it would drift. Historical status/RFC documents and the
> archived engineering logs are kept outside the published tree for provenance.
