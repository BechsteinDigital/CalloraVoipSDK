# ADR-056: Post-Nomination Whole-Socket Relay Transition (ChannelBind / ChannelData)

Status: Accepted
Date: 2026-07-19

## Context

Once a relay ICE pair is nominated (ADR-C16-01), media must actually flow through the relay. Two
operating phases exist for a relay allocation on the shared 5-tuple (ADR-011):

- **Check phase.** During connectivity checks the relay candidate sends each check as a per-pair TURN
  Send indication (RFC 8656 §10), because a relay candidate may probe several remote candidates and
  none is yet the bound peer. This is what ADR-C16-01's send path does — cheap to set up, but every
  datagram carries a full Send/Data-indication STUN wrapper. This was the direction historically
  called "Fork B".
- **Post-nomination steady state.** Once a single peer is nominated, RFC 8656 offers ChannelBind
  (§11) + ChannelData (§12): a 4-byte channel header instead of a full indication wrapper, for *all*
  media on that peer. This is the RFC-complete steady state — historically "Fork A".

The fork was a real decision, not a shortcut: run steady-state media as compact ChannelData (§11–12),
or keep everything as Send/Data indications. **The User decided (2026-07-19): Fork A — "nach den RFC,
vollständig und korrekt."** So the transport must transition, at runtime, from direct mode into a
whole-socket relay mode where every datagram (STUN, DTLS, RTP, RTCP) is framed as ChannelData to the
TURN server and every inbound datagram is unwrapped from it — below the packet demux, so the DTLS/ICE
layers above compose unchanged. This is a change to the shared, security-critical media socket, so it
was built as transport primitive first, then orchestration, then a correctness fix.

### Verified current state

- `BundledMediaTransport` (`src/Core/Infrastructure/Rtp/BundledMediaTransport.cs`) runs two relay
  phases on one 5-tuple. Its own class doc: the *control phase* (a `RelayServer` is set) sends TURN
  requests via `SendControlAsync` and surfaces control responses via `OnRelayControl`, so the
  allocation can be established before any channel exists (outbound media suppressed meanwhile); once
  ChannelBind completes, `SetRelayChannel` installs the `IRelayDatagramChannel` and the *data phase*
  begins — every outbound datagram framed as ChannelData, every inbound one unwrapped.
- `EnterRelayMode(relayServer, onControl)` (L158) transitions a *direct-mode* transport into
  whole-socket relay mode at runtime (RFC 8656 §11–12): it rejects an already-relay transport and a
  transport with no remote endpoint (the bound peer relayed inbound is attributed to). The mode
  discriminator `_relayServer` and control sink `_onRelayControl` are `readonly → mutable`; the sink
  is published (`Volatile.Write`) **before** the discriminator, so a reader observing relay mode also
  sees the sink. All 6 runtime reads of `_relayServer` are `Volatile.Read` (the receive loop reads it
  concurrently with the nomination thread that flips it). The per-pair indication path
  (`SetIndicationRelay`, §10) goes dormant after the flip and is mutually exclusive with relay mode
  (each throws if the other is active).
- `SetRelayChannel` (L133) is valid only in relay mode and only for a channel bound to that same
  server; `SendAsync` (L224) frames via `relay.Wrap` to the relay server when a channel is installed.
- Ordering: ChannelBind runs **before** `EnterRelayMode`, in direct mode, so the request reaches the
  server unframed. After the flip, `SendToAsync` would frame control as ChannelData (broken), so a
  distinct `SendUnframedAsync` sends raw to the server in both modes (identical to `SendToAsync` in
  direct mode) and the channel-rebind loop uses it to keep re-binding even after the data path flips.
- Orchestration (`BundledMediaSession`): `IceMediaAttachment`/`BundledIceControl` fire an optional
  `onRelayPairNominated` callback *only* for a relay pair; the session runs a once-guarded
  `TransitionToRelayAsync` = ChannelBind → `EnterRelayMode` → `SetRelayChannel`, cancelled and awaited
  **before** the transport is disposed. `OnPairNominated` (sets remote/DTLS) runs synchronously before
  `onRelayPairNominated`, so the `EnterRelayMode` precondition (`_remoteEndPoint` set) holds.
