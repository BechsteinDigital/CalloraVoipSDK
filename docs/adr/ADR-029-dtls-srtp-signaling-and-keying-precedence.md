# ADR-029: DTLS-SRTP Signaling, Answer-Role, and Keying-Method Precedence

Status: Accepted
Date: 2026-07-14

## Context

With the DTLS-SRTP keying core in place (foundation ADR), SIP calls still could not *reach* it:
no production path set `IsDtlsNegotiated`, and there was no offer/answer machinery for
`a=fingerprint` / `a=setup`. This ADR covers the signaling wiring and — the sharp part — the
**precedence rules that decide which keying method wins** when an SDP can be read more than one
way (SDES `a=crypto` vs. DTLS fingerprint), plus how the answerer picks its DTLS role, all while
staying **fail-closed** (K1: no cleartext downgrade).

### Verified current state

- **Offer-side precedence** (`SdpOfferAnswerNegotiator.CreateOffer`): profile is chosen
  `dtls is not null → UDP/TLS/RTP/SAVPF`, else `crypto.Count > 0 → RTP/SAVP`, else `RTP/AVP`.
  **DTLS wins over SDES over plain.** SDES a=crypto is suppressed on a DTLS offer.
- **Answer-side precedence** (`TryNegotiateVideoAnswerMedia`, and the audio path): a=crypto is
  only honored on a **non-DTLS profile** — `if (offered.Crypto.Count > 0 &&
  !SdpSecurityInspector.IsDtlsProfile(offered.Profile))`. On a DTLS profile the a=crypto is
  ignored and the answer is fingerprint-keyed. The two methods are **mutually exclusive per
  m-line** (RFC 5763).
- **Answer role** (`ResolveAnswerSetup`, RFC 5763 §5 / RFC 4145 §4): `actpass → active`,
  `active → passive`, `passive → active`, and **no/holdconn remote setup → passive** (the
  offer defaults to `active` per RFC 4145 §4, so the answerer takes the server/passive side).
  An answer is **never** `actpass`.
- **RFC 5763 §6.6 interop**: a `SAVP(F)` profile that also carries `a=fingerprint` resolves to a
  **DTLS answer** — the fingerprint decides, not the profile string; DTLS resolution runs
  *before* the keyless-secure reject.
- **Fail-closed rejects**: a secure m-line that can be keyed neither via SDES (no answerable
  a=crypto) nor via DTLS (no remote fingerprint / no local identity) is **declined**, not
  answered in the clear (`return null` on the keyless secure profile).
- **Enricher + latch** (`CallMediaParametersDtlsEnricher`, `SipCoreCallChannel`): stamps
  `IsDtlsNegotiated`/`DtlsIsClient`/remote-fingerprint onto `CallMediaParameters`; a
  `_dtlsActiveOnCall` latch conserves established keying across a re-offer; the enricher runs at
  both publish sites. `SdkConfiguration.OfferDtlsSrtp` defaults **false** (SDES stays the
  default egress).

## Decision

Signal DTLS-SRTP through the standard SDP surface and make **precedence explicit and
fail-closed**:

1. **Keying precedence: DTLS > SDES > plain.** On offer, a DTLS leg selects `UDP/TLS/RTP/SAVPF`
   and does not emit a=crypto. On answer, a=crypto is honored only on a non-DTLS profile; on a
   DTLS profile the leg is fingerprint-keyed and any a=crypto is ignored. One keying method per
   m-line, never both.
2. **Fingerprint-decides over profile string** for §6.6 interop: `SAVP(F)+fingerprint` is
   answered as DTLS.
3. **Answer role per RFC 5763 §5 / RFC 4145 §4**, defaulting to `passive` on a missing remote
   `a=setup`; answers are never `actpass`.
4. **Fail-closed**: any secure offer the SDK cannot key is rejected, never downgraded to
   cleartext. Under a *required* media policy, a keyless-secure negotiation (e.g. a
   fingerprint-less UDP/TLS answer) is a policy violation and the media backstop
   (`RequireEncryptedMedia = IsSrtpNegotiated || IsDtlsNegotiated`) keeps such legs **silent**
   rather than plain.
5. `OfferDtlsSrtp` is opt-in (default false); the answerer always carries its DTLS identity so
   it can accept a DTLS offer even when it would not itself originate one.

### Crux

Precedence lives at exactly two points and must agree: the **offer profile ladder** and the
**answer's `IsDtlsProfile` gate on a=crypto**. The invariant is *one keying method per m-line,
DTLS winning any ambiguity, and no answer that keys nothing on a secure profile*. Fingerprint
presence — not the profile token — is the DTLS discriminator, so a peer offering `SAVPF` with a
fingerprint still gets DTLS.

## Consequences

Positive: SIP calls negotiate DTLS-SRTP end-to-end (full-chain evidence: real negotiator → real
DTLS handshake → encrypted audio). SDES remains the default, so nothing regresses for existing
callers. The keyless-secure backstop closes the fail-open path that would otherwise emit
plaintext under a required policy.

Tradeoffs / honest divergence:
- **Browser / foreign-stack interop is not tested** (BC↔BC loopback only).
- **Re-handshake scenarios are open**: a re-INVITE with a *changed* fingerprint triggers a
  session rebuild → DTLS re-handshake in a live call is untested end-to-end (RFC 5763 §6.6);
  hold/unhold of a DTLS call is only logically argued (M2 latch fix), not E2E-tested.
- Precedence correctness rests on `SdpSecurityInspector.IsDtlsProfile` being the single source
  of truth for "is this a DTLS profile" across offer and answer — divergence there would break
  the exclusivity invariant.

## Guardrails

- One keying method per m-line: DTLS profile ⇒ ignore a=crypto; SDES only on a non-DTLS profile.
- Offer profile ladder is DTLS > SDES > plain; DTLS offers emit no a=crypto.
- Answer `a=setup` is always `active`/`passive` (RFC 5763 §5), defaulting `passive`; never
  `actpass`.
- A secure m-line that can be keyed by neither method is rejected, never answered in cleartext
  (K1 fail-closed); `RequireEncryptedMedia` keeps keyless-secure legs silent.
- The DTLS enricher runs at every publish site; the re-offer latch conserves — never
  force-upgrades — established keying (no DTLS forced onto a SDES/plain call).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-dtls-signaling.md`
- Code (graphify-verified): `Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs`
  (`.CreateOffer()` profile ladder, `ResolveAnswerSetup`, `TryNegotiateVideoAnswerMedia`
  IsDtlsProfile gate), `Sdp/SdpUtilities.cs` (`.TryExtractAudioDtls()`, `ConvertOptions`),
  `Sdp/SdpSecurityInspector` (`IsDtlsProfile`), `Application/.../CallMediaParametersDtlsEnricher`,
  `SipCoreCallChannel` + `SipCallChannelSrtpPolicyGuard`; tests `DtlsSignalingToMediaE2eTests`
- Markers: RFC 5763 §5/§6.6/§6.7.1, RFC 4145 §4, RFC 4568, RFC 3264; K1 (fail-closed media),
  K2 (enricher order ICE→SRTP→DTLS); Review BLOCKER B1 (keyless-secure fail-open) fixed
