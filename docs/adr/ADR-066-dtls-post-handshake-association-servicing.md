# ADR-066: DTLS Post-Handshake Association Servicing and Egress Ordering

Status: Accepted
Date: 2026-08-06

## Context

After a successful DTLS-SRTP handshake the four SRTP/SRTCP contexts are derived and media flows **directly over
SRTP** (RFC 5764 §4.2). Two defects remained in the DTLS keying channel around that point.

1. **Egress was fire-and-forget.** BouncyCastle calls `Send` synchronously from its handshake thread; the bridge
   ran `_ = SendOutboundAsync(datagram)` — so records had no local order, no backpressure, and a transport
   failure was only logged (the handshake did not fail). At teardown `close_notify` was sent fire-and-forget and
   the lifetime token was cancelled immediately after, so it could be dropped — and DTLS does not retransmit
   alerts (RFC 6347 §4.2.7).
2. **The association was never serviced after key export.** Nobody called `Receive` on the `DtlsTransport`
   again, so a remote `close_notify` went unnoticed, alerts changed nothing, and media kept flowing under a
   keying channel the peer considered closed (RFC 8827 §6.5).

The BouncyCastle post-handshake semantics had to be pinned empirically (they are not obvious): a peer
`close_notify` does **not** surface as a clean `Receive` return of `-1`. BouncyCastle processes it, closes the
underlying transport, and then — reading on — throws our now-closed `QueueDatagramTransport` as
`TlsFatalAlert(internal_error)`. A peer *fatal* alert surfaces as `TlsFatalAlertReceived`; application_data
surfaces as a positive length; a plain timeout as `-1` with the transport still open.

Reference stacks diverge here: libwebrtc and aiortc actively service the DTLS channel after keying and map a
peer `close_notify` onto a transport-state/close event; Pion services it only when a DataChannel is negotiated,
and pjsip reacts only to hard SSL faults (a clean `close_notify` is ignored). None sends an active
`no_renegotiation` alert.

## Decision

Service the keying channel and make egress a proper contract.

1. **Ordered single-writer egress (`DtlsEgressPump`).** A bounded queue drained by one consumer awaits the socket
   send in order (local record order, bounded backpressure — no second unbounded queue). The first transport
   fault is captured and re-thrown from the next enqueue, so it reaches BouncyCastle and fails the handshake
   closed instead of being logged. Teardown drains a pending `close_notify` on a tight deadline before cancelling
   the rest.
2. **A control-receive loop (`DtlsAssociationReceiver`)** started after key export, on a single consumer. It
   discards and counts stray DTLS application_data in pure-SRTP mode (RFC 5764: RTP/RTCP stays SRTP/SRTCP), and
   on a peer close/alert notifies the session owner so media ceases. It does **not** notify on our own teardown.
3. **A normalising adapter (`IDtlsControlChannel` / `BouncyCastleDtlsControlChannel`)** maps the mixed
   BouncyCastle signalling onto `{Timeout, ApplicationData, Closed}`, disambiguating a close from a timeout via
   `QueueDatagramTransport.IsClosed`. This keeps the loop testable without a live handshake.
4. **Owner reaction:** the peer-close callback flows `BundledDtlsKeying` → `BundledMediaSession.PeerClosed` →
   `WebRtcSessionEventBridge` → `WebRtcConnectionState.Closed` (a peer `close_notify` is a deliberate teardown of
   the secure channel; this mirrors libwebrtc's `DtlsTransportState::kClosed`).
5. **Renegotiation is rejected passively.** BouncyCastle discards a post-handshake ClientHello automatically (no
   rekey), which is exactly what libwebrtc/BoringSSL and pjsip/aiortc do. No active `no_renegotiation` alert is
   sent — BouncyCastle offers no hook and no reference stack sends one. Live rekeying/MKI stays out of scope.

**Teardown ordering is the critical invariant:** the receiver is stopped *cleanly* (cancellation makes its
bounded receive time out — a clean `-1` that does not fault the record layer) **before** `close_notify` is sent,
because a faulted record layer makes BouncyCastle skip the `close_notify` (`m_failed`). Only then is the
transport closed and the egress drained.

## Consequences

- **Media no longer runs under a closed keying channel** on the WebRTC/bundle path: a peer `close_notify` ends
  the association deterministically and surfaces as `connectionState = "closed"`.
- **Reference parity:** better than Pion/pjsip (which do not service the channel in the keying-only case), on par
  with libwebrtc/aiortc.
- **All new types are internal** — no public API surface added.
- **The SIP media-session owner reaction is a documented follow-up.** SIP legs still service the association
  (close noticed and logged, application_data discarded) but do not yet notify the owner; a SIP leg is torn down
  via BYE, not a mid-call DTLS `close_notify`, so the gap is low-risk.
- **A sub-millisecond late-completion race** (a handshake completing exactly during teardown) may drop the final
  `close_notify` — the same narrow, acknowledged race as the egress teardown; the association is closed either
  way.

## Alternatives considered

- **Send an active `no_renegotiation` alert.** Rejected: BouncyCastle offers no hook (it would require manual
  record inspection and alert framing), it goes beyond every reference stack, and passive rejection already
  prevents a rekey.
- **Map a peer close to `Failed` instead of `Closed`.** Considered: a fatal alert is failure-like, but the
  dominant case is a clean `close_notify` (a deliberate close), for which `Closed` matches libwebrtc `kClosed`.
- **Close the transport before stopping the receiver.** Rejected: it faults BouncyCastle's record layer, which
  then suppresses the outgoing `close_notify`.
- **Keep egress fire-and-forget and only add the receive loop.** Rejected: without ordered egress with error
  propagation, the teardown `close_notify` has no delivery guarantee and a send failure stays invisible.
