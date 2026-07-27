# ADR-026: SRTP Media-Path Wiring — Fail-Closed Keying, Wire-Derived Keys, and Hardening

Status: Accepted
Date: 2026-07-09

## Context

Negotiating SDES keys (see ADR C04-01) is inert until SRTP contexts are actually wired into
the RTP send/receive path. That wiring is the security boundary where a mistake means either
plaintext media on a call that negotiated encryption, or a crash on a malformed inbound
packet. Three questions had to be answered together: **where does the media path get its key
material**, **what happens when a secure negotiation yields no usable key**, and **how does the
crypto hot path survive hostile input and clean up its secrets**.

The guiding rule is ENGINEERING_RULES **K1**: if SRTP/DTLS is negotiated or required, media on
that leg is either encrypted or dropped/rejected — never sent or accepted in clear. Sends
issued before key installation are suppressed and counted, not buffered and not sent in clear.

### Verified current state (graphify + code)

- **Keys are recovered from the SDP that actually went over the wire**, not from an in-memory
  side channel. `CallMediaParametersSrtpEnricher.Enrich`
  (`src/…/Sip/Adapters/CallMediaParametersSrtpEnricher.cs`, graphify community 522) extracts
  the peer's key from the remote SDP (`SdpUtilities.TryExtractAudioCrypto`) and our own key
  from the local description (the answer we sent, or the offer we sent). It stamps SRTP fields
  **only when both keys are present and the suites agree** (`sdesUsable` gate) — otherwise all
  SRTP fields stay null. "Never half-encrypted" is enforced by construction. The video m-line
  is enriched the same way (`EnrichVideo`), and a keyed video leg without usable keys "stays
  fail-closed-silent."
- **This enricher runs second in a fixed order.** An explicit in-code INVARIANT
  (`CallMediaParametersSrtpEnricher.cs` line 81) states: *"the ICE enricher runs before this
  one (Ice → Srtp → Dtls), so an incoming video already carries its ICE credentials/role;
  both branches must preserve them."* This is ENGINEERING_RULES **K2** (enricher order
  ICE → SRTP → DTLS, `with`-clone not hand-copy — HARD-R5).
- **Fail-closed at context creation.** `RtpCallMediaSession`
  (`src/…/Rtp/RtpCallMediaSession.cs`, graphify community 16) builds the SDES contexts via
  `SdesMediaCryptoContextFactory.TryCreate(parameters, …)` and sets
  `RequireEncryptedMedia = parameters.IsSrtpNegotiated || parameters.IsDtlsNegotiated`
  (verified lines 167-184). The in-code comment names the rule: *"Fail-closed backstop: any
  secure-signaled negotiation (SDES, DTLS, or a keyless degenerate exchange) must never fall
  through to plain RTP."* DTLS legs start with no contexts and stay fail-closed until the DTLS
  attachment installs post-handshake contexts; on DTLS failure "media stays fail-closed and
  transmission ceases" (lines 199-204). Invalid SDES material on a negotiated-SRTP leg throws
  rather than falling back to plaintext (log `2026-07-08-dev-srtp-media-path`).
- **Inbound is unprotected before any interpretation.** `SrtpContext.Unprotect`
  (`src/…/Srtp/Context/SrtpContext.cs` L109, graphify degree 23) runs before the codec /
  jitter buffer / symmetric-latch; auth or replay failure → drop (never reaches playout).
  Outbound `Protect` runs under a dedicated lock so ROC advancement is ordered against
  sequence-number assignment (log `-media-path`).
- **Hardening (log `2026-07-08-dev-srtp-hardening`):** `GetRtpHeaderLength` validates the
  CSRC offset and extension length against the packet length and throws
  `CryptographicException` on malformed input (previously a negative payload length threw an
  uncaught `ArgumentOutOfRangeException` that killed the receive loop). `ISrtpContext :
  IDisposable`; `Dispose` zeroes cipher key / salt / auth key via
  `CryptographicOperations.ZeroMemory`, idempotent, `ObjectDisposedException` after
  (ENGINEERING_RULES **K5**). All context state is under one internal lock; `RtpSession`'s
  outer protect lock is kept deliberately (belt-and-braces + ROC ordering).

## Decision

Wire SRTP into the media path with three non-negotiable properties:

1. **Wire-derived keys.** SRTP contexts are built from the key material in the SDP strings that
   actually crossed the wire (`CallMediaParametersSrtpEnricher` → `SdesMediaCryptoContextFactory`),
   so the session keys exactly what was signalled. The domain model holds plain string
   key-param fields only; no crypto types leak into `CallMediaParameters`.
