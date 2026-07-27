# ADR-031: SRTCP Crypto Core and RTCP-Path Wiring

Status: Accepted
Date: 2026-07-09

## Context

A negotiated SRTP call encrypted its RTP but sent its **RTCP in the clear** — sender/receiver
reports, BYE, feedback, and RTCP-XR all left the socket unprotected even under an active SRTP
session. That is both a confidentiality leak (CNAMEs, timing, loss statistics) and a fail-open
inconsistency: the media path was secure while its control channel was not. Closing it needs the
SRTCP transform of RFC 3711 §3.4, which is *not* the SRTP transform — RTCP uses an independent
keystream (KDF labels 3/4/5), a different packet layout, an explicit 31-bit SRTCP index instead of
an implicit rollover counter, and mandatory authentication.

The work was cut into two slices, one decision: **(1)** an isolated, testable SRTCP crypto core
(`2026-07-09-dev-srtcp-crypto-core.md`), then **(2)** wiring it into the live RTCP send/receive path
(`2026-07-09-dev-srtcp-wiring.md`). Slice 2 also surfaced a remote-triggerable DoS on the receive
loop — that hardening decision is owned by ADR C07-02 (wire-boundary DoS), not re-decided here.

### Verified current state (graphify-grounded)

- **Crypto core exists and matches §3.4.** `SrtcpContext` (`src/Core/Infrastructure/Srtp/Context/
  SrtcpContext.cs`) implements `ISrtcpContext.ProtectRtcp`/`UnprotectRtcp`. Layout is
  `[8-byte clear header][AES-CM encrypted payload][E|31-bit index (4B)][auth tag]`; the first 8
  bytes stay clear, AES-CM runs from byte 8, the IV carries the SRTCP index (no ROC, per the class
  doc "no rollover counter feeds the IV or the authentication"), and the HMAC-SHA1 tag covers the
  encrypted packet **including** the E|index word. Receive is verify-then-decrypt with
  `CryptographicOperations.FixedTimeEquals` (RFC 3711 §3.3); `Dispose` zeroes keys.
- **Independent RTCP keystream.** `SrtpKeyDerivation.DeriveRtcp` (`Srtp/Crypto/SrtpKeyDerivation.cs`)
  derives from the *same* master material with labels 3/4/5 (`LabelRtcpCipherKey/AuthKey/Salt`),
  distinct from the SRTP `Derive` labels 0/1/2 — RFC 3711 §4.3.2.
- **Wired into the live path.** `RtpSession.SendControlCoreAsync` (`Rtp/Session/RtpSession.cs`
  L303-329) calls `OutboundSrtcp.ProtectRtcp` after the `_transmissionStopped` gate and before the
  socket send; `RtpSession.ProcessDatagram`'s RTCP branch (L560-598) calls
  `InboundSrtcp.UnprotectRtcp`. `RtpSessionOptions` carries `OutboundSrtcp`/`InboundSrtcp`
  (`ISrtcpContext?`). Contexts are minted from the same `SrtpKeyMaterial` as the SRTP contexts via
  `SdesMediaCryptoContextFactory` (4-tuple RTP+RTCP; `RtpCallMediaSession` L167-179) and disposed
  together (L476-477).
- **Fail-closed on RTCP too.** When `RequireEncryptedMedia` is set and no SRTCP context is installed
  yet (DTLS-SRTP pre-handshake), outbound RTCP is suppressed, not sent in clear (RtpSession L326-329);
  inbound clear RTCP is likewise rejected (L560). Matches ENGINEERING_RULES K1.

## Decision

Protect RTCP under a negotiated SRTP session with a dedicated SRTCP transform, built as an isolated
crypto core first and wired into the control path second.

1. **Separate `SrtcpContext`, do not overload `SrtpContext`.** SRTCP is a different transform (index
   word, no ROC, different KDF labels, always-authenticated). A per-direction `ISrtcpContext` keeps
   the SRTP hot path untouched and the SRTCP layout self-contained.
2. **Independent RTCP keystream via labels 3/4/5.** `DeriveRtcp` reuses the KDF but with the RTCP
   label set, so an RTCP keystream never overlaps the RTP keystream under the same master key.
3. **Wire at the `RtpSession` control boundary.** Protect in `SendControlCoreAsync`, unprotect in the
   RTCP arm of `ProcessDatagram`; `RtpSessionOptions` transports the contexts; the media session
   factory mints RTP+RTCP contexts together from one `SrtpKeyMaterial` and disposes them together.
4. **Fail closed on RTCP.** No plain-RTCP fallback once encryption is required or a context is
   installed; suppress-and-log instead (K1).

### Crux

The SRTCP transform is *not* SRTP with a different key. Three properties are load-bearing and were
implemented deliberately, going **beyond** the two logs' original sketch:

- **No ROC — the 31-bit SRTCP index is explicit and authenticated.** The index word travels on the
  wire and is inside the HMAC input, so replay/ordering does not depend on a synchronised rollover
  counter the way SRTP does (`BuildIv` places the index in IV bytes 10-13; §4.1).
- **Per-SSRC index and replay state, not per-direction.** `SrtcpContext` keys its send index and
  replay window by SSRC (`Dictionary<uint, SrtcpSsrcState>`, `SrtcpSsrcState.cs` over a shared
  `SlidingReplayWindow`), so several RTCP senders multiplexed over one BUNDLE key do not collide in
  one window (RFC 3711 §3.2.3, marker HARD-D1). The original crypto-core log described a single
  per-direction sender index + one 64-packet window; the shipped code is per-SSRC.
