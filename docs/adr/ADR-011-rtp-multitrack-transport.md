# ADR-011: Multi-Track RTP Transport for BUNDLE

Status: Accepted
Date: 2026-07-15

## Context

BUNDLE (RFC 8843, ADR-010) needs one shared 5-tuple / UDP socket / DTLS association / ICE agent
carrying multiple media streams (audio + video), each with its own SSRC, sequence space,
payload type, and RTX. The routing brain and outbound stamping are already built and on `main`:
MID header-extension codec (B1), the §9.2 demultiplexer (B2a) + factory, the track router (B2b),
and outbound MID stamping in the send path (B2c-out). What remains — B2c-in — is the transport
itself: making one transport serve multiple tracks. This is the invasive part and needs a design
before code.

### Verified current state

- `RtpSession` is **single-stream**: one UDP socket, one SSRC, one sequence counter, one
  timestamp, one outbound/inbound SRTP + SRTCP context, plus the RFC 7983 packet-type demux
  (STUN/DTLS/RTP/RTCP), transport-cc stamping, and the RTX secondary path — all for one stream.
- **Video runs a second, fully separate `RtpSession`** (`VideoRtpStream`) with its own socket/port,
  DTLS association, ICE/consent, and SRTP contexts.
- **`SrtpContext` is single-SSRC**: it keeps one sender index and one 64-packet replay window
  (`_replayWindowIndex`/`_replayWindowBitmap`); the SSRC only feeds the IV (`BuildIv`), it does not
  key per-SSRC ROC/replay state. Today that is fine — one context per stream.

## Decision

Do **not** rework the battle-tested single-stream `RtpSession` into a multi-track type — that would
destabilise every audio call. Instead:

1. **Extract the reusable low-level primitives** both paths share — the UDP socket send/receive and
   the RFC 7983 packet-type demux — so the new bundled transport reuses them without duplicating and
   without touching `RtpSession`'s stream logic.
2. **Build `BundledMediaTransport` alongside** `RtpSession`: it owns the shared socket, DTLS, ICE,
   the shared SRTP master key, packet-type demux, and rtcp-mux, and hosts N **tracks**. A track owns
   the per-SSRC stream state (sequence, timestamp, payload type, RTX, transport-cc) and stamps MID on
   outbound (via the built stamper). Inbound is packet-type-demuxed → SRTP-unprotected → routed by the
   built `BundledTrackRouter` → the track's receive.
3. Non-BUNDLE calls keep using `RtpSession` unchanged; only BUNDLE calls use `BundledMediaTransport`.

### Crux design pieces

- **Per-SSRC SRTP.** Under one shared DTLS-derived master key, ROC and the replay window are
  per-SSRC (RFC 3711 §3.2: the cryptographic context is per-SSRC). `SrtpContext` must key its ROC +
  replay state by SSRC (a per-SSRC state map under the shared key), for both directions.
- **Primitive reuse.** Pull the socket loop and packet-type demux out of `RtpSession` into a shared
  unit so `BundledMediaTransport` does not fork them.
- **RTCP.** Compound RTCP routed by sender/media SSRC (rtcp-mux already shares the port); SR/RR per
  track.

## Sub-slice plan (B2c-in → B6)

- **B2c-in-1 — Per-SSRC SRTP state.** Extend `SrtpContext` to keep ROC + replay window per SSRC under
  the shared key. Isolated + testable (protect/unprotect two SSRCs independently, replay per SSRC).
  Prerequisite for shared-key multi-track. **Recommended first slice.**
- **B2c-in-2 — Extract shared transport primitives.** Socket send/receive + RFC 7983 packet-type
  demux into a reusable unit; `RtpSession` delegates to it (behaviour-neutral, existing suite green).
- **B2c-in-3 — `BundledMediaTransport` inbound.** Compose the primitives + shared inbound SRTP +
  `BundledTrackRouter` → per-track receive.
- **B2c-in-4 — `BundledMediaTransport` outbound multi-track.** Per-track sender: own SSRC/seq/PT +
  MID stamp → shared SRTP protect → shared socket. The hard part.
- **B3 — Shared DTLS + ICE consent** for the transport (one association / one agent, one consent loop).
- **B4 — Video as a track** — `VideoRtpStream` sends/receives over the shared transport instead of its
  own `RtpSession`; its codec/packetisation/keyframe/reorder/transport-cc logic stays.
- **B5 — SDP BUNDLE generation** — `a=group:BUNDLE`, one port, session-level ICE, `extmap sdes:mid`.
- **B6 — Browser-interop validation** (Chrome/Firefox).

## Consequences

Positive: the proven single-stream `RtpSession` (all audio today) is untouched; BUNDLE is additive.
The routing brain, config, and outbound stamping are already done and tested, so B2c-in is transport
plumbing over a finished routing layer.

Tradeoffs: two transport types (`RtpSession` + `BundledMediaTransport`) sharing extracted primitives —
governance needed to keep them from diverging. Per-SSRC SRTP touches the crypto hot path (careful
review + tests). B2c-in-2 (primitive extraction) refactors `RtpSession` internals behaviour-neutrally
— the full send/receive/SRTP suite is the safety net.

## Guardrails

- Single-stream `RtpSession` behaviour stays byte-identical through the extraction (test suite green).
- Per-SSRC SRTP change reviewed against replay/ROC correctness with per-SSRC tests.
- No `a=group:BUNDLE` offered until `BundledMediaTransport` serves both tracks (B5 after B2c-in/B3/B4).

## Errata (2026-07-27, verified against code during ADR backfill)

The sub-slice plan was written before code; two later mechanism choices diverge from the prose here
(decision unchanged, wording lagged the final placement):

- **B4 — video as a track.** Implemented as a *new* `BundledVideoTrack` riding the shared transport,
  **not** by converting `VideoRtpStream`. `VideoRtpStream` is retained deliberately as the non-BUNDLE
  separate-m-line video path (per ADR-010's guardrail). The decision (video as a track on the shared
  transport) holds; the mechanism is a parallel type. See ADR-043…048 (video path) and ADR-046.
- **B5 — SDP BUNDLE generation.** Lives on `SdpOfferAnswerNegotiator`, driven by
  `SdpMediaOptions.Bundle` (`Infrastructure/Sdp/OfferAnswer/`) — not on the SIP `SdpMediaNegotiationOptions.Bundle`
  named in the plan (that field does not exist). This is consistent with ADR-010's "no BUNDLE code in
  the SIP signaling core" guardrail; the plan prose predates the SIP/WebRTC split.

Per-SSRC SRTP (Crux #1) and the `BundledMediaTransport`-alongside-`RtpSession` model (Crux #2) were
verified as implemented essentially as written (`SrtpContext` per-SSRC `SrtpSsrcState`, shared
DTLS/ICE via `BundledDtlsKeying`/`BundledIceControl`).
