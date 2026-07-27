# Reference

Consolidated, current technical reference for CalloraVoipSdk. These are the durable factual
references an engineer needs; the *why* behind the architecture lives in the ADRs
([`../adr/README.md`](../adr/README.md)), and the buyer-facing narrative in
[`../handover/README.md`](../handover/README.md).

| Document | What it covers |
|----------|----------------|
| [decision-inventory.md](decision-inventory.md) | Mapping of the 113 archived engineering logs to decision clusters — the provenance index behind ADR-013…061. |
| [semver-policy.md](semver-policy.md) | Versioning scheme, increment rules, release channels, per-package versioning (current version `4.6.0-preview.2`). See ADR-006. |
| [plugin-contract.md](plugin-contract.md) | Plugin/module contract v1 (extension points, module registry). See ADR-007, ADR-008, ADR-059. |
| [websocket-protocol.md](websocket-protocol.md) | Realtime WebSocket protocol surface. |

> The current module map and RFC-conformance summary are maintained as code-grounded buyer pages
> under `../handover/technical/` (`architecture.md`, `protocol-conformance.md`) rather than as
> separately-drifting reference docs. Raw historical status/RFC documents are retained under
> `../archive/` for provenance.
