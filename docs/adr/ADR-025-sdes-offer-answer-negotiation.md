# ADR-025: SDES Offer/Answer Negotiation with Own-Key Answers and Fail-Closed Keyless Rejection

Status: Accepted
Date: 2026-07-09

## Context

SDES (RFC 4568) keys SRTP by carrying master key material inline in SDP `a=crypto`
attributes. Getting the offer/answer key semantics right is a correctness- *and*
security-critical decision: each side must encrypt its outbound direction with its **own**
freshly generated key and decrypt the inbound direction with the **peer's** key. Echoing the
offerer's key back in the answer makes both directions derive the same keystream and either
breaks decryption or collapses confidentiality.

The original code did exactly the wrong thing. A live bug on `main`
(`SdpOfferAnswerNegotiator`, historic line 134: `crypto = [offeredAudio.Crypto[0]]`) mirrored
the remote master key straight into the answer. The forward-port assumption ("port the fix
branch") turned out empty — the fix branch and `main` were byte-identical, so this was fixed
directly against `main`, not ported (log `2026-07-08-dev-srtp-sdes-answer`). Two further
gaps followed: the SDK could only *answer* SDES (inbound calls) but never *offer* it
(outbound calls could not send `RTP/SAVP` with `a=crypto`), and a secure `RTP/SAVP(F)`
offer we could not key had to be resolved without ever falling through to plain RTP.

### Verified current state (graphify + code)

- `SdesCryptoSelector` (`src/Core/Infrastructure/Sdp/OfferAnswer/SdesCryptoSelector.cs`,
  graphify community 275; pure static class) is the single point of SDES key selection. Its
  XML doc states the invariant verbatim: *"The answer MUST carry the answerer's own master
  key/salt — echoing the offerer's key would make both directions derive the same keystream
  and break the peer's decryption (and any confidentiality claim)."*
- `SelectAnswer(offered)` iterates offered `a=crypto` lines, keeps the first with a supported
  suite (`TryMapSuite` → `SrtpCryptoSuiteNames.TryParse`) and an `inline:` key, mirrors the
  **tag** (RFC 4568 §5.1.2) and the **suite**, and generates **fresh** local key params
  (`GenerateInlineKeyParams` → `RandomNumberGenerator.GetBytes(keyLen + saltLen)`). Non-inline
  lines are skipped — "answering them would negotiate an undecryptable call."
- `BuildDefaultOffer()` offers exactly one suite, the mandatory-to-implement
  `AES_CM_128_HMAC_SHA1_80` (RFC 4568 §6.2, via `SrtpCryptoSuiteNames.DefaultSuiteName`),
  tag 1, fresh inline key (RFC 4568 §5.1.1). A single offered suite keeps the later
  offer/answer key match unambiguous.
- `SdpOfferAnswerNegotiator` (`src/…/Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs`) selects the
  answer profile by keying method: DTLS fingerprint present → `UDP/TLS/RTP/SAVPF`; else
  `a=crypto` present → `RTP/SAVP`; else plain `RTP/AVP` (verified code lines 33-40). It
  **rejects** (`Success = false`) a secure SDES profile it can key neither via SDES nor DTLS:
  `if (localCrypto is null && fingerprint is null && IsSdesSecuredProfile(offeredAudio.Profile))`
  (verified line 258). `IsSdesSecuredProfile` matches `RTP/SAVP`/`RTP/SAVPF` (lines 781-782).
- Suite-token ↔ implemented-suite mapping lives in the SRTP module
  (`SrtpCryptoSuiteNames`), not in SDP — the negotiator delegates, keeping the SDP layer free
  of crypto types (module boundary; log `2026-07-08-dev-srtp-media-path`).

## Decision

Negotiate SDES with strictly mirrored key semantics and a hard fail-closed floor:

1. **Own-key answers.** The answer always carries the answerer's own freshly generated master
   key/salt, mirroring only the offerer's tag and suite (RFC 4568 §5.1.2/§5.1.3). Never echo
   the offered key.
2. **Symmetric offer path.** As offerer, send `RTP/SAVP` + one `a=crypto` line
   (`AES_CM_128_HMAC_SHA1_80`, tag 1, fresh key; RFC 4568 §5.1.1/§6.2), retain that key as the
   outbound-encrypt key, and match the peer's answer crypto as the inbound-decrypt key.
3. **Single offered suite.** Offer exactly one suite so the offer/answer key correspondence is
   unambiguous; multi-suite offers with tag-based matching are deferred follow-up.
4. **Fail-closed on keyless secure profiles (ENGINEERING_RULES K1).** A secure `RTP/SAVP(F)`
   offer that can be keyed neither via SDES (no answerable/supported `a=crypto`) nor via DTLS
   (no fingerprint) is **rejected** (`Success = false` → 488), never silently downgraded to
   plain RTP. A plain `RTP/AVP` offer carrying an unsupported `a=crypto` legally falls back to
   an unencrypted answer (AVP was never a secure profile).

**Crux:** the answer's key is generated locally and independently of the offer; "keyless
secure negotiation" terminates the call rather than producing a plaintext downgrade.

## Consequences

Positive: fixes a real confidentiality/correctness bug (mirrored key); makes outbound SRTP
possible at all; the single-suite offer removes ambiguity from the key match; the fail-closed
reject makes downgrade attacks against SDES-secured profiles non-viable.

Tradeoffs / honest divergence:
- **MKI, key lifetime, and KDR are not negotiated.** The answer carries no session params;
  these are out of scope (logs `2026-07-08-dev-srtp-sdes-answer`, `-media-path`).
- **Only one suite is offered.** Multi-suite offers with tag-based answer matching are optional
  follow-up.
- **A policy edge remains:** an `RTP/AVP` offer *with* an `a=crypto` line against
  `SrtpPolicy.Required` passes the offer-signalling gate and can still produce a plain answer —
  the gate inspects offer signalling, not the negotiated result. Post-negotiation result
  validation is tracked as follow-up (logs `-sdes-answer`, `-media-path`). This ADR does not
  claim that gap is closed.
- **No production interop claim.** sipgate and comparable trunks offer only `RTP/AVP`; SDES
  correctness is proven by unit + in-proc E2E tests, not against a live SDES peer.

## Guardrails

- The SDES answer key is always freshly generated and never equal to the offered key
  (`GenerateInlineKeyParams`; tests assert own-key ≠ offer-key, two runs ≠ each other).
- Tag and suite of the selected offered line are mirrored into the answer (RFC 4568 §5.1.2).
- Only `inline:` keyed, supported-suite lines are answered; anything else is skipped.
- A keyless SDES-secured profile (`RTP/SAVP`/`RTP/SAVPF`) is rejected, never answered plain
  (`SdpOfferAnswerNegotiator`, code line 258; ENGINEERING_RULES K1).
- Suite-token mapping stays in the SRTP module; the SDP layer holds no crypto types.

## Sources

- Logs: `docs/archive/agent-log/2026-07-08-dev-srtp-s0-step1.md` (crypto layer port),
  `2026-07-08-dev-srtp-sdes-answer.md` (S1: own-key answer, mirror-bug fix),
  `2026-07-09-dev-srtp-offer-sdes.md` (offer-SDES, single suite).
- Code (graphify-verified): `SdesCryptoSelector.cs` (community 275),
  `SdpOfferAnswerNegotiator.cs` (`SelectAnswer`/`NegotiateAnswer`, lines 33-40, 258, 781-782),
  `SrtpCryptoSuiteNames` (suite mapping / `DefaultSuiteName`).
- Markers / RFC: RFC 4568 §5.1.1/§5.1.2/§5.1.3/§6.1/§6.2 (SDES); RFC 6188 (AES-256 suites);
  ENGINEERING_RULES K1 (fail-closed), K5 (secrets), K7 (RFC references in code).
