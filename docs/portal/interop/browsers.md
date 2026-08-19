# Browsers (WebRTC)

**Status: ✅ Chromium and Firefox are automated in CI.** The [WebRTC facade](../guides/webrtc.md)
is verified end-to-end against real, headless browsers driven by Playwright — not against a mock
peer. Safari/WebKit is **not** verified.

## What is actually verified

Three scenarios run per engine, so six tests in total (trait `BrowserInterop`):

| Scenario | What it proves |
|----------|----------------|
| **Audio** | SDK as offerer: signalling → ICE → DTLS-SRTP → SRTP, with the browser's Opus frames arriving on `TrackReceived`/`FrameReceived` and echoed back so media flows in **both** directions |
| **Video** | The same path with VP8, decoded by the browser — the SDK never encodes (transport-only) |
| **Offerer role reversal** | The **browser** offers and the SDK answers, so both ICE roles (controlling and controlled) are exercised |

The browsers run against the same plain `RTCPeerConnection` pages (`peer.html`,
`peer-offerer.html`, `peer-video.html`) — no browser-specific shims. Only two things differ per
engine and are encapsulated in `BrowserEngine`: how fake media and mDNS are enabled (Chromium via
launch flags, Firefox via `about:config` prefs) and where the executable lives in the Playwright
cache.

Firefox negotiates DTLS-SRTP with **AES-CM**; the SDK needs no AES-GCM for Firefox interop, though
it offers the AEAD-GCM suites preferred (RFC 7714) and falls back cleanly.

## Running it yourself

```bash
dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 \
  --filter "Category=BrowserInterop"
```

Tests **skip** when the engine is not installed in the Playwright cache — a green run without the
browser proves nothing. That skip is exactly why WebKit is listed as unverified.

## Networking note

The peer binds to the host's LAN IPv4 rather than loopback: Firefox does not generate `127.0.0.1`
candidates, so both sides must offer the same routable address. Browser `.local` mDNS candidates
are resolved through the SDK's `IMdnsResolver` seam (RFC 8828) instead of being dropped.

## Connection lifecycle (4.8)

When the browser closes the DTLS association (`close_notify`/alert), the peer ends the association and
surfaces `State == PeerConnectionState.Closed` — media does not keep flowing under a keying channel the
peer considers closed (RFC 8827 §6.5). A STUN Binding success is only trusted when it carries
MESSAGE-INTEGRITY after credentials were sent (RFC 5389 §10.1.2); a non-conforming server that omits it
triggers a safe, logged **host-only** fallback rather than being trusted — useful to know if a
misconfigured STUN/TURN server yields host-only candidates.

## Known limits

- **Safari / WebKit** — a `BrowserEngine.WebKit` exists in the matrix, but no verified run.
- **Data channels (SCTP)** — not implemented, so anything relying on them will not work.
- **TCP/TLS TURN relay is not yet browser/real-server-verified here.** New in 4.11 (a stream relay over
  a persistent TCP/TLS connection, RFC 8656 §12) and unit-proven, but its data path over real browsers /
  cloud TURN is still tracked in this matrix. The **UDP relay** path is verified against a real coturn server.
- **The 4.7 multi-party primitives are outside this CI matrix.** Multiple video and audio tracks over
  one BUNDLE, mid-call renegotiation, receive-side simulcast demux (`EncodedFrame.Rid`) and the
  per-peer bitrate recommendation are stable since 4.7.0 and transport-only, but the browser suite
  above exercises the **1 audio + 1 video** path only. A multi-track topology against a browser is
  therefore unverified here — validate it against your own clients. See the
  [WebRTC guide](../guides/webrtc.md).
- **ICE restart on a connected peer** is supported (since 4.11, `CreateIceRestartOfferAsync`), but its
  behaviour over real browsers is not yet exercised in this matrix.

Interop reports for other browsers are welcome:
[info@bechstein.digital](mailto:info@bechstein.digital).
