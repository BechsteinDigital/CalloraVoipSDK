# CalloraVoipSdk 4.12.0

**Simulcast now works when the SDK answers, and inbound calls surface their full retargeting history whichever
header the carrier used.** 4.11 could negotiate simulcast only as the *offerer*; 4.12.0 closes the answerer
half — the common SFU topology where the client offers and the server answers. Separately, an inbound call now
exposes every address it was forwarded from, read from `History-Info` or `Diversion` alike. Purely additive —
one new public member, nothing removed or changed (verified against `PublicApi.approved.txt`).

## Highlights

### Simulcast as the answerer (#369, RFC 8853 §5.3)

Until now a simulcast offer the SDK *received* was never confirmed: the answer carried neither `a=rid` nor
`a=simulcast`, so a spec-compliant peer sent a single stream. 4.12.0 mirrors the offered simulcast into the
answer:

- an offered `a=simulcast:send` — **the common SFU topology**, client offers and server answers — is confirmed
  with `a=simulcast:recv`, and the received layers arrive tagged with their RID
  (`IPeerConnection.NegotiatedReceiveSimulcastRids`, per-layer received frames);
- an offered `a=simulcast:recv` is answered with `a=simulcast:send` for the layers the app is configured to
  produce.

Only the intersection is confirmed (RFC 8853 §5.1), and only when the offer carried the RID header extension
(RFC 8852) — without a per-packet label the layers cannot be demultiplexed. It is driven entirely through the
existing `SimulcastLayers` / `SimulcastRecvLayers` configuration and the existing per-layer events, so there is
**no public API change**. A full SDK↔SDK peer-to-peer media loopback proves both directions end to end over
DTLS-SRTP — the receive path that was previously reachable only against a real browser.

Behaviour note: a single configured RID is **not** simulcast — a lone `a=rid` is dropped (RFC 8853; Chrome
strips it and never enters simulcast). A one-layer `SimulcastLayers` / `SimulcastRecvLayers` now falls back to
a single stream instead of emitting a degenerate `a=simulcast`, and **logs a warning at configuration time**
rather than degrading silently. Configure two or more distinct RIDs, or none. Multi-layer simulcast and
non-simulcast sessions are unaffected.

### The full retargeting history of an inbound call (#374)

`ICall.DiversionChain` lists every address a forwarded call was diverted from, oldest first — the number the
caller originally dialled at the front, the party that forwarded it to us at the back. It reads **both**
`History-Info` (RFC 4244) and `Diversion` (RFC 5806), which answer the same question but are ordered opposite
ways; both are normalised to one order, and the entry naming us is dropped (RFC 3261 §19.1.4 URI comparison).

Until now only the first row of `Diversion` was read. No carrier sends both headers consistently, so a
consumer was correct with part of the market and silently blind with the rest — and blind here means a
forwarded call is indistinguishable from a direct one, exactly the distinction a PBX routes on. `ICall.Diversion`
is unchanged for consumers that want precisely the first URI of the first `Diversion` row. Purely additive —
one line in `PublicApi.approved.txt`.

## Compatibility

Purely additive. One new public member (`ICall.DiversionChain`); nothing removed or changed, verified against
`PublicApi.approved.txt`. Non-simulcast and single-stream sessions are byte-identical to 4.11.0. SemVer:
**MINOR**.

See [`CHANGELOG.md`](CHANGELOG.md) for the itemised list.
