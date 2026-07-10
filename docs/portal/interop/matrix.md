# Interop matrix

CalloraVoipSdk implements standard SIP/SDP/RTP, so it is expected to interoperate with
any RFC-compliant PBX or trunk. This page is explicit about **what has actually been
verified** versus what is configuration guidance we have not yet run through a formal
interop test.

## Verification status

| Platform | Status | Notes |
|----------|--------|-------|
| **AVM FRITZ!Box** | ✅ Verified in a real interop test | Register, dial, two-way audio, DTMF against a live device; source of several hardening fixes |
| sipgate | ⚙️ Guidance only — not yet formally verified | Standard trunk registration expected to work; see the page |
| Asterisk | ⚙️ Guidance only — not yet formally verified | Standard `chan_pjsip` peer expected to work |
| FreeSWITCH | ⚙️ Guidance only — not yet formally verified | Standard SIP profile expected to work |
| 3CX | ⚙️ Guidance only — not yet formally verified | Standard extension registration expected to work |

⚙️ **Guidance only** means: the SDK speaks standard SIP and *should* interoperate, and we
provide configuration notes, but we have **not** yet run a validated end-to-end test
against that platform. Do your own acceptance test before relying on it in production.

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
