# Simplest WebRTC video call (website)

A two-person browser video call in its simplest form: a minimal ASP.NET **WebSocket signalling relay**
plus a static page that uses **native browser WebRTC** (`getUserMedia` + `RTCPeerConnection`).

```bash
dotnet run --project examples/CalloraVoipSdk.Sample.WebRtcVideoCall.Web
```

Then open the printed URL (e.g. `http://localhost:5xxx`) in **two tabs** — or on two devices on the same
network — and click **Join** in both. The two browsers connect peer-to-peer and exchange video; the .NET
host only forwards SDP/ICE between them.

## Architecture

```
Browser A  <-- media (RTP/SRTP, peer-to-peer) -->  Browser B
     \                                              /
      \------- WebSocket signalling (SDP/ICE) -----/
                     .NET host (this app)
```

The CalloraVoipSdk **WebRTC media peer is not in the media path** here — this sample is the *signalling*
layer of the WebRTC story. Putting the SDK peer in the media path (browser ↔ SDK ↔ browser, as an
SFU/gateway) is the browser-interop milestone and requires validation against real browsers.

## Notes

- No STUN/TURN is configured, so it works on `localhost` / the same LAN. For real-world NAT traversal add
  ICE servers in `wwwroot/app.js`.
- One global two-peer room; the second peer to join initiates the offer.
- Browsers require a secure context for camera access on non-localhost origins (serve over HTTPS).
