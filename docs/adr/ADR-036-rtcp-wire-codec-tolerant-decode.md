# ADR-036: RTCP Wire Codec — Feedback/XR Framing and Tolerant Compound Decode

Status: Accepted
Date: 2026-07-15

## Context

Beyond the SR/RR/SDES/BYE base set (RFC 3550 §6), the SDK's RTCP path must (a) speak the
feedback wire formats that WebRTC and modern SIP peers use — Generic NACK (RFC 4585 §6.2.1),
PLI (RFC 4585 §6.3.1), FIR (RFC 5104 §4.3.1) — and (b) parse inbound Extended Reports
(RFC 3611) far enough to surface the peer's VoIP-quality metrics. Both are *inbound-untrusted*
wire: a compound RTCP datagram typically *starts* with the SR/RR the quality monitor needs, and
one malformed or simply-unrecognized trailing packet must never cost the whole datagram. This is
the K4 parse-robustness contract applied to the RTCP demux.

Three slices built this incrementally: the feedback wire representation (`2026-07-14`), XR VoIP-
metrics decode (`2026-07-09`), and a review-driven robustness fix that moved malformed-feedback
handling from "throw and drop the datagram" to "skip just that packet" (`2026-07-15`).

### Verified current state

- **`RtcpPacketType`** (`Application/Media/Rtcp/Packets/RtcpPacketType.cs`) enumerates
  `SenderReport=200 … App=204`, plus `TransportFeedback=205` (RTPFB), `PayloadFeedback=206`
  (PSFB), `ExtendedReport=207`.
- **`RtcpFeedbackCodec`** (`Infrastructure/Rtcp/Wire/RtcpFeedbackCodec.cs`) is a dedicated codec
  kept apart from the base `RtcpPacketCodec`. The common feedback layout after the 4-byte header
  is `sender SSRC(4) | media SSRC(4) | FCI` (RFC 4585 §6.1), and the feedback message type (FMT)
  travels in the header's low 5 bits — where SR/RR carry a report count. It decodes/encodes
  `RtcpPictureLossIndication` (PSFB FMT=1), `RtcpFullIntraRequest` (PSFB FMT=4, with the common-
  header media-SSRC pinned to 0 per RFC 5104 §4.3.1 and real targets in the FCI entries),
  `RtcpGenericNack` (RTPFB FMT=1, PID+BLP with `LostSequenceNumbers()` bitmask expansion), and
  dispatches transport-wide-cc (RTPFB FMT=15) to `RtcpTransportFeedbackCodec`. Unknown `(type,fmt)`
  combinations return `null` (skipped). Truncated/inconsistent FCI throws `ArgumentException`.
- **`RtcpPacketCodec.DecodeXr`** (`Infrastructure/Rtcp/Wire/RtcpPacketCodec.cs`) parses the XR
  SSRC then iterates typed blocks (`BT(1) | type-specific(1) | block-length(2 words) | content`,
  RFC 3611 §2). VoIP-Metrics blocks (BT=7, §4.7, 32-byte content) decode into
  `RtcpVoipMetricsBlock`; other block types are skipped via block-length; an inconsistent block
  length **`break`s** the block loop rather than throwing, so a bad XR block does not discard the
  compound.
- **`RtcpPacketCodec.Decode`** is the compound driver. It walks packets by the length field, and
  for feedback types routes through **`DecodeFeedbackTolerant`**, which wraps
  `RtcpFeedbackCodec.Decode` in `try/catch (ArgumentException) → null`. The catch is scoped to
  `ArgumentException` so a genuine decoder bug (`IndexOutOfRange`) still propagates. The outer
  length-field validation still throws on a datagram-level inconsistency (that advances the read
  offset), but a malformed *feedback FCI* now yields "skip this packet, keep the compound".
- **Consumption is wired:** `CallRtcpQualityMonitor.HandleExtendedReport`
  (`Application/Media/CallRtcpQualityMonitor.cs`) reads the peer's VoIP-Metrics block keyed by our
  local SSRC (RFC 3611 §4.7 — the peer reports on the stream it received from us) and surfaces
  remote MOS-LQ/MOS-CQ (`MosFromByte`: score×10, 0/127 = unavailable). This closes the XR-decode
  log's stated follow-up ("metrics not yet aggregated in the monitor") — see divergence below.

## Decision

Model RTCP inbound parsing as **two layers with opposite strictness**, and keep feedback framing
in its own codec:

1. **Sub-codecs are strict.** `RtcpFeedbackCodec` and `DecodeXr`'s per-block/per-FCI parsing throw
   `ArgumentException` on a truncated or inconsistent payload. Unrecognized formats (unknown FMT,
   unknown XR block type) are *not* errors — they return `null` / are skipped by length.
2. **The compound layer is tolerant.** `RtcpPacketCodec.Decode` catches sub-codec
   `ArgumentException` for feedback packets (`DecodeFeedbackTolerant`) and `break`s the XR block
   loop on inconsistency, so one bad or unknown trailing packet never discards the SR/RR the
   datagram carries (RFC 3550 §6.1: unrecognized packet types are skipped via the length field).
