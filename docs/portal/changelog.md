# Changelog

The authoritative changelog lives in the repository:
[`CHANGELOG.md`](https://github.com/BechsteinDigital/CalloraVoipSDK/blob/main/CHANGELOG.md)

## Release highlights

### Unreleased
- **Local ICE restart (RFC 8445 §9):** `ICall.RestartIceAsync()` restarts ICE from the application —
  new credentials on the existing socket, role preserved — instead of only detecting a peer-initiated
  restart. Together with the consent-loss signal on `ICall.IceConnectionStateChanged` an app can now
  detect a dead media path *and* repair it.
- **RTP/RTCP port-pair reservation:** media binds an even RTP port and reserves its RTCP successor
  (RFC 3550 §11), closing the race where the RTCP port could be taken between deriving and binding it.
- **Recording encryption streams in constant memory:** an AES-GCM-HKDF STREAM construction encrypts in
  fixed-size chunks with a per-file HKDF-derived key, so recordings of any length no longer load whole
  into memory — and the long-term key never reuses a (key, nonce) pair across files.
- **Two new per-PR CI gates:** a **chaos/fault-injection gate** (transport loss, malformed packets,
  signalling outage, resource churn → graceful degradation, recovery, no leak) and a **performance
  gate** on the SRTP crypto hot path.
- **WebRTC is now browser-validated — Chromium *and* Firefox.** The facade runs end-to-end in CI
  against real headless browsers (Playwright) — signalling → ICE → DTLS-SRTP → SRTP with
  bidirectional Opus and browser-decoded VP8, in **both** roles (SDK as offerer and as answerer),
  driven by a per-engine `BrowserEngine` matrix. A controlled answerer can use its own TURN relay,
  verified against a real **coturn**. WebKit/Safari remains uncovered.
- **AEAD-AES-GCM SRTP/SRTCP (RFC 7714):** the `AEAD_AES_128_GCM` / `AEAD_AES_256_GCM` suites are
  implemented end to end and offered **preferred** in the DTLS-SRTP `use_srtp` negotiation, with
  AES-CM-128 kept as the interoperable fallback. Note that browser interop never depended on it —
  Firefox was verified negotiating AES-CM before GCM landed. See [SRTP/SRTCP](guides/srtp-srtcp.md).
- **Client facade & audio hardening:** constructor-leak fix, thread-safe peer events, a monotonic
  rate clock, a real `WebRtcClient` teardown, Windows/Linux audio parity (mute gate, drop-oldest,
  playback metrics, no hot-path allocation) and shared PCM/codec helpers instead of per-platform
  duplication. The weak-crypto analyzers (CA5350/CA5351) are no longer suppressed solution-wide.
- **SDP & media-file hardening:** `rtcp-mux` is only answered when offered (RFC 5761), bandwidth
  lines keep their original type token (no TIAS→AS corruption), an SDP missing mandatory lines or a
  usable `c=` is rejected instead of defaulting to loopback, RTX payload types stay within the 7-bit
  range, MP3 passthrough handles ID3v2 tags, ffmpeg is killed on cancellation, and the WAV parser
  tolerates partial reads.
- **Security hardening:** SAN matching parses ASN.1 directly (no locale-dependent text parsing) and
  the TLS certificate load is thread-safe.
- **IPv6 media for WebRTC:** the media socket binds to the address family of the configured
  `LocalEndPoint` — IPv6 endpoints now work (previously hard-coded to IPv4).
- **Comparison & capacity evidence:** scenario-by-scenario comparison against another stack, plus a
  quality-gated capacity benchmark ramping to thousands of calls against a real Asterisk. Both run
  outside regular CI.
- **Core hardening:** playback-cancellation leak and recording-writer race closed, `Call.Dispose`
  awaits the BYE before tearing down the channel, hold events fire only on a real change, and
  `LineState` gained a rule table.
  See [WebRTC](guides/webrtc.md).
- **WebRTC video repair & congestion control:** NACK/PLI/FIR key-frame recovery (RFC 4585/5104),
  RTX (RFC 4588) and transport-wide congestion control (transport-cc) on the BUNDLE path, with
  `NackCount` / `PliCount` / `FramesDropped` / `AvailableOutgoingBitrateBps` wired into `getStats`
  and a public `VideoKeyFrameRequested` event.
- **WebRTC mDNS candidates (RFC 8828):** browser `.local` host candidates are resolved through an
  `IMdnsResolver` seam instead of being dropped.
- **Call termination reason:** `ICall.TerminationReason` tells a busy, unanswered, cancelled or
  rejected call apart from a generic failure, classified from the SIP response status (RFC 3261 §21).
  Cancelling a ringing dial now sends a proper **CANCEL** (§9.1) and keeps the call reachable.
- **Security hardening:** RTP SSRC/sequence/timestamp seeded from a CSPRNG (RFC 3550), the
  symmetric-RTP latch no longer re-points media at an unauthenticated source (the CVE-2017-14099
  pattern), SRTP master key material is wiped after derivation, and jitter/loss/RTT run off a
  monotonic clock. On the STUN/TURN/ICE side: CSPRNG DNS-SRV transaction ids (RFC 5452), an
  over-length STUN body is rejected instead of truncated, and short-term credentials match strictly
  on username **and** type.
- **Interop:** a second PBX — **FreeSWITCH** — runs the same matrix behind a shared `IPbxFixture`
  abstraction (local-first, not yet in the CI gate). See the [interop matrix](interop/matrix.md).
- **SIP PUBLISH soft-state lifecycle (RFC 3903 §4/§6):** `RefreshPublicationAsync`,
  `ModifyPublicationAsync` and `RemovePublicationAsync` drive an existing publication via
  `SIP-If-Match` — a publication can now be kept alive, changed or withdrawn, not just created.
  See [Presence & event state](guides/presence-publish.md).
- **SIP stack hardening:** precise transport-failure classification, 488 on an unanswerable
  re-INVITE/UPDATE offer (RFC 3264), `Via`-branch-strict response matching (RFC 3261 §17.1.3),
  registrar DNS off the inbound dispatch thread, bounded `Dispose` joins, WebSocket listener bind
  retry, and a redirect fan-out cap.
- **Interop:** two-leg scenario tests over the bridged call (DTMF, hold/unhold, attended transfer,
  codec-mismatch transcoding), a concurrent-call soak, and a PBX-agnostic `IPbxFixture` abstraction
  so the matrix can run against further PBXs.

### 4.6.0-preview.3 — 2026-07-25
- **Early media (RFC 3960):** a 180/183 carrying SDP now starts a **receive-only** media session
  before the call is answered — network announcements and ringback reach the app pre-answer.
  New surface: `IPhoneLine.OutboundCallRinging` (a call handle while `DialAsync` is still
  blocking), `ICall.EarlyMediaSdp`, and DTMF in the early dialog (`SendDtmfAsync` while ringing —
  IVR / AI outbound). Verified end-to-end against a real Asterisk, plain and SRTP-SDES.
- **SIP MESSAGE (RFC 3428):** send and receive out-of-dialog instant messages —
  `VoipClient.SendMessageAsync(...)` and the `IncomingMessage` event.
  See [Instant messages](guides/messaging.md).
- **SIP PUBLISH (RFC 3903):** publish event state such as presence via `PublishAsync`, returning the
  assigned SIP-ETag and granted lifetime. See [Presence & event state](guides/presence-publish.md).
- **REFER transfer progress (RFC 3515 / 6665):** an incoming REFER now carries an
  `IReferSubscription` (`TransferRequestedEventArgs.Subscription`) reporting the referred call's
  progress, with an auto-timeout bound to the session lifetime — instead of an optimistic
  "100 Trying → 200 OK".
- **Interop- and stability-critical fixes** found by a full source audit:
  - **SIP:** re-ACK on a retransmitted 2xx of a confirmed dialog (RFC 3261 §13.2.2.4) — a lost ACK
    no longer lets the peer tear the call down; digest retry on the session-timer UPDATE and
    SUBSCRIBE refresh paths (a 401 on a refresh no longer terminates a healthy call); a correctly
    formed `+sip.instance` contact parameter (RFC 5626 §4.1); and the INVITE auth retry no longer
    adopts the 401's To-tag (RFC 3261 §12.1.2) — which had made **all authenticated outbound calls**
    fail with `481` against strict registrars.
  - **SRTP:** SRTCP now uses an 80-bit auth tag for every suite (RFC 4568 §6.2) — fixes RTCP interop
    with libsrtp-based peers when `AES_CM_128_HMAC_SHA1_32` is negotiated. SDES over insecure
    signalling is warned about, with an opt-in `RequireSecureSignalingForSdes` enforcement.
  - **TURN:** Send indications are no longer required to be authenticated (RFC 8656 §10), which had
    rejected RFC-conformant third-party clients; a configurable `PublicRelayAddress` replaces a
    loopback relay address that was unreachable off-host.
  - **Media/Core:** a transfer can no longer wedge the call in `Transferring` on a signalling
    failure; no media-session leak when a call terminates during ICE selection; G.722 is transcoded
    statefully across frames (removing audible artefacts); and the media sockets' kernel receive
    buffer is no longer capped at 8 KiB.
- **Interop:** the Asterisk matrix runs with **no skipped cases**, and a **two-leg bridged-call**
  suite verifies bidirectional, byte-exact media through the PBX.
  See the [interop matrix](interop/matrix.md).

### 4.6.0-preview.2 — 2026-07-22
- **Self-hostable STUN & TURN server** via `AddCalloraStunServer(...)` / `AddCalloraTurnServer(...)`,
  with TURN control over UDP/TCP/TLS, FINGERPRINT validation, and EVEN-PORT + RESERVATION-TOKEN
  (RFC 8656 §7); plus the TURN relay lifecycle (permission refresh and channel rebind, §9/§12).
- **RTCP quality on the WebRTC/BUNDLE media path** (RFC 3550): periodic Sender/Receiver Reports,
  per-SSRC reception statistics, RTT from RR/SR, and per-SSRC/MID quality snapshots on `WebRtcStats`.
- **DTMF (RFC 4733) end-to-end on the WebRTC/BUNDLE path.**
- **SHA-512-256 SIP digest authentication** (RFC 8760), digest `qop=auth-int` (RFC 7616) and the
  RFC 5626 outbound `;ob` contact parameter.
- Broad SIP/RTP conformance fixes: dialog route-set routing (§12.2.1.1), dialog identity matching
  (§12.2.2), `received=`/`rport=` handling (RFC 3581), strictly in-order PRACK (RFC 3262 §4),
  SSRC-collision handling and randomized RTCP intervals (RFC 3550 §6.3.1/§8.2).

### 4.6.0-preview.1 — 2026-07-18
- **WebRTC facade (preview, transport-only)** in the `CalloraVoipSdk.WebRtc` namespace: a
  signalling-neutral browser/peer surface mirroring `VoipClient` — `WebRtcClient.CreatePeer()`
  (ICE, DTLS-SRTP, BUNDLE, RTP/RTCP), an SDK-driven handshake (`peer.ConnectAsync(signalling, role)`),
  the W3C track model (`TrackReceived` → `RemoteTrack`/`EncodedFrame`), a multi-peer manager
  (`client.Peers`) and L3 seams (`IMediaTap`, `IWebRtcClientModule`). Transport-only — bring your own
  codec. See [WebRTC](guides/webrtc.md). Includes trickle ICE + early-bind (an ephemeral media port
  yields a live m-line) and send-side simulcast (RFC 8853, offerer-confirmed; receive-side RID demux is
  a later slice). **Preview:** not yet browser-validated; API may change; no data channels (SCTP) or
  TURN relay yet.
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
