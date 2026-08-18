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

### 3. Whoever signals the restart rotates first

RFC 8445 §9.1.1.1 requires new credentials on both sides. Which side rotates when follows from who is speaking:

- **Answering a restart offer:** rotate *before* negotiating the answer, because the answer has to carry the new
  credentials — the peer authenticates against what our answer says.
- **Initiating one** (`CreateIceRestartOfferAsync`, the W3C `createOffer({iceRestart: true})`): rotate *before*
  producing the offer, and restart the agent at the same moment. Deferring the agent restart until the answer
  arrives would be a bug, not an optimisation: the peer starts checking against the credentials our offer
  advertises as soon as it processes that offer — a signalling round trip earlier — and an agent still holding
  the old password would reject those checks (§7.3.1.1). A peer that marks pairs failed on a 401 would abandon
  the restart we asked for. Restarting with (new local, current remote) is a fully working state meanwhile: the
  peer's new-credential checks authenticate, and our outbound checks still carry the password its pre-restart
  agent expects. When the answer then rotates the remote half, the ordinary detection path restarts once more to
  adopt it.
- **Applying a re-answer that rotates:** adopt the peer's new credentials; ours stay as our re-offer advertised
  them.

Rotation and the description that announces it are produced together in one call, so an application cannot end
up having rotated without sending the offer that tells the peer — which would leave the peer checking against
credentials nobody honours.

### 4. Re-gathering runs over the live transport, and stops where the socket guarantee starts

Every reference stack re-gathers on a restart — libwebrtc restarts gathering on the next `MaybeStartGathering`,
SipSorcery calls `StartGathering()`, Pion requires the caller to call `GatherCandidates`. Not doing so would
leave a restarted peer offering the very candidates the network change invalidated.

The obstacle was that gathering used to need the socket to itself: a probe ran its own receive loop, which the
transport's loop owns once the peer has started, so gathering after `StartAsync` was refused outright. The read
side is now inverted instead — the Binding request goes out through the transport's raw send and the response
comes back through the same inbound STUN demux that feeds the ICE agent (`IceReflexiveProbe`). Both match by
transaction id (RFC 5389 §6), so neither sees the other's traffic as anything but noise: the agent's consent
registry ignores a probe response exactly as the probe ignores a connectivity check.

Two things are deliberately **not** re-gathered:

- **The TURN relay candidate.** The allocation is keyed to the 5-tuple the transport still holds and its refresh
  loop keeps it alive, so the relay candidate did not change; re-allocating would only duplicate it.
- **The socket itself.** Re-binding is what would produce genuinely new host candidates if the local host moved
  networks — and it is exactly what this ADR refuses, because the DTLS association and every SRTP context are
  keyed to that socket surviving. A local host that changes networks needs a new peer, not an ICE restart. Host
  candidates are re-emitted, which covers an interface coming up under a wildcard bind (it shares the port).

### 5. The role is carried over

Re-running the checks does not redetermine which agent controls them. A role switch would need a fresh role
negotiation neither side asked for. This matches the SIP path, which preserves `_iceControlling` across its
restart for the same reason.

### 6. The restart is a state transition, not a failure

The peer moves back to `Connecting`. A network change that killed consent has usually already dropped it to
`Failed`; the restart is what makes that recoverable, so the transition must be allowed to run backwards out of
`Failed`.

## Consequences

**A running peer survives a network change.** The socket, the DTLS association and every per-SSRC SRTP context
are untouched, so media resumes on the re-selected path without a second handshake and without the stream index
spaces restarting under a live key.

**Neither continuing media nor a rotated ufrag is evidence that the restart worked.** This is worth stating
because it shaped the tests, and the trap appears twice.

Once a pair is latched and SRTP is keyed, RTP keeps flowing whatever ICE does — verified by mutation: a build
that rotates the credentials but never swaps the agent still passes a media-only assertion. One layer up, the
ufrag in a locally produced restart offer is read from the renegotiator's credential state, so the same
never-restarts build still emits a fresh-looking offer while the live socket answers with the old password —
precisely the failure that would strand the far side.

