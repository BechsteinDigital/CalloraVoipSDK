# Interop matrix

CalloraVoipSdk implements standard SIP/SDP/RTP, so it is expected to interoperate with
any RFC-compliant PBX or trunk. This page is explicit about **what has actually been
verified** versus what is configuration guidance we have not yet run through a formal
interop test.

## Verification status

| Platform | Status | Notes |
|----------|--------|-------|
| **Asterisk** | ✅ Full SIP/RTP flow automated in CI | Real PJSIP Asterisk container (Testcontainers): register (happy + failure), in/outbound calls with live RTP, codec negotiation (PCMU/PCMA/G722), SRTP-SDES, DTMF (RFC 4733), hold/unhold, blind & attended transfer, session timers (RFC 4028), TCP/TLS transport. Known gap: early media (183). See the [Asterisk page](asterisk.md) |
| **AVM FRITZ!Box** | ✅ Verified against a live device (manual) | Register, dial, two-way audio, DTMF against real hardware; source of several hardening fixes. Not an automated CI test |
| sipgate | ⚙️ Guidance only — not yet formally verified | Standard trunk registration expected to work; see the page |
| FreeSWITCH | ⚙️ Guidance only — not yet formally verified | Standard SIP profile expected to work |
| 3CX | ⚙️ Guidance only — not yet formally verified | Standard extension registration expected to work |

- ✅ **Automated (CI)** — a repeatable interop suite runs the full SIP/RTP flow against a real
  container on every relevant run.
- ✅ **Verified (manual)** — exercised against real hardware by hand; not reproducible in CI.
- ⚙️ **Guidance only** — the SDK speaks standard SIP and *should* interoperate, and we provide
  configuration notes, but we have **not** yet run a validated end-to-end test against that
  platform. Do your own acceptance test before relying on it in production.

## What "standard" support means

The SDK covers the interop-relevant basics broadly:

- Digest authentication (RFC 2617), bounded stale-nonce retry
- `Expires` precedence and registration refresh (RFC 3261 §10.2.1.1 / §10.3)
- Reliable provisionals (RFC 3262) only when the peer requires `100rel`
- Static payload types without `rtpmap`, ordered codec preference
- RTCP compound decoding tolerant of unknown packet types (e.g. RFC 3611 XR)
- SRTP/SRTCP via SDES (RFC 4568 / RFC 3711), offered and answered

## Reporting interop results

If you validate against a platform not marked verified here, interop reports are
welcome — contact [info@bechstein.digital](mailto:info@bechstein.digital). Verified
results get promoted on this matrix.