2. **Fail-closed keying (K1).** `RequireEncryptedMedia` is set whenever SRTP or DTLS was
   negotiated. Invalid/absent key material on such a leg drops/ceases media instead of sending
   plaintext; SDES contexts are installed up front, DTLS contexts post-handshake, and sends
   before installation are suppressed and counted (the BUNDLE path exposes this as
   `SuppressedSends`/`RtcpSuppressedSends`).
3. **Hardened crypto boundary (K4/K5).** Malformed RTP headers are validated and rejected as a
   `CryptographicException`-driven drop that the receive loop survives; inbound is
   unprotected before interpretation; derived session keys are zeroed on dispose; context
   state is lock-guarded.

**Crux:** the media path keys itself from what was signalled and refuses to run in clear on a
secure leg — the decision point is `RequireEncryptedMedia`, and the failure mode is
drop/cease, never downgrade.

## Consequences

Positive: SDES SRTP is proven end-to-end in-process (`SrtpSignalingToMediaE2eTests`: SDK frame
arrives as ciphertext, peer decrypts with the answer key, reverse direction decrypts with the
offer key — peers derive their contexts *only* from exchanged SDP, like a real SIP peer, log
`2026-07-08-dev-srtp-e2e`). A manipulated packet is dropped without crash and the next valid
packet still delivers. Regression floor: a plain RTP session is byte-identical to before.

Tradeoffs / honest divergence:
- **RTCP is not encrypted in this slice.** The media-path slice logs SRTCP as absent ("RTCP
  runs in clear, logged"); SRTCP crypto/wiring is a separate cluster (C06). This ADR does not
  claim full RFC 3711.
- **No MKI / key lifetime / KDR** in the media path (mirrors ADR C04-01).
- **`SrtpContext` was single-SSRC at this point** — one sender index, one 64-packet replay
  window; the SSRC only feeds the IV. Per-SSRC ROC/replay under a shared key is a BUNDLE
  concern handled elsewhere (ADR-011); for one-context-per-stream SDES calls the single-SSRC
  model is correct.
- **The suppress-and-count fail-closed counter (`SuppressedSends`) is implemented in the
  BUNDLE/DTLS outbound pipeline** (`BundledOutboundPipeline`), not in the classic SDES
  single-stream `RtpSession` path, where SDES contexts are installed synchronously up front so
  there is no pre-keying send window. Honest scope note: K1's "suppress + count" is realized in
  the DTLS/BUNDLE path; the SDES path realizes K1 as up-front install + fail-closed
  `RequireEncryptedMedia`.
- **Not security-audited; no interop claim.** Evidence is loopback packet-level + in-proc E2E.

## Guardrails

- SRTP fields are stamped only when both keys are present and the suites agree (`sdesUsable`);
  never half-encrypted (`CallMediaParametersSrtpEnricher.Enrich`).
- Enricher order ICE → SRTP → DTLS is preserved; SRTP enrichment carries forward the ICE
  fields the prior enricher stamped (in-code INVARIANT; ENGINEERING_RULES K2 / HARD-R5).
- `RequireEncryptedMedia = IsSrtpNegotiated || IsDtlsNegotiated`; a secure leg with no usable
  key drops/ceases media, never plaintext (ENGINEERING_RULES K1).
- Inbound is unprotected before codec/jitter/latch; auth/replay failure → drop.
- Malformed RTP header → `CryptographicException` drop; receive loop survives.
- Session keys zeroed on dispose (`CryptographicOperations.ZeroMemory`), idempotent; context
  state lock-guarded (ENGINEERING_RULES K5).

## Sources

- Logs: `docs/archive/agent-log/2026-07-08-dev-srtp-media-path.md` (S2 wiring, fail-closed,
  wire-derived keys), `2026-07-08-dev-srtp-e2e.md` (S3 in-proc E2E),
  `2026-07-08-dev-srtp-hardening.md` (B.1 header bounds, key zeroing, thread-safety).
- Code (graphify-verified): `RtpCallMediaSession.cs` (community 16, lines 167-204),
  `CallMediaParametersSrtpEnricher.cs` (community 522, `sdesUsable` gate + INVARIANT line 81),
  `SrtpContext.cs` (`Unprotect` L109, `Dispose`/`SrtpSessionKeys.Zero`),
  `BundledOutboundPipeline.cs` (`SuppressedSends`/`RtcpSuppressedSends`, fail-closed comment).
- Markers / RFC: RFC 3711 §3.2/§3.4 (SRTP contexts, IV), RFC 4568 (SDES keys);
  ENGINEERING_RULES K1 (fail-closed), K2 (enricher order / HARD-R5), K4 (wire-boundary parse),
  K5 (secrets zeroing / FixedTimeEquals), K7 (RFC refs).
