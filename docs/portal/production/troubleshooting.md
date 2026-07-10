# Troubleshooting

Concrete fixes for the failures you are most likely to hit. Pair each with a `Trace` log
and quality snapshots — see [Diagnostics](diagnostics.md).

## Registration fails

- **401/407 loop** — wrong username/password/realm. The SDK does a bounded stale-nonce
  retry; a persistent challenge means credentials. For 3CX, use the **auth ID**, not the
  extension number ([3CX](../interop/3cx.md)).
- **No response to REGISTER** — registrar host/port unreachable, or the box expects TLS.
  Check `SipServer` and network path.
- **Binding lingers after restart** — fixed: unregister reuses the registration Call-ID +
  CSeq (RFC 3261 §10.2.2). Prefer an awaited `UnregisterAsync` on shutdown over relying on
  dispose.

## One-way or no audio

- SDP advertises a wrong/unreachable media address → check `c=`/`m=` in the trace. The
  SDK advertises a routable (non-loopback) address; on complex NAT you may still need an
  SBC. See [NAT and SIP trunks](../guides/nat-and-trunks.md).
- No common codec → align `PreferredAudioCodecs` with the peer; keep G.711 (PCMU/PCMA) as
  a baseline.

## Choppy / robotic audio

- High jitter/loss in the quality snapshots → a network problem, not the SDK. The adaptive
  jitter buffer compensates within limits.
- A stalled RTP timestamp burst (comfort noise / repeats) no longer derails the jitter
  estimator (fixed in 4.3.1).

## DTMF not detected

- Confirm RFC 4733 telephone-events are negotiated in the SDP and the peer's DTMF mode
  matches. Inbound DTMF fires `DtmfReceived` — remember it can arrive on two threads
  ([Events](../concepts/events.md)).

## SRTP call fails

- With `SrtpPolicy.Required`, a peer that can't do SDES SRTP yields a **failed** call by
  design (no silent downgrade). Confirm `a=crypto` in the trace and the peer's SRTP
  setting. Use `Optional` to allow fallback. See [SRTP/SRTCP](../guides/srtp-srtcp.md).

## Calls linger after the process exits

- Explicitly `HangupAsync` active calls and `UnregisterAsync` lines before `Dispose()`;
  the dispose-time cleanup is best-effort ([Lifecycle & dispose](lifecycle-dispose.md)).

## Still stuck?

Capture a `Trace` log of the failing call and open a conversation at
[info@bechstein.digital](mailto:info@bechstein.digital).
