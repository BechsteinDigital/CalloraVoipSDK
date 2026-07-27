# ADR-020: Dialog Route-Set — Record-Route Echo and In-Dialog Routing

Status: Accepted
Date: 2026-07-09

## Context

Behind NAT, ACK-to-200-OK and BYE never reached the SDK — pcap-confirmed. Root cause: the
SDK's 200 OK carried **no Record-Route**, although the INVITE carried three
(217.10.68.150 / 172.20.40.6 / 217.10.68.137). Violation of RFC 3261 §12.1.1. Consequence: the
far-end's route-set was empty, so ACK/BYE were sent **directly** to the SDK's (NAT-private)
Contact from an origin node the NAT had never exchanged packets with → restrictive NAT dropped
them. INVITE/OPTIONS still arrived because they targeted the existing pinhole binding.

This is one half of a dialog-routing correctness class with three parts: (a) a UAS must echo
Record-Route into responses; (b) a UAC must build its dialog route-set from the response
Record-Route in reverse; (c) in-dialog requests (ACK/BYE) must carry a `Route` header built
from that route-set.

### Verified current state (graphify + logs)

- **Inbound echo (a).** `SipCallSessionHeaderService.CreateResponseHeadersFromRequest`
  (`src/Core/Infrastructure/Sip/Signaling/Dialogs/SipCallSessionHeaderService.cs`) copies the
  request's Record-Route rows order-preserving into the response
  (`HeaderValues("Record-Route")` → `CombineHeaderRows` → newline-joined → wire serializer emits
  separate lines); no Record-Route → no header. `Record-Route` is comma-combinable per
  `SipHeaderRowRules.CommaCombinableHeaderNames` (RFC 3261 §7.3).
- **UAC route-set build (b).** `SipCallSessionTransactionUtilities.ParseRouteSetFromRecordRoute`
  builds the UAC dialog route-set from the response Record-Route in **reverse** order
  (RFC 3261 §12.1.2), stripping `<>` while preserving URI params (e.g. `;lr`); null/blank → empty.
- **Outbound Route header (c).** `SipCallSessionHeaderService.CreateDialogRequestHeaders` emits
  `Route: <proxy1;lr>, <proxy2;lr>` in route-set order for in-dialog requests (method-agnostic —
  ACK and BYE alike); empty route-set → no `Route` header.

## Decision

Implement the full RFC 3261 dialog route-set path so in-dialog requests stay on the proxy chain
instead of going straight to a NAT-private Contact:

1. **Echo Record-Route** from the INVITE into the 200 OK, order-preserving (§12.1.1).
2. **Build the UAC route-set** from the response Record-Route, reversed, `<>`-stripped, params
   preserved (§12.1.2).
3. **Stamp the `Route` header** on outbound ACK/BYE from the route-set, in order (§12.2.1.1);
   empty set → no header.

Crux: the NAT routing failure was a missing-header bug, not a transport bug. Echo + reverse +
stamp closes the loop; the far-end then routes ACK/BYE over the proxy chain (last hop
217.10.68.150) and the retransmission storm stops.

## Consequences

Positive: RFC-correct dialog routing; ACK/BYE traverse the proxy chain and reach the SDK behind
NAT; the 200-OK retransmit storm stops on the first ACK. The three pieces are individually
test-locked (inbound echo + reversed route-set + outbound Route header), making the whole chain
regression-safe.

Tradeoffs / honest scope: **loose-/strict-router request-URI rewriting is deliberately out of
scope** — only the `Route` *header* is built, not the strict-router target-URI swap (`;lr`
absent). The media-timeout fallback stays as a safety net for BYE loss. End-to-end proxy
traversal against a live registrar was a founder real-test, not a unit claim.

## Guardrails

- A UAS MUST echo the INVITE's Record-Route rows into the 200 OK, order-preserving; none → none.
- The UAC route-set MUST be the response Record-Route **reversed** (§12.1.2), `<>`-stripped,
  URI params preserved.
- In-dialog ACK/BYE MUST carry the `Route` header from the route-set; empty set → no header
  (never an empty `Route`).

## Sources

- Logs: docs/archive/agent-log/2026-07-08-dev-sip-record-route-response.md;
  docs/archive/agent-log/2026-07-09-dev-b8-uac-route-set-tests.md;
  docs/archive/agent-log/2026-07-09-dev-b8-dialog-route-header-tests.md
- Code: `SipCallSessionHeaderService` (CreateResponseHeadersFromRequest / CreateDialogRequestHeaders,
  src/Core/Infrastructure/Sip/Signaling/Dialogs/SipCallSessionHeaderService.cs);
  `SipCallSessionTransactionUtilities.ParseRouteSetFromRecordRoute`;
  `SipHeaderRowRules` (src/Core/Infrastructure/Sip/Wire/SipHeaderRowRules.cs)
- Tests: SipRecordRouteResponseTests; SipUacRouteSetTests; SipDialogRouteHeaderTests
- Marker: RFC 3261 §12.1.1, §12.1.2, §12.2.1.1, §7.3; B.7 / B.8 slices 2 + 3
