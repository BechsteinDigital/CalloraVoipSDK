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

## Known limits

- **Safari / WebKit** — a `BrowserEngine.WebKit` exists in the matrix, but no verified run.
- **Data channels (SCTP)** — not implemented, so anything relying on them will not work.
- **TURN relay is UDP-only** — no TCP/TLS relay. The relay path itself is verified against a real
  coturn server.
- **Receive-side simulcast** ships in **4.7.0-preview** (transport-only, forwarding-only) — a
  browser's simulcast layers arrive demultiplexed and RID-tagged (`EncodedFrame.Rid`), but the
  preview primitives are not yet covered by the browser-interop CI matrix.

Interop reports for other browsers are welcome:
[info@bechstein.digital](mailto:info@bechstein.digital).