- Correctness fix (4d-4c-iii): a `_relayTransitioned`-succeeded flag commits the transport to relay
  after `SetRelayChannel`; `OnPairNominated` becomes a no-op afterwards, and the transition re-asserts
  `SetRemoteEndPoint(peer)` before `EnterRelayMode`. Finding: after the flip, direct checks misroute
  anyway, so a relay→direct re-nomination is only possible in a sub-second window, which the flag closes.

## Decision

Transition the shared BUNDLE transport at runtime from direct mode into **whole-socket relay mode**
(RFC 8656 §11–12) when a relay ICE pair is nominated, so all steady-state media flows as ChannelData —
the RFC-complete Fork A, per the User's decision.

1. **Two phases on one socket, discriminated by a volatile mode field.** Direct mode uses the socket
   (and, for relay checks, the dormant-until-flip per-pair indication path); relay mode frames every
   datagram as ChannelData to the TURN server and unwraps every inbound one, below the packet demux, so
   DTLS/ICE/RTP above are unchanged. The two paths (per-pair indication vs. whole-socket relay) are
   mutually exclusive by construction.
2. **Nominate → ChannelBind → EnterRelayMode → SetRelayChannel, in that order.** ChannelBind runs in
   direct mode (request reaches the server unframed). `EnterRelayMode` then suppresses sends until
   `SetRelayChannel` installs the bound channel, opening the data path. The session drives this once,
   guarded, only for a relay pair.
3. **Control after the flip goes raw via `SendUnframedAsync`.** Channel rebind, refresh, and any other
   control that must reach the *server* (not the peer) send raw in both modes, so they are not
   double-framed as ChannelData after the transition.
4. **Commit to relay once bound.** After `SetRelayChannel` succeeds, a `_relayTransitioned` flag makes
   `OnPairNominated` a no-op, so a late relay→direct re-nomination cannot re-point the unwrap source and
   silently drop inbound media. The transition re-asserts the peer as the remote endpoint before flipping.
5. **Concurrency by publication order and volatile reads.** The nomination thread flips the mode; the
   receive loop and send paths read it. Publish the control sink before the discriminator; read all mode
   state via `Volatile`. Cancel and await the transition before disposing the transport.

### Crux

The transition is a mutation of the *shared, security-critical* media socket while a receive loop and
send threads run against it. Correctness rests on three things: (a) ChannelBind must happen while still
direct (or the bind request itself gets suppressed/mis-framed); (b) control-to-server must bypass
ChannelData framing (`SendUnframedAsync`) or the rebind/refresh loops break the moment the data path
flips; (c) once committed to relay, nomination must not re-point the unwrap source — hence the
succeeded-flag, not merely a started-flag (a failed transition must not lock the transport).

## Consequences

Positive: steady-state relayed media uses the compact ChannelData framing (RFC-complete), not a full
indication wrapper per packet. The transition is additive to the direct path — with no
`EnterRelayMode` call the transport is byte-identical to before (verified: full suite green including
direct mode, ctor-relay mode, SIP media, and session paths). DTLS/ICE/RTP layers above are unaware of
the mode.

Divergence and honest open edges:

- **The full session-level transition is not exercised end-to-end in a fast test.** Forcing a real
  relay pair to be *nominated* over a real session socket is timeout-bound (direct exhaustion ~10 s+),
  so the whole ChannelData round-trip through a session is not in a unit test. The transport primitives
  (`EnterRelayMode` + `SetRelayChannel` over a real `TurnRelayChannel`, control-response surfacing) and
  the orchestration wiring (relay-pair-nominated callback firing, dispose ordering) are tested
  individually; an orchestration end-to-end uses an IPv6-dead direct pair to reach `SetRelayChannel`
  quickly. No real-server end-to-end and no browser interop (ADR-009) — no production-ready claim.
- **Relay→direct re-nomination after commit is closed, not gracefully re-transitioned.** The transport
  commits to relay; it does not transition *back* to direct. In practice direct checks misroute after
  the flip so this window is sub-second, but a formal relay→direct downgrade is not supported.
