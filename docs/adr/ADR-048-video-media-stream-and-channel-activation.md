# ADR-048: Video Media Stream and SIP Channel Activation

Status: Accepted
Date: 2026-07-14

## Context

Once video can be negotiated in SDP (ADR-C12-01), encoded frames must actually flow: a media
stream that packetises frames onto an RTP session on the video port, and a config-gated
activation path so video is reachable from a real SIP call (not only from directly-constructed
`CallMediaParameters`). This is the transport plumbing that ties the C12 pieces
(packetisation, reorder, feedback, security, ICE) into one stream object and exposes it to the
public API. No BUNDLE — the video stream owns its own socket/port/DTLS association.

### Verified current state (graphify-grounded)

- `IVideoMediaStream` (Application port): `CodecName`/`PayloadType`, `SendFrameAsync(frame,
  rtpTimestamp, ct)`, `event FrameReceived(byte[], uint, bool)`, `event KeyFrameRequested`, plus
  the congestion-recommendation surface (`RecommendedBitrateBps`, `NetworkQuality`,
  `CongestionUpdated`). `ICallMediaSession.Video` is a default interface member returning `null`
  so audio-only sessions and test fakes need no change.
- `VideoRtpStream` (`src/Core/Infrastructure/Rtp/VideoRtpStream.cs`) is the implementation and the
  composition root for the whole video path: its own `RtpSession` on the video port (L148-168),
  packetiser/depacketiser by codec (`VideoPayloadFormat.Create`, L138), its own
  `DtlsMediaAttachment` with the video remote endpoint as override (L245-249, RFC 5763: one
  association per m-line without BUNDLE), and frame-atomic send under a `SemaphoreSlim`
  (`SendFrameAsync`, L370-391) so two frames' packets never interleave. `TryCreate` (L345-358)
  returns `null` for audio-only legs and throws up front on a codec with no payload format or a
  DTLS leg missing its dependencies (fail closed). Ordered `DisposeAsync` (L479-525) tears down
  before the socket, mirroring audio.
- Frames carry explicit timestamps; `SamplesPerPacket = ClockRate / 30` is a nominal frame
  interval to keep RTCP maths sane (L155-157). `SendTimestampedAsync` stamps the per-packet
  sequence and holds the timestamp fixed across a frame.
- Activation: `SdkConfiguration.EnableVideo` + `PreferredVideoCodecs` (default off) flow
  `VoipClient` → `SipLineChannel` (inbound + outbound) → `SipCoreCallChannel`, which reserves a
  second UDP socket/port for the `m=video` line only when enabled, released before the video RTP
  bind and idempotently on dispose. `BuildSdpOptions`/`BuildLocalSdpOptions` set
  `SdpVideoNegotiationOptions{Port, Codecs}`. `RtpCallMediaSession` wires the video stream
  (`TryCreate`, `Video` property, `Start`, teardown before the audio socket).

## Decision

1. **One `VideoRtpStream` per video leg** owning its own socket, RTP session, DTLS association,
   packetisation, feedback, security, ICE, and congestion control — the single composition point
   for the C12 stack. Not multiplexed onto audio (no BUNDLE on this path).
2. **`IVideoMediaStream` is the Application-layer port**; `ICallMediaSession.Video` defaults to
   `null` so audio-only and fakes are unaffected.
3. **Frame-atomic send**: whole frames are serialised under a semaphore; all packets of a frame
   are consecutive and share one timestamp (interleaving would corrupt the peer's reassembly).
4. **Config-gated activation.** `EnableVideo` reserves the video port and plumbs the negotiation
   options through the SIP channel; disabled is a byte-for-byte no-op. The port is stable across
   Hold/re-offer.
5. **Fail-closed construction**: `TryCreate` validates codec/DTLS dependencies up front and
   inherits `RequireEncryptedMedia`; a secure leg never sends/accepts plaintext video.

### Crux

`VideoRtpStream` is where every other C12 decision is composed, so its construction order and
teardown order carry the correctness: DTLS/SDES contexts installed before frames flow, ICE gated
on negotiation, RTX/reorder only on RTX legs, and teardown draining the receive loop and closing
DTLS before the socket. The port-reservation timing is the other load-bearing detail — the
reserved socket must be released **before** the synchronous `MediaParametersNegotiated` invoke
that binds the video RTP session, or the bind hits EADDRINUSE.

## Consequences

Positive: video is reachable from real SIP calls via one config flag; the stream is a clean
Application port; audio-only paths are untouched.

Divergence / honesty:
- **Outbound video is suppressed under an SDES-offering policy** (SDES + video is fail-closed on
  the offerer side until the SDES-video-offer slice, ADR-C12-04): outbound video needs DTLS or a
  disabled-crypto policy; inbound plain/DTLS video is always answered. Documented on `EnableVideo`.
- At the channel-activation slice, **video ICE gathered no video candidates** (host/srflx/relay
  landed in the later ICE slices, ADR-C12-05) — documented as a caveat rather than a silent gap.
- Hold/Unhold with video is code-consistent but **not E2E-tested**.
- **Transport-only**: frames flow encrypted end-to-end (loopback evidence), but there is no native
  codec — the application supplies encoded frames. **No interop against foreign stacks/browsers,
  no DONE/compliant claim.**

## Guardrails

- Video port reserved only under `EnableVideo`; released before the RTP bind and idempotently on
  dispose (leak/EADDRINUSE-free).
- Disabled path is byte-for-byte unchanged (regression net).
- Whole-frame send atomicity preserved (semaphore); one timestamp per frame.
- `TryCreate` fails closed on missing codec/DTLS deps; `RequireEncryptedMedia` inherited.
- Teardown order: stream collaborators → DTLS close_notify → RTP session → socket (mirrors audio).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-video-media-stream.md`,
  `…-video-channel-activation.md`.
- Code (graphify-verified): `src/Core/Infrastructure/Rtp/VideoRtpStream.cs` (ctor L128,
  `SendFrameAsync` L370, DTLS attach L245, `TryCreate` L345, `Start` L361, `DisposeAsync` L479);
  `src/Core/Application/Media/IVideoMediaStream.cs`; `RtpCallMediaSession` (video wiring —
  `Video` property, `SendFrameAsync` L257, `StartAsync` L243); `SipCoreCallChannel` (video port
  reservation, `BuildSdpOptions`); `SdkConfiguration` (`EnableVideo`, `PreferredVideoCodecs`);
  `src/Core/Infrastructure/Dtls/DtlsMediaAttachment.cs` (`remoteEndPointOverride`).
- Related: ADR-C12-01 (SDP), C12-03 (feedback), C12-04 (security), C12-05 (ICE); ADR-010/011
  (BUNDLE — the multiplexed alternative not taken here).
- RFC: 5763 (one DTLS association per m-line without BUNDLE), 3550 (RTP timestamps/sequence).
