# Interop matrix

CalloraVoipSdk implements standard SIP/SDP/RTP, so it is expected to interoperate with
any RFC-compliant PBX or trunk. This page is explicit about **what has actually been
verified** versus what is configuration guidance we have not yet run through a formal
interop test.

## Verification status

| Platform | Status | Notes |
|----------|--------|-------|
| **Asterisk** | ✅ Full SIP/RTP flow automated in CI | Real PJSIP Asterisk container (Testcontainers), **all cases green / none skipped**: register (happy + failure), in/outbound calls with live RTP, codec negotiation (PCMU/PCMA/G722), SRTP-SDES, DTMF (RFC 4733), hold/unhold, blind & attended transfer, session timers (RFC 4028), early media (RFC 3960), TCP/TLS transport, plus a two-leg bridged call with byte-exact bidirectional media. See the [Asterisk page](asterisk.md) |
| **Chromium** (WebRTC) | ✅ Automated in CI | Headless via Playwright: signalling → ICE → DTLS-SRTP → SRTP, bidirectional Opus and browser-decoded VP8, in **both** roles (SDK as offerer and as answerer). See [WebRTC](../guides/webrtc.md) |
| **Mozilla Firefox** (WebRTC) | ✅ Automated in CI | The same three scenarios via the browser-agnostic `BrowserEngine` matrix. Negotiates DTLS-SRTP with **AES-CM** — the SDK needs no AES-GCM for Firefox interop |
| **coturn** (TURN relay) | ✅ Automated in CI | TURN relay allocation and media over a real coturn server, end-to-end |
| **AVM FRITZ!Box** | ✅ Verified against a live device (manual) | Register, dial, two-way audio, DTMF against real hardware; source of several hardening fixes. Not an automated CI test |
| **FreeSWITCH** | 🧪 Automated, run locally | The same PBX matrix runs against a real FreeSWITCH container via the shared `IPbxFixture` abstraction, but the suite is **not yet in the PR CI gate** (trait `InteropFreeSwitch`) — it is a local-first check for now. See the [FreeSWITCH page](freeswitch.md) |
| WebKit / Safari (WebRTC) | ⚙️ Not yet verified | The browser matrix has a WebKit engine, but the tests skip when the browser is not installed |
| sipgate | ⚙️ Guidance only — not yet formally verified | Standard trunk registration expected to work; see the page |
| 3CX | ⚙️ Guidance only — not yet formally verified | Standard extension registration expected to work |

- ✅ **Automated (CI)** — a repeatable interop suite runs the full flow against a real container or
  browser on every relevant run.
- ✅ **Verified (manual)** — exercised against real hardware by hand; not reproducible in CI.
- 🧪 **Automated, run locally** — a repeatable suite exists and passes, but is not (yet) part of the
  CI gate, so regressions are caught only when it is run.
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
