# ADR-072: An ICE Restart Replaces the Agent, Not the Transport

Status: Accepted
Date: 2026-08-18

## Context

Until now a re-offer that rotated the peer's ICE credentials on the transport-anchoring m-line was rejected:

> ICE restart is not supported on a running WebRTC peer. Dispose this peer and create a new one to restart ICE.

The SIP path has had a restart since #62 (`ICall.RestartIceAsync`). The WebRTC peer did not, and the case it
fails on is not exotic. A browser participant moving from WLAN to mobile — the ordinary situation on a phone —
signals exactly this re-offer. Rejecting it drops the call. In a video consultation that is a visible break in
the middle of a treatment. An application-layer resume covers getting back in; it does not cover the experience.

The question this ADR settles is **how far down the stack a restart reaches**. Three layers could plausibly be
rebuilt, and the reference stacks agree on the answer:

| Layer | Rebuilt on restart? | Why |
|---|---|---|
| ICE agent (credentials, check list, consent) | **Yes** | The credentials rotated; every piece of ICE state is keyed to them |
| Socket / 5-tuple | No | The local candidate did not change; re-binding would invalidate the peer's check list |
| DTLS association, SRTP contexts | No | RFC 8842: a restart is not a re-keying. Re-handshaking would restart every stream's index space |
| Tracks, SSRCs | No | Orthogonal — the track set is the renegotiation diff, which runs independently |

This is also what a browser does: `createOffer({iceRestart: true})` re-gathers and re-runs checks; the
`RTCDtlsTransport` and the media keep going.

## Decision

### 1. The agent is replaced wholesale, not re-keyed

New credentials mean a new inbound validator, a new check list and a new consent session. None can be re-keyed
in place without leaving half-updated state on the media hot path, so `BundledIceControl.RestartIceAsync` builds
a fresh `IceMediaAttachment` and swaps it in.

**The order is the load-bearing part.** The old agent is detached from the inbound STUN feed *before* the new
one attaches, so no datagram is ever offered to both. Two live agents would be a protocol error — one answers
checks with retired credentials — and two live nomination drivers could redirect the transport against each
other. The gap between detach and attach drops inbound checks for the length of a few statements; ICE
retransmits them (RFC 8445 §14.1). That retransmission is the mechanism that makes the swap safe, not an
accident we are relying on. The old agent is disposed last, so the window in which nothing is running is bounded
by a few statements rather than by a disposal.

A relay local candidate added after construction (the answerer's TURN path) is re-applied to the new agent.
Dropping it would silently downgrade a relayed session to direct-only at exactly the moment the network changed
— when the relay is most likely to be the only thing that works.

### 2. Media stays on the previously selected pair

The transport's send target is deliberately **not** re-pointed when the restart is applied. RFC 8445 §9 keeps
media on the previously selected pair until a new one is selected. A peer that rotated its credentials because
it changed networks keeps receiving on the old path for as long as that path still works, instead of losing
media the moment the re-offer arrives. The new agent re-points the transport itself when it nominates.

### 3. Only the answerer rotates its own credentials

RFC 8445 §9.1.1.1 requires the answerer to a restart offer to generate new credentials too, and the answer must
carry them — so the rotation happens *before* the answer is negotiated. As the offerer we do not rotate: our
re-offer already advertised our credentials and the peer is checking against those.

### 4. The role is carried over

Re-running the checks does not redetermine which agent controls them. A role switch would need a fresh role
negotiation neither side asked for. This matches the SIP path, which preserves `_iceControlling` across its
restart for the same reason.

### 5. The restart is a state transition, not a failure

The peer moves back to `Connecting`. A network change that killed consent has usually already dropped it to
`Failed`; the restart is what makes that recoverable, so the transition must be allowed to run backwards out of
`Failed`.

## Consequences

**A running peer survives a network change.** The socket, the DTLS association and every per-SSRC SRTP context
are untouched, so media resumes on the re-selected path without a second handshake and without the stream index
spaces restarting under a live key.

**Continuing media is not evidence that the restart worked.** This is worth stating because it shaped the tests.
Once a pair is latched and SRTP is keyed, RTP keeps flowing whatever ICE does — verified by mutation: a build
that rotates the credentials but never swaps the agent still passes a media-only assertion. The evidence that
ICE actually moved is on the wire: after the restart the peer answers connectivity checks authenticated with the
rotated credentials and no longer answers the retired ones. Both are asserted against a real connected peer.

**Local initiation is not covered.** Everything beneath it now exists, but nothing yet rotates our credentials in
a locally created re-offer, so this SDK can adopt a restart the far side signals and cannot signal one itself
(the W3C equivalent is `createOffer({iceRestart: true})`). Tracked as follow-up work.

**Two files were extracted to make room.** `BundledMediaSession` and `WebRtcPeerConnection` were both at the
1000-line limit. The relay data path moved out of the session (`BundledRelayDataPath`) and the connection-state
machine out of the peer (`WebRtcConnectionStateMachine`), the latter sharing the peer's lock so its
serialisation is unchanged.

## References

- RFC 8445 §9 (ICE restarts), §9.1.1.1 (new credentials on both sides), §14.1 (retransmission)
- RFC 8829 §5.3.1 (JSEP: signalling an ICE restart)
- RFC 8839 §5.4 (SDP: `a=ice-ufrag` / `a=ice-pwd` rotation)
- RFC 8842 (a restart is not a DTLS re-keying)
- RFC 7675 (consent freshness — the loop the replaced agent owns)
- [ADR-054](ADR-054-turn-relay-as-ice-candidate.md) — the relay local candidate a restart must carry over
- #226 (this change), #62 (the SIP-path restart)
