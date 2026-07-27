# ADR-028: DTLS-SRTP Keying Foundation (RFC 5763/5764)

Status: Accepted
Date: 2026-07-14

## Context

The SDK needs SRTP keying that browsers and modern SIP peers actually use: DTLS-SRTP
(RFC 5763/5764), where the two endpoints run a DTLS 1.2 handshake over the media path and
derive the SRTP master keys from the TLS keying-material exporter. Neither .NET's BCL nor the
existing SDES a=crypto path (RFC 4568) covers this — there is no managed DTLS in the platform,
and hand-rolling a DTLS stack is security-critical and not a sensible in-house build (the
reference stacks delegate too: SipSorcery → BouncyCastle, baresip → OpenSSL).

This ADR captures the **cryptographic foundation only** — the handshake engine, certificate,
fingerprint, and key export — independent of how a call decides to use it (see the signaling
and media-wiring ADRs in this cluster).

### Verified current state

- **`DtlsSrtpKeyExporter`** (`src/Core/Infrastructure/Dtls/DtlsSrtpKeyExporter.cs`) exports
  the `EXTRACTOR-dtls_srtp` block of length `2 * (key_len + salt_len)` (RFC 5764 §4.2) and
  splits it into local/remote `SrtpKeyMaterial` by handshake role. The block layout is
  `client_write_key || server_write_key || client_write_salt || server_write_salt`.
- **`DtlsSrtpClient`** / **`DtlsSrtpServer`** (BouncyCastle `DefaultTlsClient`/`Server`
  subclasses) offer/mirror the `use_srtp` extension, pin `DTLSv12` only, and
  `RequiresExtendedMasterSecret() => true` — both because §4.2 export must bind to the full
  transcript (triple-handshake hardening) and because the BC exporter refuses to run without
  it. The client asserts the server mirrors exactly one offered profile
  (`handshake_failure`/`illegal_parameter`) and rejects a non-empty srtp_mki echo
  (RFC 5764 §4.1.3, `illegal_parameter`) **before** certificates or keys are touched.
- **Peer authentication** is by SDP-signaled fingerprint (RFC 5763 §6.7.1), verified in the
  `TlsAuthentication` before key use; a mismatch is a fatal alert, not a soft failure.
- **`DtlsSrtpHandshaker`** wraps the blocking BouncyCastle engine in `Task.Run` with
  cancellation via transport close; **`QueueDatagramTransport`** bridges the RTP socket demux
  and the blocking engine.
- **Exporter-secret hygiene** (`git 1ef5e7c`): the concatenated block carries *both*
  endpoints' write keys and salts. `SplitKeyingMaterial` now copies each half into its own
  buffer and `CryptographicOperations.ZeroMemory`s the aggregate in a `finally`, so the full
  exported secret is no longer pinned on the managed heap for the SRTP contexts' lifetime.
  Verified by `DtlsSrtpKeyExporterTests` (returned halves keep their bytes while the source
  block reads all-zero — proving independent copies + wipe together).

## Decision

Take on **BouncyCastle.Cryptography** as a Core dependency for the DTLS 1.2 handshake, but keep
the **key derivation (RFC 5764 §4.2) and the SRTP engine as in-house code**. BouncyCastle owns
the record layer and handshake state machine; the SDK owns everything from the exporter output
onward.

Concrete foundation contract:

1. **ECDSA P-256 self-signed certificate** per client instance, with an **sha-256** fingerprint
   only (the WebRTC de-facto standard; RFC 8122-recommended). Other fingerprint algorithms are
   deferred until demanded.
2. **`use_srtp` without MKI**, profiles `SRTP_AES128_CM_HMAC_SHA1_80` (preferred) + `_32`,
   mapping onto the existing SRTP engine.
3. **`extended_master_secret` enforced on both sides.**
4. **Fingerprint verified before key use**; profile/MKI mismatch aborts before certificate
   validation.

### Crux

The security boundary is "export, then own it": the concatenated exporter output is treated as a
single secret to be split into independent per-direction copies and immediately wiped — the SDK
never retains the aggregate, and the derived key material lives only in `SrtpKeyMaterial` copies.
Everything the peer can influence (profile, MKI, certificate) is validated *before* any key
touches an SRTP context.

## Consequences

Positive: a tested, RFC-shaped DTLS-SRTP keying core that both the SIP path and the WebRTC path
build on, without an in-house DTLS state machine. The exporter-secret wipe closes a real
key-lifetime leak (aggregate secret was previously heap-pinned for the context lifetime).

Tradeoffs / honest divergence:
- **Interop is BouncyCastle↔BouncyCastle loopback only** at foundation time — no browser or
  foreign-stack handshake has been validated here.
- **Per-context key copies are still not zeroed at teardown** — only the aggregate exporter
  block is wiped. Zeroing the copies needs a disposable `SrtpKeyMaterial` and also touches the
  SDES `ParseInline` path (explicit follow-up in `1ef5e7c`).
- sha-256-only fingerprints; single protection-profile family.

## Guardrails

- Export requires a completed handshake **with** `extended_master_secret`; the exporter must not
  run otherwise.
- Peer profile echo must be exactly one offered profile; srtp_mki must be empty
  (RFC 5764 §4.1.3) — both checked before certificate/key handling.
- Fingerprint verified before any exported key reaches an SRTP context (RFC 5763 §6.7.1).
- The concatenated exporter secret is wiped in a `finally`; returned key material must be
  independent copies, never aliasing views into the wiped block (regression-guarded by
  `DtlsSrtpKeyExporterTests`).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-dtls-srtp-foundation.md`
- Code (graphify-verified): `Dtls/DtlsSrtpKeyExporter.cs` (`.Export()`, `.SplitKeyingMaterial()`),
  `Dtls/DtlsSrtpClient.cs`, `Dtls/DtlsSrtpServer.cs`, `Dtls/DtlsSrtpHandshaker.cs`,
  `Dtls/DtlsCertificate.cs` (`.GenerateEcdsaP256()`), `Dtls/DtlsFingerprint.cs`,
  `Dtls/QueueDatagramTransport.cs`; tests `DtlsSrtpKeyExporterTests`, `DtlsSrtpHandshakeTests`
- Git: `1ef5e7c` (wipe the concatenated DTLS-SRTP exporter secret after splitting, #14)
- Markers: RFC 5763, RFC 5764 §4.1/§4.1.3/§4.2/§6.7.1, RFC 8122; K5 (Secrets — zeroization);
  DECISION (BouncyCastle Core dependency)
