# ADR-047: Video ICE — Per-5-tuple Media Layer, Shared Credentials, Full Candidate Gathering

Status: Accepted
Date: 2026-07-14

## Context

Without BUNDLE the video m-line has its **own** 5-tuple (own socket/port), so it needs its own
ICE: it must answer inbound connectivity checks (RFC 8445 §7.3) and run consent freshness
(RFC 7675) on the video socket, and it must gather and emit its own candidates (host + srflx +
relay, RFC 8839) so a strict candidate-pairing peer has something to probe. Credentials are
**session-shared** (one ufrag/pwd across all m-lines, distinct 5-tuples) — not per-m-line, not
BUNDLE. This ADR covers video ICE; the audio ICE state machine and consent primitives are the
C11 track, whose machinery this reuses; BUNDLE (one agent, one 5-tuple) is the ADR-010/011 track.

### Verified current state (graphify-grounded)

- `IceMediaParameters` (internal record) decouples the ICE view from `CallMediaParameters`, with
  `FromCall` (audio, 1:1 projection of the 7 fields the old path read) and `FromVideo` (video
  5-tuple, shared creds). `IceMediaAttachment`/`IceMediaConsentSessionFactory` take
  `IceMediaParameters` — audio stays behaviour-identical.
- `VideoRtpStream` builds `IceMediaAttachment` from `FromVideo(video)` (L256-259), wires
  `StunPacketReceived`/`Start`/`DisposeAsync`, and on `OnMediaConsentLost` logs + `StopTransmission`
  (L265-269, RFC 7675 §5.1 — socket stays open for a possible ICE restart). Inactive (a no-op)
  when ICE was not negotiated (`_iceMedia.IsActive` gate, L258). Separate consent loops for
  audio/video on separate sockets, no shared mutable state.
- Candidate gathering: `CallIceAgent.GatherTransportCandidatesAsync(endpoint, socket, ct)` was
  extracted from `BuildLocalDescriptionAsync` and is called for **both** audio and video —
  host + srflx (STUN via the socket) + relay (TURN allocation) + dedup, identical ordering,
  foundation numbering, and warn-once for a missing TURN allocator. `SipCoreCallChannel` passes
  `_localVideoSocket?.Client` as the video socket. `CallIceLocalDescription.VideoCandidates`
  carries the full host/srflx/relay set.
- Emission: `SdpOfferAnswerNegotiator` emits the shared `ice-ufrag`/`pwd` and `video.Candidates`
  on **both** the video offer and answer m-line (L554-559), only when ICE is set (SDES/DTLS
  sections untouched otherwise). `SdpUtilities.TryResolveVideoParameters` stamps the video remote
  creds with a media-level override falling back to the session-shared value
  (`video.IceUfrag ?? sharedRemoteIceUfrag`). The enricher chain runs Ice → Srtp → Dtls, and the
  SRTP rebuild carries the ICE fields through (invariant: ICE runs before SRTP).

## Decision

1. **Per-5-tuple video ICE with session-shared credentials.** The video stream runs its own
   inbound-check responder and consent loop on its own socket, keyed from one session-wide
   ufrag/pwd — not BUNDLE (which would collapse to one 5-tuple), not per-m-line credentials.
2. **Reuse the audio ICE machinery** via `IceMediaParameters` so audio is a byte-for-byte 1:1
   projection and video is a second projection of the same types — no forked ICE code.
3. **Gather the full candidate set for video** (host + srflx + relay) through the shared
   `GatherTransportCandidatesAsync`, over the video socket, so audio and video gather identically
   and a candidate-pairing peer can probe the video 5-tuple.
4. **Emit shared ufrag/pwd + video candidates on offer and answer**; resolve remote creds with a
   media-level override over the session-shared fallback.
5. **Consent loss ceases video transmission** but leaves the socket open (RFC 7675 §5.1).

### Crux

The founding constraint is **SIP-video first, no BUNDLE** (memory
`project_video_interop_codec_decision`): audio and video keep distinct 5-tuples but share one ICE
credential set. That shapes everything here — `IceMediaParameters.FromVideo` reuses the audio
attachment with the shared creds and the video socket, and the candidate gatherer is extracted so
one code path serves both sockets. The alternative (per-m-line credentials or a shared BUNDLE
socket) is explicitly the later ADR-010/011 track, not this path.

## Consequences

Positive: video reuses the whole audio ICE stack (state machine, consent, gathering) with a thin
projection; audio is provably unchanged; NAT paths for video are covered by srflx/relay.

Divergence / honesty:
- **No full ICE candidate-pair selection on the video 5-tuple.** Consent uses the m-line 5-tuple
  (lite-consent style); candidates are gathered/emitted/answered but **not actively paired**
  (video-ice-srflx-relay log caveat). Trickle-ICE for video is also open. **No DONE/compliant
  claim.**
- Interop for the media-layer step is SDK↔SDK (5-tuple derived from the m-line address/port);
  strict candidate-pairing WebRTC peers were addressed by the later host/srflx/relay slices, but
  full pairing remains open.
- Video `OnMediaConsentLost → StopTransmission` mirrors audio but is not covered by a real
  consent-timeout test (would be flaky) — noted follow-up.
- `_keyFrameFeedback` not disposed — pre-existing, not introduced here (review follow-up).

## Guardrails

- Audio ICE stays a 1:1 projection through `IceMediaParameters.FromCall` (regression net).
- Non-ICE video leg is inactive (`IsActive` false) — no consent/responder, fail-closed.
- Enricher order Ice → Srtp → Dtls; the SRTP rebuild must carry the video ICE fields through.
- Candidate gathering stays in the application layer; the channel only forwards the video socket.
- Video candidates emitted only when the list is non-empty (no regression for plain/SDES/DTLS).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-video-ice-media-layer.md`,
  `…-video-ice-candidates.md`, `…-video-ice-srflx-relay.md`.
- Code (graphify-verified): `src/Core/Infrastructure/Rtp/VideoRtpStream.cs` (ICE attachment
  L256, `OnMediaConsentLost` L265); `src/Core/Infrastructure/Stun/Ice/IceMediaAttachment.cs`
  (`IceMediaParameters.FromVideo`); `CallIceAgent.GatherTransportCandidatesAsync`,
  `CallIceLocalDescription.VideoCandidates`;
  `src/Core/Infrastructure/Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs` (video ICE emission
  L554-559); `src/Core/Infrastructure/Sdp/SdpUtilities.cs` (`TryResolveVideoParameters` L521);
  `SipCoreCallChannel` (`_localVideoSocket` passthrough); `CallMediaParametersIceEnricher.EnrichVideo`.
- Related: C11 (audio ICE state machine / consent), ADR-010/011 (BUNDLE, single 5-tuple).
- RFC: 8445 §7.3 (inbound checks), 7675 §5.1 (consent), 8839 (ICE in SDP), 8843 (BUNDLE — the
  path *not* taken here).
