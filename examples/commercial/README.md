# Commercial-module examples

These samples demonstrate the **paid** Callora modules and depend on APIs that are **not**
part of the open SDK core:

| Sample | Requires module | Status |
|--------|-----------------|--------|
| `CalloraVoipSdk.Sample.Conference` | Callora.Conference (mixed multi-party conference) | in development — not yet available |
| `CalloraVoipSdk.Sample.RealtimeBridge` | Callora.Realtime / Callora.WebSocket | in development — not yet available |

Because the required modules are not present in this repository, these projects **do not
build against the open core** and are intentionally:

- **not** part of `CalloraVoipSdk.sln`, and
- **not** committed to the repository (their project folders are git-ignored) until the
  corresponding modules ship.

They serve as the reference for what those paid-module APIs are intended to look like. See
the documentation portal's [Commercial Modules](../../docs/portal/commercial/index.md)
section for the current status and early-access contact.