The evidence in both directions is therefore taken on the wire, against a real connected peer: after a restart
the peer answers connectivity checks authenticated with the credentials now in force and no longer answers the
retired ones. Those are the assertions that die under the mutations; the SDP-level ones survive them, which is
why they are not left to carry the claim alone.

**The public surface grew by one member**, `IPeerConnection.CreateIceRestartOfferAsync`. It is a *defaulted*
interface member that throws `NotSupportedException`, so adding it does not break an existing implementation of
the interface (a consumer's test double) — the same additive pattern used elsewhere in this SDK when an interface
gains a capability. The API-surface baseline records it.

**Interop is proven against a real browser, not only against ourselves.** A loopback test can only confirm that
we agree with our own reading of the RFC. The browser-interop suite now drives a restart against Chromium and
asserts that *the browser* rotates its own ice-ufrag in the answer — which per §9.1.1.1 it does only once it has
understood the offer as an ICE restart. That is a foreign implementation confirming our SDP, which is the one
thing loopback cannot establish.

**Three collaborators were extracted to make room.** `BundledMediaSession` and `WebRtcPeerConnection` were both
at the 1000-line limit. The relay data path moved out of the session (`BundledRelayDataPath`); the
connection-state machine (`WebRtcConnectionStateMachine`) and the media-socket ownership across its one hand-over
to the transport (`WebRtcMediaSocketOwner`) moved out of the peer. Both peer collaborators share the peer's lock,
so their serialisation is unchanged. `WebRtcPeerConnection` is back at exactly 1000 lines and is now essentially
all public API plus documentation — the next change to it needs a structural split, not another small extraction.

## How the reference stacks compare

Checked against the sources, not the documentation, per the parity rule for this SDK.

| | Agent object | Media during the restart | Re-gathers | Rotates local credentials |
|---|---|---|---|---|
| **libwebrtc** | reused (`P2PTransportChannel`) | keeps flowing | yes | yes |
| **Pion** | reused, fully reset | stops | yes (caller) | yes |
| **SipSorcery** | reused, fully reset | stops | yes | **no** |
| **this SDK** | replaced | keeps flowing | yes (srflx, over the live transport) | yes |

- **libwebrtc** keeps media alive by *generation-tagging*: `SetRemoteIceParameters` pushes onto a vector —
  "Keep the ICE credentials so that newer connections are prioritized over the older ones" — and stamps every
  existing `Connection` with the new generation, so old pairs stay valid while newer ones sort ahead. A different
  mechanism from ours, the same RFC 8445 §9 outcome.
- **Pion**'s `Agent.Restart` calls `setSelectedPair(nil)` and `deleteAllCandidates()`, so the selected pair and
  its sockets are gone; §9's "continue to use the previously selected pair" is not met.
- **SipSorcery** cannot signal a conforming restart at all: `RtpIceChannel.LocalIceUser` / `LocalIcePassword` are
  `readonly`, set once in the constructor, so `restartIce()` re-gathers without rotating anything. A re-offer then
  carries the same ufrag/pwd, which per §9.1.1.1 is not a restart, and the peer will not restart. Its
  `RTCOfferOptions` has no `iceRestart` field either, and inbound `SetRemoteCredentials` overwrites the remote
  values without resetting the check list.

## References

- RFC 8445 §9 (ICE restarts), §9.1.1.1 (new credentials on both sides), §14.1 (retransmission)
- RFC 8829 §5.3.1 (JSEP: signalling an ICE restart)
- RFC 8839 §5.4 (SDP: `a=ice-ufrag` / `a=ice-pwd` rotation)
- RFC 8842 (a restart is not a DTLS re-keying)
- RFC 7675 (consent freshness — the loop the replaced agent owns)
- [ADR-054](ADR-054-turn-relay-as-ice-candidate.md) — the relay local candidate a restart must carry over
- #226 (this change), #62 (the SIP-path restart)
