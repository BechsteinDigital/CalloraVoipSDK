# ADR-030: DTLS-SRTP Media Wiring and Fail-Closed Context Installation

Status: Accepted
Date: 2026-07-14

## Context

The keying core produces SRTP master keys; the signaling layer decides a leg is DTLS-keyed. This
ADR covers the layer between them: **how the media session actually runs the DTLS handshake on
the live media socket and installs the resulting SRTP contexts** — late, once, and fail-closed,
without ever leaking plaintext on the wire during the window before keys exist.

The design constraint is that a DTLS handshake completes *after* the RTP socket is up, so the
SRTP contexts cannot be constructor-injected the way SDES contexts (keyed from SDP) can. Media
must be blocked until the handshake yields keys, and must stay blocked forever if it fails.

### Verified current state

- **`DtlsMediaAttachment`** (`src/Core/Infrastructure/Dtls/DtlsMediaAttachment.cs`) bridges DTLS
  records between the shared RTP socket (`sendRaw` ↔ `QueueDatagramTransport`), runs the
  handshake in the negotiated role as a background task, derives the four SRTP/SRTCP contexts
  from the exported keys, and hands them back via `onContextsReady` (plus optional RTX
  secondary contexts). It owns those contexts and the DTLS association (close_notify on
  dispose). It mirrors the `IceMediaAttachment` pattern to keep the media session small.
- **Fail-closed at creation** (`EnsureDependencies`, `TryCreate`): a DTLS-negotiated leg with no
  handshaker/certificate, or no remote fingerprint, **throws** (RFC 5763 §6.7.1 — refuse
  unauthenticated media) *before* any socket/context is allocated. Returns `null` when the leg
  did not negotiate DTLS.
- **Fail-closed on the hot path** (`RtpSession` + `RequireEncryptedMedia`): SRTP/SRTCP contexts
  moved from readonly options to Volatile fields, installed once via `InstallSecurityContexts`;
  all four crypto paths (RTP/RTCP × send/receive) drop rather than emit/accept plaintext until
  keys are installed, and `ObjectDisposedException` in protect/unprotect is a clean drop
  (teardown race). `RequireEncryptedMedia = IsSrtpNegotiated || IsDtlsNegotiated` — a
  keyless-secure leg stays silent, never plain (verified: passive peer counts 0 RTP-shaped
  datagrams pre-keying, including the DTMF path).
- **Strict inbound source filter**: inbound DTLS records are accepted only from the nominated
  remote endpoint. For BUNDLE the endpoint is mutable (Volatile, updated from the ICE
  nomination path via `UpdateRemoteEndPoint`); the SIP path keeps a fixed nominated remote, so
  its strict behavior is unchanged.
- **Dispose ordering**: `StopTransmission → attachment dispose → socket teardown`; the result
  is disposed *before* the lifetime cancel so `close_notify` is not suppressed; contexts are
  zeroed on dispose. `SdesMediaCryptoContextFactory` was extracted from `RtpCallMediaSession`
  (1000-line limit) as a behavior-neutral split.

## Decision

Wire DTLS-SRTP into the media session as a **self-owning attachment with late, once-only,
fail-closed context installation**:

1. **`DtlsMediaAttachment` owns the handshake and the derived contexts.** The media session
   passes `sendRaw` and receives contexts via callback; it does not run TLS itself.
2. **Validate before allocating** (`EnsureDependencies`): a DTLS leg missing its
   handshaker/certificate/fingerprint fails closed *before* socket/context allocation — never a
   half-initialized secure leg.
3. **Contexts install exactly once**, late, into Volatile fields (`InstallSecurityContexts`);
   until then every crypto path drops.
4. **`RequireEncryptedMedia` is the media backstop**: no plaintext egress or ingress on a leg
   that negotiated security, ever — pre-keying, on handshake failure, or on fingerprint
   mismatch.
5. **Strict inbound source filter** on DTLS records, endpoint-mutable only for the
   ICE-nominated BUNDLE case.
6. **Ephemeral ECDSA-P256 certificate per client instance** as a composition-root fallback,
   DI-overridable.

### Crux

The whole design turns on the ordering asymmetry: **keys arrive after the socket is live.** So
the safe default is "drop until installed" enforced at all four crypto paths behind a Volatile
gate, with `RequireEncryptedMedia` making that drop mandatory for any secure leg — a handshake
that never completes (or authenticates) leaves the leg permanently silent instead of silently
plaintext. Ownership of the contexts sits in the attachment so the dispose sequence
(result-before-cancel for close_notify, contexts zeroed) is one place, not scattered.

## Consequences

Positive: the media layer runs DTLS-SRTP end-to-end on a real UDP socket with encrypted
round-trips, and demonstrably emits **no plaintext** when unkeyed, on handshake failure, or on
fingerprint mismatch (E2E-tested including the DTMF path). Fail-closed is enforced at both
creation and the hot path, not just documented.

Tradeoffs / honest divergence:
- **Strict source filter is not NAT-tolerant yet**: inbound records are pinned to the nominated
  remote; a symmetric-DTLS latch (analogous to the RTP one; the fingerprint already
  authenticates) is deferred follow-up (m1).
- **Handshake timeout / app-surfacing is thin**: failure is Log + StopTransmission; media
  supervision only reacts indirectly — no explicit timeout or app-facing failure event yet
  (m3).
- **BC↔BC loopback only** — no browser/foreign-stack interop validated at this layer.
- Per-context key copies are still not zeroed at teardown (shared with the foundation ADR's
  follow-up).

## Guardrails

- A DTLS-negotiated leg with missing handshaker/certificate/fingerprint fails closed at creation
  (`EnsureDependencies`), before any resource allocation.
- SRTP/SRTCP contexts install once, late, via Volatile fields; every crypto path drops until
  installed.
- `RequireEncryptedMedia` (`IsSrtpNegotiated || IsDtlsNegotiated`) forbids plaintext egress and
  ingress on any secure leg — pre-keying, handshake failure, or fingerprint mismatch — verified
  by no-plaintext-egress E2E tests.
- Inbound DTLS records accepted only from the nominated remote (mutable only for the ICE-BUNDLE
  case, Volatile).
- Dispose order: StopTransmission → attachment (result-before-cancel so close_notify is sent,
  contexts zeroed) → socket teardown.

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-dtls-media-wiring.md`
- Code (graphify-verified): `Dtls/DtlsMediaAttachment.cs`
  (`EnsureDependencies`, `TryCreate`, `UpdateRemoteEndPoint`, dispose ordering),
  `Rtp/Session/RtpSession.cs` (`InstallSecurityContexts`, `RequireEncryptedMedia`, four crypto
  paths), `Rtp/SdesMediaCryptoContextFactory.cs`, `Rtp/RtpCallMediaSession.cs` (+ factory
  wiring), `Dtls/QueueDatagramTransport.cs`, `Domain/Calls/CallMediaParameters.cs`
  (`IsDtlsNegotiated`, `DtlsIsClient`, remote-fingerprint fields); tests
  `DtlsMediaPathE2eTests`, `DtlsSignalingToMediaE2eTests` (full-chain)
- Markers: RFC 5763 §6.7.1; K1 (fail-closed media — no keyless secure egress),
  K3 (threading: Volatile hot-path state, idempotent dispose), K5 (context zeroization on
  dispose); Review M1/M2 (dispose race, close_notify suppression) fixed
