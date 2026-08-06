# ADR-064: Per-Line SIP Mutual TLS with a Certificate from Memory

Status: Accepted
Date: 2026-08-06

## Context

Outbound SIP over TLS/WSS presented **no** client certificate — a basic capability was missing, so the SDK
could not register against a registrar that requires mutual TLS (`verify_client=yes`). Contact-center and
carrier trunks increasingly require it, and a single client often terminates **several lines** (accounts) to the
**same** registrar that must present **different** client identities.

The reference stacks bind the TLS/authentication identity to the *line/account*, not to the client as a whole.
Asterisk `pjsip` keys each account to a `transport=`/endpoint identity, stamps it on every request the account
originates, and keys its connections by that identity, so two accounts to the same host are isolated; SIPSorcery
and PJSIP behave the same. A client-wide single certificate cannot express that.

Consumers frequently hold certificates **in memory** (from a secret store, HSM export, or a rotated in-process
cache), not as a file path. The existing `TlsConfiguration` only accepted a `CertificatePath`.

Layering constraint (R1): the map from a SIP account to its per-line TLS identity is application state; it must
not live in `Core.Infrastructure` (which would be a Domain→Infrastructure inversion).

## Decision

Adopt the reference model: **the TLS identity is bound to the line**, carried through the whole request chain,
and used to key the connection pool.

1. **Client certificate over outbound TLS/WSS**, presented only when the registrar sends a `CertificateRequest`
   (RFC 8446 §4.4.2) — so behaviour against registrars that do not request one is byte-identical to 4.7.
2. **Certificate from memory:** `TlsConfiguration.ClientCertificate` (an `X509Certificate2`) takes precedence
   over `CertificatePath` and is **caller-owned** — the SDK never disposes it. Only a file-loaded certificate
   stays SDK-owned/disposed.
3. **Identity-keyed connection pool:** the pool key becomes `(transport, addr:port, identity)`, so two lines to
   the same endpoint present their own identity over separate connections.
4. **The identity is stamped on every request the line originates** — REGISTER, INVITE and all in-dialog
   requests, MESSAGE, PUBLISH — by threading `ConnectOptions.LineTls` → a per-account application map →
   `SipLineChannel` → the request/dialog chain.
5. **Per-connect override:** `ConnectOptions.LineTls` overrides the client-wide identity for that connect
   boundary; `null` keeps the client-wide identity.
6. Per-line `ExpectedSipDomain` / `TrustMode` stay **fail-closed** (RFC 5922) — see ADR on SIP-TLS trust.

The per-account TLS map lives in the application layer (`VoipClient`), consumed synchronously by the line
manager, so no Core→Application dependency is introduced.

## Consequences

- **Additive public API:** `ConnectOptions.LineTls` and `TlsConfiguration.ClientCertificate` — no removals,
  `PublicApi.approved.txt` only grew. Existing single-identity configurations are unaffected.
- **Two lines to the same registrar are isolated**, each presenting its own certificate over its own pooled
  connection.
- **No on-wire change** for registrars that do not request a client certificate.
- **Ownership is explicit:** an in-memory certificate is the caller's to dispose; a file-loaded one is not.
- **Live `verify_client=yes` interop is not locally CI-verifiable** and is therefore not claimed — the change is
  covered by wire tests against a loopback mutual-TLS server (two lines → two certificates at the same endpoint).

## Alternatives considered

- **One client-wide identity.** Rejected: it cannot isolate two lines to the same registrar — the core use case.
- **Certificate only from a file path.** Rejected: consumers commonly hold certificates in memory/a store; a
  path-only API forces a temp-file round-trip and a disposal ambiguity.
- **Hold the per-line identity map in Core.** Rejected: it is application state; keeping it there inverts the DDD
  layering (R1). The application-layer map keyed by account keeps Core protocol-only.
