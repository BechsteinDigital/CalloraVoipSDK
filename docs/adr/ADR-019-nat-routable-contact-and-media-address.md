# ADR-019: NAT-Routable Contact and Advertised Media Address

Status: Accepted
Date: 2026-07-08

## Context

Behind NAT against a public registrar/trunk (sipgate, Fritz!Box), registration succeeded
(401→auth→200) and OPTIONS keepalives ran, yet the trunk showed "offline" and inbound INVITEs
never arrived — and when they did, ACK/BYE and media failed. Root cause across the failure
family: the SDK advertised **private / loopback** addresses in the places a remote peer must
route back to.

- REGISTER Contact carried the route-probed private LAN IP → the registrar bound the AOR to an
  unroutable address → no inbound.
- The in-dialog Contact in the 200 OK carried the private IP → the caller's ACK could not
  route → 200-OK retransmission storm.
- The SDP `c=` line carried the private (or `127.0.0.1`) address → the far-end SBC sent media
  into the void → recv=0.

The registrar already reflects the caller's public address in the 200 OK Via
(`received=`/`rport=`, RFC 3581 §4) — the SDK just wasn't using it. And a manual public host is
awkward with dynamic IPs.

### Verified current state (graphify + logs)

- `NatPublicContactState` (`src/Core/Infrastructure/Sip/Adapters/NatPublicContactState.cs`, L12)
  holds a single learned public value; `.ApplyObserved(...)` is the pure change-detection
  decision function.
- `AdvertisedMediaAddressResolver` (`src/Core/Infrastructure/Sip/Adapters/AdvertisedMediaAddressResolver.cs`,
  L18) exposes `.Resolve()` + `.ProbeRoute()`: a concrete non-loopback bind address is
  authoritative; a loopback bind toward a non-loopback peer is **not** advertised — it
  route-probes against `RemoteSignalingEndPoint` (IP directly, no DNS), falls back to a
  URI-host probe, and only logs a warning before ever advertising loopback. The probe is
  injectable for deterministic tests.
- Precedence for the SIP contact: **manual `PublicSipHost` > learned (rport/received) > local**.
  Learned value is recomputed on *every* 200 OK; a *change* triggers exactly one immediate
  re-REGISTER (idempotent — a second identical 200 OK is a no-op, self-healing on IP change).
- Media split: the SDP `c=` line advertises the public address
  (`ResolveAdvertisedSdpAddress = publicMediaAddress ?? local`), while the RTP/RTCP socket
  **still binds locally** (a public IP is not a bindable interface); the far-end SBC latches via
  symmetric RTP onto the real source port.

## Decision

Advertise a routable identity everywhere a peer routes back, while keeping the bind local:

1. **Learn, don't just configure.** Parse `received=`/`rport=` from the 200 OK top Via
   (`SipProtocol.ExtractViaReceivedRport`, RFC 3261 §18.2.1 / RFC 3581 §4); hold one learned
   value; re-REGISTER exactly once on change. A manual `PublicSipHost`/`PublicSipPort` override
   always wins (DynDNS/FQDN host string supported).
2. **One source for the contact.** REGISTER Contact, in-dialog Contact (200 OK), and Via are all
   built from the same resolved value, so ACK/BYE route over the pinhole and the retransmission
   storm stops.
3. **Advertise ≠ bind for media.** The SDP `c=` line carries the public/route-probed address;
   RTP/RTCP bind locally and rely on symmetric-RTP latching. Never advertise a loopback address
   toward a non-loopback peer — route-probe instead.

Crux: single-state + change-detection (not "one-shot correction + flag + counter") makes the
learning idempotent and self-terminating; and "advertised" and "bound" are deliberately
different addresses.

## Consequences

Positive: zero-config NAT traversal for the common case (learn from rport); stable AOR binding;
ACK/BYE routable; media reaches the far end via latching; manual override for DynDNS / explicit
public IP. No flag, no counter, converges on its own.

Tradeoffs / honest limits: media path B assumes the far-end SBC does symmetric-RTP latching —
if the port does not latch, RTP-socket STUN for the public media port is required follow-up (and
sending outbound RTP first to open the pinhole helps). **CGNAT / symmetric NAT is explicitly not
covered** — the rport address may still be unreachable, needing a real public IP / port-forward /
TURN. Real-world confirmation (trunk online + inbound call + audio) stayed a founder real-test in
the logs; the ADR records the mechanism, not an end-to-end "NAT solved" claim.

Relationship to ADR-003: ADR-003 is the **UAS reflecting** rport/received into responses it
sends; this ADR is the **UAC learning** its own public contact from the rport/received a
registrar reflected back. Complementary, not overlapping.

## Guardrails

- Contact precedence is fixed: manual override > learned > local; one contact source only.
- Re-REGISTER fires only on an actual change of the learned value; identical observation = no-op.
- Never advertise a loopback address toward a non-loopback peer; route-probe or warn.
- Advertised media address and bound socket address are decoupled by design.

## Sources

- Logs: docs/archive/agent-log/2026-07-08-dev-sip-public-contact.md;
  docs/archive/agent-log/2026-07-08-dev-sip-rport-contact.md;
  docs/archive/agent-log/2026-07-08-dev-sip-media-nat.md;
  docs/archive/agent-log/2026-07-08-dev-m1-hotfix.md (advertised-media-address root cause)
- Code: `NatPublicContactState` (src/Core/Infrastructure/Sip/Adapters/NatPublicContactState.cs);
  `AdvertisedMediaAddressResolver` (src/Core/Infrastructure/Sip/Adapters/AdvertisedMediaAddressResolver.cs);
  `SipProtocol.ExtractViaReceivedRport`; `SipLineChannel`; `SipCoreCallChannel.ResolveAdvertisedSdpAddress`
- Tests: SipPublicContactTests; SipInDialogPublicContactTests; AdvertisedMediaAddressResolver tests
- Marker: RFC 3261 §18.2.1, §10.3; RFC 3581 §4; N1 / N2 / Media-NAT