3. **Feedback framing lives in `RtcpFeedbackCodec`**, separate from the base RTCP codec, because
   the FMT-in-header-bits convention and the SSRC-pair + FCI layout are distinct from the RC-based
   SR/RR framing; the base codec dispatches 205/206 to it and falls through on encode.
4. **XR is decode-only** for the VoIP-Metrics block (BT=7). We do not emit XR; other block types
   (RRT/DLRR §4.4/§4.5 etc.) are skipped, not parsed.

### Crux

The security/robustness boundary is *strict inner, tolerant outer*. Untrusted RTCP is length-
delimited, so the read offset is always recoverable from the length field regardless of a
packet's internal validity — that is exactly what lets the compound layer swallow a malformed
sub-packet without losing frame sync. Scoping the tolerance to `ArgumentException` (the type the
sub-codecs deliberately throw for wire-malformation) keeps real logic bugs loud. A single
malformed NACK from a peer can therefore never suppress the SR/RR that drives quality reporting.

## Consequences

Positive: the RTCP path is DoS/junk-tolerant at the datagram boundary while individual decoders
stay honest; feedback and base framing are cleanly separated; inbound XR MOS is available to the
quality monitor.

Honest divergence:
- **The XR-decode log (2026-07-09) is now stale on its own follow-up.** It claimed the parsed
  metrics were *not yet* consumed by `CallRtcpQualityMonitor`; the current tree *does* consume
  them (`HandleExtendedReport` → remote MOS-LQ/CQ). The rest of the log (decode-only, no XR send,
  other block types skipped) still holds.
- **XR is inbound-only.** We never send Extended Reports, and only the VoIP-Metrics block is
  decoded — RRT/DLRR-based RTT (an alternative to the SR/DLSR path) is not implemented.
- **Feedback framing predates its live use here.** The wire codec (Slice 1) was built before the
  session wiring; NACK→RTX, PLI/FIR→keyframe, and transport-cc live handling are separate clusters
  (C08/C10/C12). This ADR covers the *wire representation and tolerant demux* only.
- **No foreign-stack interop** validated for the feedback/XR wire at codec time — roundtrip and
  byte-layout tests only.

## Guardrails

- Compound decode must survive a malformed or unknown trailing packet: SR/RR that leads the
  datagram is preserved (regression-guarded by `RtcpFeedbackCodecTests` — "…skipped_and_the_
  compound_survives", and `RtcpExtendedReportDecodeTests` — unknown-block-type keeps the envelope).
- Tolerance stays scoped to `ArgumentException`; sub-codecs keep throwing on malformed FCI so a
  decoder bug (`IndexOutOfRange`) is not silently swallowed (K4 — parse failures observable).
- The compound length-field validation in the main loop still throws on datagram-level
  inconsistency (frame-sync loss is fatal, per-packet malformation is not).
- FIR common-header media-SSRC stays 0 (RFC 5104 §4.3.1); NACK BLP bit *i* → PID+*i*+1 with
  ushort wrap; byte layout regression-guarded by roundtrip tests.
- The `return null;` catch body is not a silent catch under R5 (the rule targets empty/comment-only
  bodies); its intent is documented inline (RFC 3550 §6.1).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-rtcp-feedback-wire.md`,
  `docs/archive/agent-log/2026-07-09-dev-b9-rtcp-xr.md`,
  `docs/archive/agent-log/2026-07-15-dev-rtcp-tolerant-feedback-decode.md`
- Code (graphify-verified): `Rtcp/Wire/RtcpFeedbackCodec.cs`
  (`.Decode()`/`.Encode()`/`.DecodeFir()`/`.DecodeNack()`/`.EncodePli()`),
  `Rtcp/Wire/RtcpPacketCodec.cs` (`.Decode()`, `.DecodeFeedbackTolerant()`, `.DecodeXr()`,
  `.DecodeVoipMetrics()`), `Rtcp/Packets/RtcpPacketType.cs`,
  `Rtcp/Packets/RtcpExtendedReport.cs` / `RtcpVoipMetricsBlock.cs`,
  `Application/Media/CallRtcpQualityMonitor.cs` (`.HandleExtendedReport()`, `.MosFromByte()`);
  tests `RtcpFeedbackCodecTests`, `RtcpExtendedReportDecodeTests`, `RtcpCompoundDecodeTests`
- Git: `f12c7a6` (tolerant feedback decode merge), `26816e5` (decode each inbound RTCP compound
  once, then fan out, #14)
- Markers/RFC: RFC 3550 §6.1/§6.4.1; RFC 4585 §6.1/§6.2.1/§6.3.1; RFC 5104 §4.3.1; RFC 3611
  §2/§4.7; ENGINEERING_RULES K4 (trust-boundary parse robustness), K5, R5 (no silent catch), K7