- **Transition happens after check-phase indications, not per-pair during checks.** This is the
  intended split (Fork B for checks, Fork A for steady state), but it means there is a brief moment
  where sends are suppressed (`_relayServer` set, `_relay` still null) between `EnterRelayMode` and
  `SetRelayChannel`.
- **Controlled-agent relay gap carries over** (ADR-C16-01): only the controlling agent nominates a
  relay pair, so only it triggers the transition. An Answerer-owned relay against a direct Offerer does
  not transition.
- **TCP/TLS relay data path out of scope.** This is UDP whole-socket relay; a stream relay transport is
  a separate, unbuilt feature.

## Guardrails

- Order is load-bearing and must not be reordered: **ChannelBind (direct) → EnterRelayMode →
  SetRelayChannel.** ChannelBind after `EnterRelayMode` (with no channel yet) would be suppressed.
- Control destined for the TURN *server* (rebind, refresh) must use `SendUnframedAsync`, never the
  ChannelData-framing send, so it is not double-wrapped after the flip.
- Every mode field (`_relayServer`, `_relay`, `_onRelayControl`) is read via `Volatile`; publish the
  control sink before the mode discriminator. `EnterRelayMode` is at most once and only on a
  direct-mode transport with a remote endpoint set.
- Commit with a *succeeded* flag (after `SetRelayChannel`), not a started flag, so a failed transition
  does not lock the transport; once committed, `OnPairNominated` is a no-op.
- The whole-socket relay path and the per-pair indication path are mutually exclusive — never active
  together on one transport.
- Cancel and await the transition before disposing the transport; the transport must outlive the
  channel-rebind/keepalive teardown that rides its send.

## Sources

- Logs (`docs/archive/agent-log/`):
  - `2026-07-19-dev-relay-4c-4b.md` — records the Fork A vs. Fork B block awaiting the User decision.
  - `2026-07-19-dev-relay-4d-4c-i.md` — **Fork A decision** ("nach den RFC, vollständig und korrekt");
    `EnterRelayMode` primitive, `_relayServer` readonly→mutable, 6 volatile sites, publication order,
    dormant indication path, suppress window `_relayServer!=null && _relay==null`.
  - `2026-07-19-dev-relay-4d-4c-ii.md` — orchestration: ChannelBind-before-EnterRelayMode ordering,
    `SendUnframedAsync` for post-transition control, `onRelayPairNominated` (relay-only), session
    `TransitionToRelayAsync`, dispose ordering; the Low-1 re-nomination finding raised here.
  - (Low-1 fix `4d-4c-iii`, main `035c1f3`) — `_relayTransitioned` succeeded-flag; `OnPairNominated`
    no-op after commit; re-assert `SetRemoteEndPoint` before flip; orchestration E2E via IPv6-dead direct.
- Code:
  - `src/Core/Infrastructure/Rtp/BundledMediaTransport.cs` (`EnterRelayMode` L158, `SetRelayChannel`
    L133, `SetIndicationRelay` L187, `SendAsync` L224, `SendUnframedAsync`)
  - `src/Core/Infrastructure/Rtp/BundledMediaSession.cs` (`TransitionToRelayAsync`, `OnRelayPairNominated`)
  - `src/Core/Infrastructure/Rtp/BundledIceControl.cs`, `src/Core/Infrastructure/Stun/Ice/IceMediaAttachment.cs`
  - `src/Core/Infrastructure/Common/Relay/RelayIceBinding.cs` (`BindChannel`), `IRelayDatagramChannel.cs`
  - `src/Core/Infrastructure/Turn/Client/TurnRelayChannel.cs` (`Wrap`/`TryUnwrap`, channel `0x4000`)
- Markers / RFC: RFC 8656 §10 (Send/Data indications — check phase), §11 (ChannelBind), §12
  (ChannelData — steady state). Adjacent: ADR-C16-01 (relay as ICE candidate), ADR-C16-02 (control
  stack / keepalive), ADR-011 (shared BUNDLE transport), ADR-009 (interop boundary).
