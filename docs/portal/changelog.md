# Changelog

The authoritative changelog lives in the repository:
[`CHANGELOG.md`](https://github.com/BechsteinDigital/CalloraVoipSDK/blob/main/CHANGELOG.md)

## Release highlights

### 4.6.0-preview.1 — 2026-07-18
- **WebRTC facade (preview, transport-only)** in the `CalloraVoipSdk.WebRtc` namespace: a
  signalling-neutral browser/peer surface mirroring `VoipClient` — `WebRtcClient.CreatePeer()`
  (ICE, DTLS-SRTP, BUNDLE, RTP/RTCP), an SDK-driven handshake (`peer.ConnectAsync(signalling, role)`),
  the W3C track model (`TrackReceived` → `RemoteTrack`/`EncodedFrame`), a multi-peer manager
  (`client.Peers`) and L3 seams (`IMediaTap`, `IWebRtcClientModule`). Transport-only — bring your own
  codec. See [WebRTC](guides/webrtc.md). **Preview:** not yet browser-validated; API may change; no
  data channels / TURN / simulcast yet.
- **BREAKING (from 4.6):** SIP-facade config types renamed — `SdkConfiguration` → `VoipConfiguration`,
  `SdkOptions` → `VoipOptions`, `AddCallora(...)` → `AddCalloraVoip(...)` (no aliases). `VoipClient` is
  unchanged. See [`CHANGELOG.md`](https://github.com/BechsteinDigital/CalloraVoipSDK/blob/main/CHANGELOG.md).

### 4.5.0 — 2026-07-15
- Public **video** API (transport-only): send/receive encoded frames
  (`client.Media.CreateVideoReceiver()/CreateVideoSender()`, `VideoFrame`), a ready-to-use
  recommended outbound bitrate + `NetworkQuality` from transport-cc
  (`IVideoSender.RecommendedBitrateChanged`), inbound key-frame flags and RTCP PLI/FIR
  keyframe-request feedback, plus a default-video convenience
  (`client.AttachDefaultVideoAsync` with a DI-supplied `IVideoDevice` codec). The SDK never
  encodes/decodes — bring your own VP8/H.264 codec. See [Video calls](guides/video-calls.md)

### 4.4.1 — 2026-07-11
- Native Opus (RFC 7587) in the Linux and Windows audio devices: negotiated Opus now decodes/encodes
  at 48 kHz through `AttachDefaultAudioAsync` instead of being mis-decoded as G.722. Opus stays
  opt-in via `PreferredAudioCodecs`

### 4.4.0 — 2026-07-11
- Additive public-API capabilities (no breaking changes) closing developer-experience gaps:
  consumer-selectable default SIP transport (`SdkConfiguration.DefaultTransport`, UDP/TCP/TLS/WS/WSS);
  an opt-in public media address (`SipAccount.PublicMediaHost`) for CGNAT / static 1:1 NAT;
  ICE observability (`ICall.IceSnapshot`) and raw RTP statistics (`ICall.RtpStatistics`) on the call;
  custom outbound INVITE headers (`DialOptions.CustomHeaders`, injection-guarded) plus read-only
  remote identity (`ICall.RemoteAssertedIdentity`, `ICall.Diversion`); and the negotiated SRTP suite
  name with an SRTCP-encrypted flag on `CallMediaParameters`

### 4.3.5 — 2026-07-10
- Security/robustness fixes from a production-readiness review: stream-framer memory-DoS limits,
  SIP-over-WebSocket `sip` subprotocol (RFC 7118), TLS/WSS SNI + certificate validation against the
  SIP domain (not the IP), and redaction of SRTP keys / ICE passwords in trace logs
- Versioning now flows from the git tag into the assemblies; the release pipeline runs tests before
  publishing; corrected several documentation overclaims (Opus/device, thread-safety, MediaFrame)

### 4.3.4 — 2026-07-10
- Attended transfer now sends REFER with an RFC 3891 `Replaces` (RFC 5589), so REFER/Replaces-capable
  PBXs (Asterisk / FreeSWITCH / 3CX) actually join the two calls; endpoints without REFER transfer
  (e.g. a FRITZ!Box on PSTN legs) still need a media bridge

### 4.3.3 — 2026-07-10
- Documentation-only release (no code changes vs 4.3.2): restructured the portal around a
  7-section information architecture (Overview · Getting Started · Core Concepts · Guides ·
  Interop · Production · Commercial Modules), with an honest interop verification status
  and commercial modules marked as in development

### 4.3.2 — 2026-07-09
- Documentation-only release (no code changes vs 4.3.1): corrected the ICE status row to the
  released state and added the GitHub Pages documentation link to the README

### 4.3.1 — 2026-07-09
- Fixed the RFC 3550 jitter estimator derailing on a stalled RTP timestamp (comfort noise /
  audio-payload repeats) — no more mid-call latency spike from false late-drops
- Registration removal (unregister) now reuses the registration's Call-ID + CSeq (RFC 3261
  §10.2.2), so a binding is actually cleared instead of lingering after stop/restart
- Documented the event threading contract and the `ICall` error contract; filled public XML-doc gaps

### 4.3.0 — 2026-07-09
- SRTP as the offerer (SDES, RFC 4568): outbound calls now advertise `RTP/SAVP` + `a=crypto`
- SRTCP (RFC 3711 §3.4): a negotiated SRTP call now encrypts and authenticates RTCP too
- SRTP re-keying on re-INVITE (RFC 3264 §8), and hold/unhold keeps SRTP alive
- Two remotely triggerable receive-loop DoS fixes on malformed short RTP/RTCP packets

### 4.2.0 — 2026-07-09
- Protocol-correctness fixes: `Expires` precedence/responses (RFC 3261 §10.2.1.1/§10.3),
  bounded stale-nonce retry (RFC 2617), dropped the non-functional SHA-512-256 digest advert
- Peer MOS in `CallQualitySnapshot` from RTCP-XR VoIP Metrics (RFC 3611 §4.7)

### 4.1.0 — 2026-07-09
- Bidirectional ICE (RFC 8445 / RFC 7675): inbound connectivity checks, role derivation +
  tie-breaker sharing, consent freshness with media cease, triggered checks — opt-in and still
  marked experimental/unproven in production

### 4.0.0 — 2026-07-09
- SRTP/SDES media (offer/answer keying, media path, hardening), Opus codec (opt-in),
  RTCP-XR decoding, SDP `o=` session versioning
- **Breaking:** `DialOptions` moved to the domain layer

### 3.1.1 — 2026-07-08
- RFC 3550 jitter estimator fixed (arrival-time overflow made jitter converge to the
  frame interval); RTT measured from RTCP LSR/DLSR now feeds the adaptive jitter buffer

### 3.1.0 — 2026-07-08
Hardening from the first real-world interop test (AVM Fritz!Box, AI voice agent):
- `SdkConfiguration.PreferredAudioCodecs` — ordered codec preference for offers,
  answers and RTP sessions
- Advertised media address resolution fixed (no loopback towards LAN peers)
- Static payload types without rtpmap now negotiate correctly
- Reliable provisionals (RFC 3262) only on explicit `Require: 100rel`
- RTCP compound decoding tolerates unknown packet types (e.g. RFC 3611 XR)
- SIP wire trace diagnostics (Trace level, includes SDP bodies)

### 3.0.0 — 2026-07-07
- Module registry as the SDK extension point: `IVoipClientModule`, `client.Modules`,
  typed resolution via `Get<T>`/`TryGet<T>`
- Per-call media tap pinned as a public, tested contract

### 2.0.0 — 2026-07-07
- **Breaking:** unimplemented module facades removed from the public surface —
  these capabilities return as separate commercial plugins
- `net9.0` and `net10.0` target frameworks added
- First releases published to nuget.org
