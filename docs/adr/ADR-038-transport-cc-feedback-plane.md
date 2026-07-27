# ADR-038: Transport-Wide Congestion Control — Feedback Plane (seq stamping, arrival recording, RTCP feedback)

Status: Accepted
Date: 2026-07-15

## Context

Video bandwidth estimation needs a signal the sender can act on. Transport-wide congestion
control (transport-cc) is the WebRTC-de-facto mechanism: the **media sender** stamps every
outbound RTP packet with a monotonic transport-wide 16-bit sequence number as an RFC 8285
one-byte header extension; the **receiver** records each packet's arrival time keyed by that
number and periodically returns an RTCP feedback report (RTPFB PT=205, FMT=15,
draft-holmer-rmcat-transport-wide-cc-extensions-01; the modern equivalent is RFC 8888 ccfb).
That report lets the original sender reconstruct per-packet one-way delay and loss.

This ADR covers the **feedback plane** — everything on the wire and both packet paths that
produce and carry the signal, up to (but not including) the sender-side estimator/rate policy,
which is [C10-02]. The chain was built as ~8 isolated, individually reviewed slices ("BWE
foundation N/N"), each merged inert, so that no partial capability was advertised before the
whole path stood.

### Verified current state

Code-grounded via graphify (`explain "congestion control"`, `query "transport-wide feedback"`)
plus reads of the named files:

- **Seq stamping (send).** `RtpSession` allocates `_transportCcSequence` under the send lock
  alongside the RTP sequence and stamps it **before** SRTP-protect (RFC 3711: the header
  extension is authenticated, not encrypted, so the receiver reads it in clear-text). The
  negotiated `a=extmap` id flows from SDP through `CallVideoParameters.TransportWideCcExtensionId`
  and the ICE/SRTP enrichers into `RtpSessionOptions.TransportWideCcExtensionId`
  (`CallMediaParametersIceEnricher.cs:99`, `CallMediaParametersSrtpEnricher.cs:119`,
  `VideoRtpStream.cs:165/218`). The stamp/read helpers live in
  `OneByteRtpHeaderExtensions` (`TransportSequenceNumber` write; `TryReadTransportSequenceNumber`
  allocation-free inline scan, 2-byte big-endian).
- **Arrival recording (receive).** `TransportCcArrivalRecorder`
  (`Infrastructure/Rtp/CongestionControl/`) is a fixed-capacity ring buffer under one `_sync`
  lock; `Record` is allocation-free (`TransportCcArrival` is a `readonly record struct`),
  overwrite-oldest on overflow with an observable `DroppedCount` (no silent loss). Timestamp is
  passed in (injectable clock, unit-testable).
- **Wire codec.** `RtcpTransportFeedbackCodec` (`Infrastructure/Rtcp/Wire`) encodes/decodes the
  FMT=15 message per draft §3.1: base seq | status count | 24-bit signed reference time (64 ms
  units) | fb pkt count, then two-bit status-vector chunks, then receive deltas (250 µs units),
  32-bit padded. Decode accepts all chunk forms (run-length, one-bit, two-bit vectors),
  sign-extends the reference time, rejects reserved symbol 3, and guards truncation (no OOB read).
  Model types: `RtcpTransportFeedback` + top-level `RtcpTransportFeedbackStatus`
  (`Application/Media/Rtcp/Packets`).
- **Feedback builder.** `TransportCcFeedbackBuilder.Build(...)` turns a recorder `Drain()` batch
  into the `RtcpTransportFeedback` model: 16-bit unwrap relative to the first seq (contiguous
  across the 65535→0 boundary, 32767-span guard), gap-fill (missing seq = not-received),
  reference time via `FloorDiv` from a shared epoch, receive deltas computed against the
  reconstructed clock (no cumulative rounding drift), duplicate-collapse to earliest arrival,
  overflow-safe `ToMicros`.
- **Receive-side sender loop.** `TransportCcFeedbackSender` records each stamped inbound video
  packet and sends a report ~every 100 ms over the RTCP-mux channel
  (`Drain`→`Build`→`Encode`→`sendControl`). Packet-triggered on the receive-loop thread (single
  consumer, no timer thread), injected time source, build/encode failures logged-and-skipped (no
  crash, no silent catch), `DroppedCount` growth debug-logged. Wired in `VideoRtpStream` and
  constructed only when the extmap was negotiated.
- **RTCP dispatch.** `RtcpFeedbackCodec` routes `(TransportFeedback, FMT=15)` to
  `RtcpTransportFeedbackCodec` on both encode and decode, so transport-cc rides the normal
  compound RTCP path. `CallRtcpQualityMonitor`'s typed switch has no `default`, so the new type
  is ignored there (like NACK/PLI) — no regression.
- **Live offer — DIVERGES from every C10 log.** `SipCoreCallChannel.cs:774` now sets
  `HeaderExtensionUris = [RtpHeaderExtensionUris.TransportWideCc]` on the video offer;
  `SdpUtilities.cs:598/609` resolves the id from the answer (Ordinal match on the draft URI).
  All eleven C10 logs describe the chain as **gate-off / extmap not offered / inert**; the offer
  is now wired, and `TransportCcExtmapOfferTests` covers it.

## Decision

Build transport-cc as a **per-slice, inert-until-complete feedback plane** with a strict
separation between wire/transport plumbing (this ADR) and estimation/policy ([C10-02]):

1. **Stamp before SRTP, read from clear-text.** The transport-wide seq is an authenticated,
   unencrypted RFC 8285 extension; the sender stamps it under the send lock (monotonic,
   thread-safe, `unchecked` wrap), the receiver reads it allocation-free on the hot path.
2. **Receiver captures, never estimates.** Arrival recording is a bounded, allocation-free,
   drop-observable ring; the feedback builder and wire codec are pure transformations. The
   receiver's only job is to report facts (arrivals + gaps) back to the sender.
3. **One wire codec on the central dispatch.** transport-cc encode/decode lives in one codec
   reached through the existing compound-RTCP path, not a side channel.
4. **Opportunistic, gate-off offer.** Offer the extmap; a peer that echoes it enables
   transport-cc, a peer that omits it leaves the stream byte-identical to before. No premature
   capability advertising — the offer was wired only once the full chain (including [C10-02]'s
   estimator) stood.

### Crux

The feedback plane is deliberately **policy-free**: it reconstructs per-packet delay and loss
but makes no bandwidth decision. That boundary is what let each slice merge inert and be reviewed
in isolation, and it is what keeps the SDK transport-only — the receiver reports, the sender's
estimator ([C10-02]) decides, the application encodes.

## Consequences

Positive: eight independently reviewed, individually inert slices composed into a working
feedback path; the wire format is verified bit-for-bit against the draft; the hot paths are
allocation-free and the overflow behaviour is observable, not silent.

Honest divergences and limitations:

- **The C10 logs are stale on liveness.** Every log says "gate-off / not offered / inert."
  Current code offers the extmap (`SipCoreCallChannel.cs:774`) — the chain is **live**, gated
  only by peer support. This ADR documents the live state; the logs document the build-up.
- **Draft URI, not the RFC 8888 URN.** The resolver matches the libwebrtc draft URI Ordinal-only
  (`RtpHeaderExtensionUris.TransportWideCc`); a registered/RFC-8888 URN is a marked follow-up.
  Interop today is with Chrome/libwebrtc-style peers.
- **Encoder is not compact.** `RtcpTransportFeedbackCodec.Encode` emits only two-bit vector
  chunks (no run-length optimisation) and a receive-gap > ~8.19 s between packets makes `Encode`
  throw (int16 delta) — the ~100 ms feedback window keeps this out of range; window-splitting is
  open.
- **RTX packets do not participate** in the transport-wide seq; buffer pooling for the stamp
  path is a noted perf follow-up (a few heap allocs/packet).
- **Malformed inbound feedback discards the whole compound** (ArgumentException), the pre-existing
  behaviour of all feedback decoders; fault-tolerant per-type decoding is a noted follow-up.

## Guardrails

- The transport-wide seq is stamped **before** SRTP-protect and read from the authenticated,
  unencrypted extension region — never from ciphertext.
- Arrival recording stays bounded and allocation-free with observable drops; no unbounded queue
  on the receive hot path.
- The extmap stays **opportunistic**: a non-supporting SIP peer leaves the stream byte-identical
  (gate-off), asserted by `TransportCcExtmapOfferTests` / negotiation tests.
- The wire codec stays the single transport-cc coder on the compound-RTCP dispatch; new RTCP
  feedback types must not silently break the `CallRtcpQualityMonitor` switch.

## Sources

- Logs (`docs/archive/agent-log/`): `2026-07-15-dev-transport-wide-cc-seq.md`,
  `…-transport-cc-arrival-recorder.md`, `…-transport-cc-feedback.md`,
  `…-transport-cc-feedback-builder.md`, `…-transport-cc-feedback-sender.md`,
  `…-transport-cc-rtcp-dispatch.md`, `…-transport-cc-sender-findings.md`.
- Code: `OneByteRtpHeaderExtensions.cs`, `RtpHeaderExtensionUris.cs`,
  `Infrastructure/Rtp/CongestionControl/{TransportCcArrival,TransportCcArrivalRecorder,
  TransportCcFeedbackBuilder,TransportCcFeedbackSender}.cs`,
  `Infrastructure/Rtcp/Wire/{RtcpTransportFeedbackCodec,RtcpFeedbackCodec}.cs`,
  `Application/Media/Rtcp/Packets/RtcpTransportFeedback*.cs`,
  `Infrastructure/Sip/Adapters/SipCoreCallChannel.cs:774`, `Infrastructure/Sdp/SdpUtilities.cs:598`.
- RFC/marker: RFC 8285 (one-byte header extension), RFC 3711 §3 (auth-not-encrypted extension),
  draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1 (FMT=15 wire format), RFC 8888 (ccfb,
  follow-up URN). Findings register: `docs/audit/CODE_FINDINGS_REGISTER.md`.
