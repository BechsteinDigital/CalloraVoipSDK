# ADR-021: Trunk-Inbound Line Matching

Status: Accepted
Date: 2026-07-08

## Context

Once the NAT-public contact was learned and the inbound INVITE finally arrived, it was
*rejected*: "local URI 'sip:00493075435072@sipconnect.sipgate.de' does not match account
username." `SipLineChannel.IsSessionForThisLine` required the INVITE To-user to equal the
registration username. That holds for 1:1 accounts but breaks for trunks, where the dialed DID
is never the trunk credential (DID ≠ trunk username). So a correctly registered trunk could not
accept any inbound call.

### Verified current state (graphify + logs)

- `TrunkInboundMatcher` (`src/Core/Infrastructure/Sip/Adapters/TrunkInboundMatcher.cs`, L18) is a
  pure, testable matcher; `.IsForThisLine()` is the single decision entry point.
- `SipLineChannel.IsSessionForThisLine` delegates to it and resolves trusted registrar addresses
  via `ResolveTrustedRegistrarAddresses` (best-effort DNS for SipServer + OutboundProxy, cached),
  using `session.RemoteSignalingEndPoint` (the INVITE source) for the peer match.
- `SipAccount.InboundNumbers` is an optional public-API DID whitelist.

## Decision

Do not reject trunk inbound on the user-part. Match like established stacks (PJSIP best-match,
FreeSWITCH gateway/ACL):

- **`InboundNumbers` set** → accept only those DIDs on the registered domain (multi-line-safe).
- **Otherwise** → accept on any of: exact username match **OR** peer match (INVITE source ==
  the registered registrar/proxy address) **OR** To-domain == the registered domain (trunk
  default).

Crux: the accept criterion is *peer/domain*, not user-part; the peer match (INVITE came from our
registrar) is the strong criterion, and the DID whitelist is the tightening knob.

## Consequences

Positive: registered trunks accept inbound DIDs; 1:1 username accounts are unchanged;
`InboundNumbers` gives multi-line safety and explicit DID control; peer-match accepts even
off-domain DIDs that genuinely come from our registrar.

Tradeoffs / honest limits: the trunk default "any DID on the domain" has a spoofing trade-off —
`InboundNumbers` is documented as the hardening, and a stricter "registrar-peer only" mode is a
possible follow-up package. DID exposure to the application layer (`CalledNumber` on `ICall`)
was deliberately *not* built in this pass — it needs its own result class (follow-up T1b); it is
not required for mere acceptance. Full call (accept + media + conversation) stayed a founder
real-test.

## Guardrails

- Trunk inbound MUST NOT be rejected on To-user alone.
- When `InboundNumbers` is set, only listed DIDs on the registered domain are accepted.
- Peer match uses the actual INVITE source endpoint against the resolved registrar/proxy
  addresses; an unparseable Request/To URI is rejected.

## Sources

- Logs: docs/archive/agent-log/2026-07-08-dev-sip-trunk-inbound.md
- Code: `TrunkInboundMatcher` (src/Core/Infrastructure/Sip/Adapters/TrunkInboundMatcher.cs);
  `SipLineChannel.IsSessionForThisLine` / `ResolveTrustedRegistrarAddresses`;
  `SipAccount.InboundNumbers`
- Tests: TrunkInboundMatcher tests (7 cases)
- Marker: RFC 3261 §8.2 (UAS request handling); T1 (trunk-inbound)
