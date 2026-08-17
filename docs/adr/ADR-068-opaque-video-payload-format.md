# ADR-068: Opaque Video Payload Format for End-to-End Encrypted Frames

Status: Accepted
Date: 2026-08-17

## Context

When a browser encrypts its frames before the packetiser — WebRTC Encoded Transform / SFrame
(RFC 9605), the precondition for any end-to-end encrypted conference — the frame content is
ciphertext for everything downstream. Both of the SDK's video payload formats assume they may read
it (#223):

- **H.264 fails closed on principle.** `H264Depacketiser` dispatches on the NAL type, parses STAP-A
  aggregation sizes and refuses what it cannot classify ("never a corrupted access unit"). An opaque
  frame is malformed by that definition, so nothing arrives at all. `H264Packetiser` is the mirror
  image: it runs the Annex-B parser to find NAL boundaries that no longer exist.
- **VP8 is worse than broken, it is plausible.** `Vp8Depacketiser` derived the key-frame flag from
  the first byte of the VP8 payload header (RFC 7741 §4.3 → RFC 6386 §9.1) — which is the first byte
  of the *frame*, hence ciphertext. The descriptor in front of it stays in the clear, so the packet
  parses fine and the flag is simply random: wrong key-frame detection, PLI storms, participants
  without a picture.

The compliance driver is Anlage 31b BMV-Ä § 2 Abs. 3/4 (Videosprechstunde, § 365 SGB V): end-to-end
encryption over the whole path, and the provider must be *unable* to see or store content. While a
depacketiser reaches into the payload, "unable" is a statement about intent, not capability.

### What the reference implementations do

This was researched before choosing a design, because the wire format is not a free choice.

- **Nobody makes the payload opaque on the wire.** Shipping E2EE implementations leave exactly the
  bytes the packetiser and the SFU must read in the clear and encrypt the rest. Jitsi
  (`lib-jitsi-meet/modules/e2ee/Context.ts`) is explicit: `UNENCRYPTED_BYTES = { delta: 3, key: 10,
  undefined: 1 }`, with the comment "This allows the bridge to continue detecting keyframes … and is
  also a bit easier for the VP8 decoder". libwebrtc's frame cryptor (as used by LiveKit) keeps
  per-codec "unencrypted header bytes"; for H.264 the clear portion must extend to the NAL headers,
  and the ciphertext must be RBSP-escaped so it cannot emulate a start code.
- **H.264 is the hard case for a reason.** Chrome's and Firefox's H.264 packetisers run *after* the
  transform and still expect Annex-B structure. Touching the start codes destroys packetisation, so
  applications must know which bits to leave alone.
- **Key-frame and dependency information belongs outside the payload.** RFC 9605 §6.1/§6.2 has the
  SFU working from RTP metadata, with key-frame requests outside the encryption layer — which is what
  the Dependency Descriptor header extension exists for.

Two consequences follow, and they shape the decision. First, the *receive*-side defect in #223 is
real and worth fixing: the SDK must stop *depending* on payload semantics. Second, a fully opaque
H.264 *send* path cannot be browser-facing — raw ciphertext in Annex-B framing runs straight into the
start-code emulation problem the references guard against with RBSP escaping.

## Decision

A **second** payload-format pair, selected explicitly, that works from the RTP framing alone. The
existing pair is untouched and stays the default: it is correct for clear media, and its key-frame
detection is a feature there.

1. **VP8 keeps its packetiser.** `Vp8Packetiser` only prepends the payload descriptor and never looks
   at the frame — it was already opaque. Only the receive side changes: `OpaqueVp8Depacketiser` reads
   the descriptor through the same `Vp8PayloadDescriptor` helper as the non-opaque one and differs in
   exactly one thing, which is the point — it makes **no** key-frame claim instead of a random one.

2. **H.264 replaces both halves.** `OpaqueH264Packetiser` synthesises the NAL header RFC 6184
   requires in every packet (F=0, NRI=3, non-IDR type 1) and carries the frame verbatim as FU-A
   fragment payload; `OpaqueH264Depacketiser` reads the indicator's type and S/E, nothing else, and
   emits the concatenated fragments with no start codes and no reconstructed header byte. A round trip
   is therefore byte-identical for arbitrary content.

3. **Every frame is fragmented**, including one that would fit a single packet (RFC 6184 §5.8 only
   discourages that — SHOULD NOT, not MUST NOT). A single-NAL packet would place the frame's first
   byte where a receiver reads the NAL type, so ciphertext beginning `0x1C` or `0x18` would be read
   as FU-A or STAP-A framing. With FU-A throughout, every type field is ours.

4. **Single-NAL and STAP-A are refused on receive** (counted as discards) rather than guessed at. For
   opaque data their leading byte is content, and treating content as a header is the class of bug
   this path removes.

5. **Neither pair claims a key frame.** `isKeyFrame` is always `false`. The signal has to arrive in a
   plaintext RTP header extension (Dependency Descriptor) — tracked as follow-up work on #223. This
   is safe today because the flag feeds a statistics counter and the application event, not a PLI
   decision.

6. **Structural provability over a flag.** The opaque path is separate types rather than a mode flag
   on the existing ones. For a requirement phrased as "the provider *cannot* see the content", an
   auditor should be able to establish that by reading the type, not by tracing which value a boolean
   had at runtime.

7. **The switch is scoped to the peer, not the track.** `WebRtcConfiguration.OpaqueVideoFrames` (and
   its `WebRtcOptions` / `CalloraWebRtcBuilder.WithOpaqueVideo` siblings) selects the format for every
   video track of that peer — those built at session time and those a later renegotiation adds. Two
   reasons, both decisive. The requirement covers the whole session, not one stream: "the provider
   cannot see the content" is not a per-m-line property. And SDP carries no attribute for it, so the
   policy cannot be re-derived from the descriptions the session factory works from — a per-track
   choice would need its own non-SDP channel through the factory, the renegotiator and the
   added-track path, for a case (one encrypted and one clear video stream on one peer) that the
   driver does not have. `VideoTrackOptions` therefore documents the peer-level switch instead of
   duplicating it.

8. **No default on the internal seam.** `WebRtcSessionFactory.TryBuildVideoTrack` takes the policy as
   a required parameter and `WebRtcRenegotiator` takes it in its constructor. A defaulted parameter
   would let a future caller silently hand an end-to-end encrypted peer a clear-media track — the
   exact defect this ADR removes — so forgetting to thread it is a compile error instead.

### Interop scope, stated rather than assumed

The opaque H.264 framing is self-consistent between two SDK endpoints and through a relay that
forwards payloads untouched. **It is not what a browser emits.** Receiving H.264 from a browser whose
transform keeps NAL headers in the clear — the shape every reference implementation produces — is the
non-opaque path's job, and what that path needs is not opacity but the key-frame signal moved out of
the payload. Browser interop for the opaque path is unvalidated and must not be claimed.

## Consequences

- An E2EE-capable transport exists and is provable by inspection: with the opaque pair selected, no
  byte of the frame is read, interpreted or altered in either direction.
- The clear-media paths are byte-for-byte unchanged, including key-frame detection.
- Key-frame-derived statistics are zero on an opaque stream until the Dependency Descriptor work
  lands. Consumers that gate on `isKeyFrame` must treat "false" as "unknown" there.
- The switch is reachable from all three configuration levels the facade offers (direct
  `WebRtcConfiguration`, DI `WebRtcOptions`, fluent builder) and defaults to off, so no existing
  app's media path changes under it.
- The public-API record criterion 4 asks for is the repository's `PublicApi.approved.txt` baseline
  (ADR-006 §4, enforced by `PublicApiSurfaceTests`), updated in the same commit — three added public
  members and nothing else. There is no `PublicAPI.Unshipped` file or `PublicApiAnalyzers` reference
  here; the approval baseline is this repository's form of the same gate.

## References

- RFC 6184 §5.6/§5.7.1/§5.8 (H.264 RTP payload format), RFC 7741 §4.2/§4.3 (VP8), RFC 6386 §9.1
- RFC 9605 (SFrame) §6.1 Selective Forwarding Units, §6.2 Video Key Frames
- Anlage 31b BMV-Ä § 2 Abs. 3/4 (Videosprechstunde nach § 365 SGB V)
- `jitsi/lib-jitsi-meet` `modules/e2ee/Context.ts` — `UNENCRYPTED_BYTES`
- webrtcHacks, "True End-to-End Encryption with WebRTC Insertable Streams"
- Issue #223 and the follow-up issues on two-byte header extensions and the Dependency Descriptor
