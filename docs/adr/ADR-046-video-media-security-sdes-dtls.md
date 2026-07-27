# ADR-046: Video Media Security — Per-m-line SDES, RTX Keying, DTLS Precedence

Status: Accepted
Date: 2026-07-14

## Context

The video m-line must be securable exactly like audio, but keyed independently: SDES (RFC 4568)
derives SRTP/SRTCP contexts from the video m-line's **own** `a=crypto`, and DTLS-SRTP (RFC 5763)
runs its own association on the video 5-tuple. The two are mutually exclusive per m-line, and the
RTX repair stream needs its own keyed context (RFC 4588 §9). Getting this wrong risks a
plaintext leak or a two-time pad. This ADR covers video-specific keying and the DTLS/SDES
precedence rule; audio SDES offer/answer is ADR-C04-01, the DTLS-SRTP foundation is the C05 track.

### Verified current state (graphify-grounded)

- `SdesMediaCryptoContextFactory` (`src/Core/Infrastructure/Rtp/SdesMediaCryptoContextFactory.cs`)
  has a per-m-line overload `TryCreate(suite, local, remote, logger)` (L40) so audio and video
  each key from their own `a=crypto`; `TryCreateSecondarySrtp` (L59-67) builds an independent
  context pair for the RTX repair stream from the same key material (RFC 4588 §9 — own replay
  window). `TryParseKeyMaterial` (L74-95) throws on set-but-unparsable material — **fail closed,
  never plaintext**.
- `VideoRtpStream` builds the four SDES contexts (L144-146), hands them to the `RtpSession`
  (which only borrows), and disposes them — zeroing keys — after the session stops (L512-518).
  All-null for a plain/DTLS leg → primary path unchanged; **mutually exclusive with DTLS** (class
  doc L26-31). The RTX SDES contexts are built only when both SDES and RTX are negotiated
  (L194-201).
- Offer/answer (both `SdpOfferAnswerNegotiator`): the answer generates our **own** fresh key
  (`SdesCryptoSelector.SelectAnswer`), never echoing the offer key (RFC 4568 §5.1.3);
  audio/video call `BuildDefaultOffer` separately so each m-line gets fresh RNG keys (no
  two-time pad, RFC 4568 §7.1.3). Offerer-side reuses a retained
  `_activeLocalVideoSrtpKeyParams` across Hold/Unhold so a re-offer does not rekey a live stream.
- DTLS/SDES precedence: `SdpSecurityInspector.IsDtlsProfile` (`StartsWith("UDP/TLS/")`) guards the
  SDES branch in `TryNegotiateVideoAnswerMedia` (L493: `!IsDtlsProfile`) and the decline logic in
  `SdpUtilities.TryResolveVideoParameters` — a DTLS profile is fingerprint-keyed, any `a=crypto`
  on it is ignored (RFC 5763). Negotiator and resolver decide **identically** across the full
  profile × crypto × fingerprint matrix. A secure m-line keyable by neither → zero-port decline
  (L516-523), never answered in the clear.
- `RequireEncryptedMedia = IsSrtpNegotiated || IsDtlsNegotiated` stays true for SDES video
  (`VideoRtpStream` L162).

## Decision

1. **Per-m-line SDES keying.** The video stream derives its SRTP/SRTCP contexts from the video
   m-line's own key material via the raw-parameter factory overload; the session borrows, the
   stream owns and zeroes.
2. **RTX repair stream keyed independently** from the same video key material (RFC 4588 §9), so it
   keeps its own ROC/replay window and never reuses a (key, SSRC, index) triple.
3. **Fresh answer key, never the offer echo** (RFC 4568 §5.1.3); fresh RNG key per m-line (no
   two-time pad); retained offer key reused across Hold/Unhold so a live SDES video stream never
   rekeys mid-call.
4. **DTLS takes precedence over SDES per m-line** (RFC 5763): a `UDP/TLS/*` profile always goes
   the DTLS path and ignores any stray `a=crypto`; negotiator and resolver must decide identically.
5. **Fail-closed everywhere.** Unparsable key material throws; a secure m-line keyable by neither
   method is declined; RTX stays silent until its context exists.

### Crux

Two invariants carry the security weight. **Never fail open**: a secure-negotiated video m-line
either keys or declines (zero port) — it never sends plaintext, and unparsable key material
throws rather than degrading. **Never two-time-pad**: audio, video, and the video-RTX repair
stream each get independent contexts with distinct keys/SSRCs, and the offerer reuses its retained
key across re-offers so a rekey can't collide with in-flight packets. The DTLS-precedence rule
exists because the negotiator (what we answer) and the resolver (what we key) must agree on which
method secures the leg, or one could answer SDES while the other tries DTLS.

## Consequences

Positive: SDES video is bidirectional at both the media and signaling layers; DTLS video keys
post-handshake; RTX recovers under both. The precedence fix closes an answer/resolve divergence
that could otherwise leave a leg keyed one way and answered another.

Divergence / honesty:
- **No single SIP↔SIP loopback E2E with SDES video in both roles** — the layers (media keying,
  offer, answer, RTX keying) are each proven separately (video-sdes-offer log caveat).
- A pre-existing `IsSdesSecuredProfile` (exact-equals) vs `IsSecureProfile` (`Contains("SAVP")`)
  substring divergence for exotic non-DTLS-SAVP profiles (e.g. `TCP/TLS/RTP/SAVP`) — no real RTP
  transport, noted follow-up, not introduced here.
- SDES video is only reachable once the SDP `a=crypto` negotiation populates the params — the
  media-layer keying alone is not a production path (documented across the SDES slices).

## Guardrails

- Set-but-unparsable SRTP material throws (fail closed); a keyless-secure m-line is declined.
- Offerer key reused across Hold/Unhold (regression: `Hold_reoffer_reuses_the_live_video_key`).
- Audio, video, and video-RTX contexts stay independent (no two-time pad); answer key ≠ offer key.
- Negotiator and resolver stay in lockstep on DTLS/SDES precedence (full-matrix tested).
- All owned SDES contexts disposed exactly once, keys zeroed, session only borrows.

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-video-sdes-keying.md`,
  `…-video-sdes-offer.md`, `…-video-sdes-sdp-answer.md`, `…-video-sdes-rtx-keying.md`,
  `…-video-dtls-sdes-precedence.md`.
- Code (graphify-verified): `src/Core/Infrastructure/Rtp/SdesMediaCryptoContextFactory.cs`
  (`TryCreate` L40, `TryCreateSecondarySrtp` L59, `TryParseKeyMaterial` L74);
  `src/Core/Infrastructure/Rtp/VideoRtpStream.cs` (SDES build L144, RTX SDES L194, dispose L512);
  `src/Core/Infrastructure/Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs`
  (`TryNegotiateVideoAnswerMedia` SDES/DTLS branch L488-523);
  `src/Core/Infrastructure/Sdp/SdpSecurityInspector.cs` (`IsDtlsProfile`);
  `src/Core/Infrastructure/Sdp/SdpUtilities.cs` (`TryResolveVideoParameters` L521);
  `src/Core/Infrastructure/Sdp/OfferAnswer/SdesCryptoSelector.cs`;
  `src/Core/Domain/Calls/CallVideoParameters.cs`.
- Related: ADR-C04-01 (audio SDES offer/answer), C05 (DTLS-SRTP foundation).
- RFC: 4568 §5.1.3/§7.1.3 (SDES answer/key generation), 4588 §9 (RTX keying), 5763 (DTLS-SRTP,
  precedence), 3711 §4.3.2 (session-key derivation).