- **80-bit SRTCP auth tag regardless of suite.** `SrtcpAuthTagLength = 10`. The 32-bit truncation of
  RFC 3711 §5.2 applies to SRTP only; SRTCP stays at 80 bits even under
  `AES_CM_128_HMAC_SHA1_32` so mandatory RTCP authentication is not weakened and libsrtp interop
  holds (RFC 4568 §6.2, RFC 5764 §4.1.2 footnote). Neither log fixed the tag-length policy; the code
  documents and enforces it.

## Consequences

Positive: RTCP under a negotiated SRTP call is now encrypted and authenticated end-to-end; the
control channel no longer fails open. The core was proven in isolation (KDF known-answers, layout,
round-trip, tamper/replay/wrong-key rejection) before touching the live path, and the SRTP hot path
was never modified. The per-SSRC design made the same core directly reusable for BUNDLE
(`BundledInboundPipeline`/`BundledOutboundPipeline`) and the video SDES path
(`VideoRtpStream._sdesOutboundSrtcp/_sdesInboundSrtcp`) without redesign.

Honest divergences from the source logs:

- **Scope grew past the logs.** The logs described a single-window, per-direction context wired only
  into `RtpSession`. The shipped code is per-SSRC (HARD-D1) with an explicit 80-bit tag policy, and
  the same `SrtcpContext` now serves BUNDLE and video — evolution, not regression.
- **Crypto-primitive duplication was resolved, not left open.** Both logs listed "consolidate the
  mirrored `AesCmXor`/`BuildIv`/`ComputeAuthTag` between `SrtpContext` and `SrtcpContext`" as
  follow-up. Current `SrtcpContext` imports the shared `AesCmCipher` from `Srtp.Crypto` and reuses
  `SlidingReplayWindow`; the mirror is gone.
- **The receive-loop DoS is owned elsewhere.** The wiring slice found (and fixed) a HIGH
  remote-DoS: a short RTCP-looking runt made `UnprotectRtcp` throw `ArgumentException`, uncaught,
  killing the receive loop; it also flagged the identical latent bug on the RTP arm as
  KRITISCH follow-up. Both catches now exist (`RtpSession` RTCP arm L584, RTP arm L665,
  secondary L822: `catch when (ex is ArgumentException or CryptographicException or
  ObjectDisposedException)`). That decision is **owned by ADR C07-02** (wire-boundary DoS); this ADR
  only records that SRTCP wiring was its origin.
- **Rekey.** No RTCP rekey problem was engineered — RTCP is periodic; the contexts are minted/disposed
  with the SRTP contexts on the same key material.

## Guardrails

- **Never fall through to plain RTCP** once encryption is required or a context is installed — suppress
  and log (K1); this holds on both send (RtpSession L326-329, and the disposed-during-teardown race
  L318-324) and receive (L560).
- **SRTCP auth tag stays 80-bit** across all suites; do not truncate to 32-bit for SHA1_32 (RFC 4568
  §6.2). Interop and security both depend on it.
- **Verify-then-decrypt with constant-time compare** on the receive path (RFC 3711 §3.3;
  `FixedTimeEquals`) — never decrypt unauthenticated SRTCP.
- **Per-SSRC index/replay state** must be preserved as multi-sender BUNDLE reuses the core (HARD-D1);
  a single shared window would be a correctness regression.
- **Keys zeroed on dispose** (`SrtpSessionKeys.Zero`, K5); contexts owned and disposed by the media
  session that created them.
- Any `UnprotectRtcp` throw on the receive path must remain a clean drop, never an unhandled throw
  that stops the loop (guardrail enforced by ADR C07-02).

## Sources

- Logs:
  - `docs/archive/agent-log/2026-07-09-dev-srtcp-crypto-core.md` — isolated §3.4 crypto core,
    `DeriveRtcp` labels 3/4/5, KDF known-answers, tamper/replay/wrong-key tests, primitives mirrored
    (consolidation deferred).
  - `docs/archive/agent-log/2026-07-09-dev-srtcp-wiring.md` — wiring into `RtpSession`
    send/receive + `RtpCallMediaSession` context creation; HIGH receive-loop DoS found+fixed; RTP-twin
    DoS flagged as KRITISCH follow-up.
- Code (graphify-verified):
  - `src/Core/Infrastructure/Srtp/Context/SrtcpContext.cs` — transform, layout, IV, 80-bit tag,
    verify-then-decrypt, dispose-zeroing.
  - `src/Core/Infrastructure/Srtp/Context/ISrtcpContext.cs` — `ProtectRtcp`/`UnprotectRtcp` contract.
  - `src/Core/Infrastructure/Srtp/Context/SrtcpSsrcState.cs` — per-SSRC send index + replay window.
  - `src/Core/Infrastructure/Srtp/Crypto/SrtpKeyDerivation.cs` — `DeriveRtcp` labels 3/4/5.
  - `src/Core/Infrastructure/Rtp/Session/RtpSession.cs` — protect (L303-329), unprotect
    (L560-598), fail-closed, disposed-race suppression.
  - `src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs` — context 4-tuple + dispose.
  - `src/Core/Infrastructure/Rtp/SdesMediaCryptoContextFactory.cs` — mints RTP+RTCP from one
    `SrtpKeyMaterial`.
- Markers / RFC: RFC 3711 §3.2.3 (per-SSRC crypto context, HARD-D1), §3.3 (verify-then-decrypt),
  §3.4 (SRTCP transform + layout), §4.1 (IV), §4.2 (HMAC-SHA1 auth), §4.3.2 (KDF labels 3/4/5),
  §5.2 (32-bit truncation is SRTP-only), §9.4 (key hygiene); RFC 4568 §6.2 and RFC 5764 §4.1.2
  (SRTCP tag stays 80-bit); ENGINEERING_RULES K1 (fail-closed media security), K5 (secrets zeroed /
  constant-time), K7 (RFC references at code). DoS-catch decision: ADR C07-02.
```

