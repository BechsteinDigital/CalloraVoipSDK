# Changelog

The authoritative changelog lives in the repository:
[`CHANGELOG.md`](https://github.com/BechsteinDigital/CalloraVoipSDK/blob/main/CHANGELOG.md)

## Release highlights

### 4.8.0 — 2026-08-06

**Stack-wide hardening plus two additive public features.** A series of protocol-layer review findings
across DTLS/STUN/TURN/RTP/RTCP/SDP/SIP/WebRTC/Audio, hardened fail-closed with caps far above real
signalling/media, so legitimate traffic is unaffected. `PublicApi.approved.txt` only grew — a consumer
that opts into none of the new surfaces stays behaviour-identical. See
[ADR-064](../adr/ADR-064-per-line-sip-mutual-tls.md),
[ADR-065](../adr/ADR-065-public-pcm-transcoding-surface.md) and
[ADR-066](../adr/ADR-066-dtls-post-handshake-association-servicing.md).

**New features**

- **Mutual TLS per SIP line, with a certificate from memory.** Outbound SIP over TLS/WSS presents a
  client certificate when configured — sent only if the registrar requests one (RFC 8446 §4.4.2), so
  behaviour is byte-identical otherwise. Two lines to the same registrar present their own identity over
  separate pooled connections (pool key `(transport, addr:port, identity)`), stamped on every request
  the line originates. New: `ConnectOptions.LineTls`, `TlsConfiguration.ClientCertificate` (caller-owned
  in-memory `X509Certificate2`, precedence over `CertificatePath`). See the
  [SIP mTLS guide](guides/sip-tls-mtls.md). (#183)
- **Public PCM transcoding surface** (`CalloraVoipSdk.Audio.Abstractions`): `IAudioPayloadCodec` +
  `AudioPayloadCodecFactory` transcode Opus / G.711 / G.722 ↔ PCM16 for a server-side mixer/SFU, with no
  Concentus/NAudio in any public signature and no new `PackageReference`. See the
  [audio transcoding guide](guides/audio-transcoding.md). (#205)
- **Fail-closed SIP-TLS server trust:** `SipTlsTrustMode { System, DangerousAcceptAnyChain }` +
  `TlsConfiguration.TrustMode`, strict RFC 5922 §7.2 SIP-domain identity and RFC 5924 §5 EKU policy;
  `AcceptUntrustedCertificates` is now an `[Obsolete]` alias. (#164)
- **Configurable SIP inbound hardening** (`SipTransportHardeningConfiguration` /
  `SipSignalingHardeningConfiguration`, defaults equal to the built-in limits) and a **DTLS handshake
  deadline** (`DtlsHandshakeOptions.HandshakeTimeout`, default 20 s →
  `DtlsSrtpHandshakeTimeoutException`). (#158, #163)

**Security & correctness**

- **DTLS-SRTP:** constant-time fingerprint comparison (RFC 5763/8122), private-identity zeroing, a
  stateless HelloVerifyRequest cookie before the certificate flight (RFC 6347 §4.2.1), ordered
  single-writer egress with a `close_notify` drain, and the association is now serviced after key export
  — a peer `close_notify`/alert ends it deterministically and surfaces as WebRTC
  `connectionState = "closed"` (RFC 8827 §6.5). (#190, #191, #192, #193, #163)
- **STUN/TURN:** Binding success responses require MESSAGE-INTEGRITY when credentials were sent (a
  non-conforming ICE server triggers a logged host-only fallback), `StunMessageCodec.Decode` rejects the
  whole message fail-closed, stateless STUN nonce, TCP/TLS slowloris deadlines, and surplus TURN
  allocations are torn down immediately with `LIFETIME=0` (RFC 8656). (#156, #184, #188)
- **RTP/RTCP DoS caps:** RTCP compound budgets, a transport-cc feedback expansion cap, depacketiser
  frame caps and a BUNDLE reception-state cap; plaintext legs discard foreign RTP/RTCP bound to the
  latch and suppress foreign SSRC-collision reseeds (RFC 3550 §8.2). (#161, #162)
- **SDP/WebRTC:** a structurally non-conforming remote answer is rejected (RFC 3264 §6 / RFC 8829), the
  offerer sends the codec the answer accepted (RFC 3264 §6.1), BUNDLE is grouped semantically not by
  string prefix (RFC 5888/8843/9143), and per-media-section collection caps bound the parser. (#160)
- **Fixed:** inbound audio now carries its real RTP timestamp (`EncodedFrame.RtpTimestamp`, RFC 3550
  §5.1) — an SFU no longer stamps forwarded audio at `0`; `SipCredentials.ToString()` redacts the
  password; a 416 no longer downgrades `sips:` to `sip:`. (#170, #165, #158)

### 4.7.2 — 2026-08-01

**ICE connection-setup latency patch.** The internal ICE connectivity-check scheduler is reworked into a
globally paced, *overlapping* RFC 8445 checklist: checks start at most one per pacing interval (§14 `Ta`) but
run concurrently, so an unreachable higher-priority candidate no longer stalls every other pair behind its
timeout. STUN checks now retransmit at the transaction level (RFC 8489 §6.1), both ICE roles check actively
(§7.2), and peer-reflexive triggered checks (§7.3.1.4) dispatch reactively. It also folds in a round of
review-finding fixes: superseded-nomination cancellation, a priority-capped ICE checklist, type-scoped ICE
foundations, **stable append-only MIDs for runtime-added tracks** (RFC 8829), and recv-track DoS caps.
**`PublicApi.approved.txt` is unchanged** and a fixed 1+1 peer's SDP is byte-identical; the review fixes adjust
a few on-wire details for correctness. Full ICE stays opt-in and not yet browser-interop-proven. See
[ADR-062](../adr/ADR-062-ice-checklist-pacing.md) and [ADR-063](../adr/ADR-063-jsep-append-only-track-mids.md).

### 4.7.1 — 2026-07-31

**WebRTC/SFU correctness patch.** Stable browser-safe MIDs across RFC 8829 renegotiation
(`UseStableNumericMediaIds`), a live bundle sender for an outbound `sendonly` audio track the browser accepts
as `recvonly`, and a first ICE pair-progression fix (a lower-priority reachable candidate is checked before an
unreachable higher-priority one consumes another round — extended into a full checklist in 4.7.2). Additive and
transport-only.

### 4.7.0 — 2026-07-29

The 4.7 line builds **multi-party / SFU enablement** onto the WebRTC facade. Everything is **additive and
transport-only**: a peer that uses none of it negotiates byte-identical SDP and behaves exactly as in 4.6.
The SDK stays a peer — it **forwards, it does not mix or transcode**. See [WebRTC](guides/webrtc.md).

**WebRTC**

- **Multiple video tracks + mid-call renegotiation (RFC 8829).** `IPeerConnection.AddVideoTrack()` adds a
  further video track (its own `m=video` line, SSRC and `RemoteTrack.Mid`) before *or* after connect — a
  second `CreateOffer`/`SetRemoteDescriptionAsync` cycle applies the delta live, with no transport / DTLS /
  ICE / SRTP rebuild. New public types `IVideoTrack`, `VideoTrackOptions`, `TrackDirection`; ICE restart is
  not supported (dispose and re-create the peer). `IPeerConnection.SignalingState` /`SignalingStateChanged`
  surface the RFC 8829 state, and `RequestVideoKeyFrameAsync(mid)` targets one track.
- **Multiple audio tracks over one BUNDLE.** `IPeerConnection.AddAudioTrack()` (and an
  `AddAudioTrack(AudioTrackOptions)` overload) returns an `IAudioTrack` (`Mid`, `Direction`,
  `SendFrameAsync(frame, rtpTimestamp)`) — each track its own `m=audio` line, SSRC and per-participant
  `a=msid` on the shared transport, received per track (`RemoteTrack.Mid`), added/removed mid-call via
  renegotiation. The **primary** audio m-line anchors ICE/DTLS and is never deactivated (single-track
  SDP is byte-identical); DTMF stays on the primary track. The send path threads the RTP timestamp
  through so A/V sync holds against forwarded video. Forwarding building block for conference audio —
  the SDK does not mix.
- **Receive-side simulcast demux (RFC 8853/8852).** A peer's multiple encodings of one video m-line are
  separated receive-side into independent per-RID reassembly and tagged on the new `EncodedFrame.Rid`
  (`string?`); one `RemoteTrack` per m-line, layers told apart by `frame.Rid`. Completes simulcast
  (send side already shipped). **Forwarding-only** — no layer is dropped or transcoded; that is SFU
  logic. Non-simulcast receive is byte-identical (`Rid` is `null`).
- **Per-peer bitrate recommendation (transport-cc, RFC 8888).**
  `IPeerConnection.RecommendedOutgoingBitrateBps` (`long?`) plus a `RecommendedBitrateChanged` event
  carrying a `BitrateRecommendation` (`BitrateBps`, `Quality` `NetworkQuality`) — a finished
  recommended send bitrate toward the peer, derived from the peer's returned congestion feedback; for an
  SFU, the per-receiver signal of which layer to forward. A recommendation, not raw metrics, and
  **reactive** (fires per feedback interval, no poll); `null`/silent when transport-cc is not
  negotiated. No SDK throttling, no layer decision in the SDK.

### 4.6.0 — 2026-07-28

The 4.6 line adds a **WebRTC facade** and a **self-hostable STUN/TURN server** on top of the SIP + RTP
core, and closes every interop- and stability-critical finding of a full source audit. WebRTC is
validated end-to-end in CI against **real browsers** (Chromium and Firefox) and a real **coturn**; the
SIP core runs a full interop matrix against a real **Asterisk** with zero skipped cases, plus a second
PBX (**FreeSWITCH**) and a two-leg bridged call verified **byte-exact in both directions**.

> **BREAKING (from 4.5):** SIP-facade config types renamed — `SdkConfiguration` → `VoipConfiguration`,
> `SdkOptions` → `VoipOptions`, `AddCallora(...)` → `AddCalloraVoip(...)` (no compatibility aliases).
> `VoipClient` and all other public types are unchanged. See
> [`CHANGELOG.md`](https://github.com/BechsteinDigital/CalloraVoipSDK/blob/main/CHANGELOG.md).

**WebRTC**

- **WebRTC facade (transport-only)** in the `CalloraVoipSdk.WebRtc` namespace: a signalling-neutral
  browser/peer surface mirroring `VoipClient` — `WebRtcClient.CreatePeer()` (ICE, DTLS-SRTP, BUNDLE,
  RTP/RTCP), an SDK-driven handshake (`peer.ConnectAsync(signalling, role)`), the W3C track model
  (`TrackReceived` → `RemoteTrack`/`EncodedFrame`), a multi-peer manager (`client.Peers`) and L3 seams
  (`IMediaTap`, `IWebRtcClientModule`). Bring your own codec. See [WebRTC](guides/webrtc.md).
- **Browser-validated — Chromium *and* Firefox.** The facade runs end-to-end in CI against real
  headless browsers (Playwright) — signalling → ICE → DTLS-SRTP → SRTP with bidirectional Opus and
  browser-decoded VP8, in **both** roles (SDK as offerer and as answerer), driven by a per-engine
  `BrowserEngine` matrix. A controlled answerer can use its own TURN relay, verified against a real
  **coturn**. WebKit/Safari remains uncovered.
- **Trickle ICE + early-bind** (an ephemeral media port still yields a live m-line) and **send-side
  simulcast** (RFC 8853, offerer-confirmed; receive-side RID demux is a later slice).
- **Video repair & congestion control:** NACK/PLI/FIR key-frame recovery (RFC 4585/5104), RTX
  (RFC 4588) and transport-wide congestion control (transport-cc, RFC 8888) on the BUNDLE path, with
  `NackCount` / `PliCount` / `FramesDropped` / `AvailableOutgoingBitrateBps` wired into `getStats`
  and a public `VideoKeyFrameRequested` event.
- **mDNS candidates (RFC 8828):** browser `.local` host candidates are resolved through an
  `IMdnsResolver` seam instead of being dropped.
- **RTCP quality** (RFC 3550: periodic SR/RR, per-SSRC reception statistics, RTT from RR/SR, per-SSRC
  and per-MID snapshots on `WebRtcStats`) and **DTMF (RFC 4733)** end-to-end on the BUNDLE path.
- **IPv6 media:** the media socket binds to the address family of the configured `LocalEndPoint`.

**Connectivity**

- **Self-hostable STUN & TURN server** via `AddCalloraStunServer(...)` / `AddCalloraTurnServer(...)`,
  with TURN control over UDP/TCP/TLS, FINGERPRINT validation, and EVEN-PORT + RESERVATION-TOKEN
  (RFC 8656 §7); plus the TURN relay lifecycle (permission refresh and channel rebind, §9/§12) and a
  configurable `PublicRelayAddress` replacing a loopback relay address that was unreachable off-host.
- **Local ICE restart (RFC 8445 §9):** `ICall.RestartIceAsync()` restarts ICE from the application —
  new credentials on the existing socket, role preserved — instead of only detecting a peer-initiated
  restart. Together with the consent-loss signal on `ICall.IceConnectionStateChanged` an app can now
  detect a dead media path *and* repair it.

**Call control and signalling**

- **Early media (RFC 3960):** a 180/183 carrying SDP starts a **receive-only** media session before the
  call is answered — network announcements and ringback reach the app pre-answer. New surface:
  `IPhoneLine.OutboundCallRinging` (a call handle while `DialAsync` is still blocking),
  `ICall.EarlyMediaSdp`, and DTMF in the early dialog (`SendDtmfAsync` while ringing — IVR / AI
  outbound). Verified against a real Asterisk, plain and SRTP-SDES.
- **SIP MESSAGE (RFC 3428):** send and receive out-of-dialog instant messages —
  `VoipClient.SendMessageAsync(...)` and the `IncomingMessage` event.
  See [Instant messages](guides/messaging.md).
- **SIP PUBLISH (RFC 3903)** with the full soft-state lifecycle: `PublishAsync` plus
  `RefreshPublicationAsync`, `ModifyPublicationAsync` and `RemovePublicationAsync` (`SIP-If-Match`,
  §4/§6) — a publication can be kept alive, changed or withdrawn, not just created.
  See [Presence & event state](guides/presence-publish.md).
- **REFER transfer progress (RFC 3515 / 6665):** an incoming REFER carries an `IReferSubscription`
  (`TransferRequestedEventArgs.Subscription`) reporting the referred call's progress, with an
  auto-timeout bound to the session lifetime — instead of an optimistic "100 Trying → 200 OK".
- **Call termination reason:** `ICall.TerminationReason` tells a busy, unanswered, cancelled or
  rejected call apart from a generic failure, classified from the SIP response status (RFC 3261 §21).
  Cancelling a ringing dial sends a proper **CANCEL** (§9.1) and keeps the call reachable.
- **SHA-512-256 SIP digest authentication** (RFC 8760), digest `qop=auth-int` (RFC 7616) and the
  RFC 5626 outbound `;ob` contact parameter.

**Media security**

- **AEAD-AES-GCM SRTP/SRTCP (RFC 7714):** the `AEAD_AES_128_GCM` / `AEAD_AES_256_GCM` suites are
  implemented end to end and offered **preferred** in the DTLS-SRTP `use_srtp` negotiation, with
  AES-CM-128 kept as the interoperable fallback. Browser interop never depended on it — Firefox was
  verified negotiating AES-CM before GCM landed. See [SRTP/SRTCP](guides/srtp-srtcp.md).
- **SRTCP auth tag:** an 80-bit tag for every suite (RFC 4568 §6.2) — fixes RTCP interop with
  libsrtp-based peers when `AES_CM_128_HMAC_SHA1_32` is negotiated. SDES over insecure signalling is
  warned about, with an opt-in `RequireSecureSignalingForSdes` enforcement.
- **Recording encryption streams in constant memory:** an AES-GCM-HKDF STREAM construction encrypts in
  fixed-size chunks with a per-file HKDF-derived key, so recordings of any length no longer load whole
  into memory — and the long-term key never reuses a (key, nonce) pair across files.
- **Security hardening:** RTP SSRC/sequence/timestamp seeded from a CSPRNG (RFC 3550), the
  symmetric-RTP latch no longer re-points media at an unauthenticated source (the CVE-2017-14099
  pattern), SRTP master key material is wiped after derivation, and jitter/loss/RTT run off a
  monotonic clock. On the STUN/TURN/ICE side: CSPRNG DNS-SRV transaction ids (RFC 5452), an
  over-length STUN body is rejected instead of truncated, and short-term credentials match strictly
  on username **and** type. SAN matching parses ASN.1 directly and the TLS certificate load is
  thread-safe.

**Media transport and SIP hardening**

- **RTP/RTCP port-pair reservation:** media binds an even RTP port and reserves its RTCP successor
  (RFC 3550 §11), closing the race where the RTCP port could be taken between deriving and binding it.
- **SIP fixes** found by the source audit: re-ACK on a retransmitted 2xx of a confirmed dialog
  (RFC 3261 §13.2.2.4); digest retry on the session-timer UPDATE and SUBSCRIBE refresh paths; a
  correctly formed `+sip.instance` contact parameter (RFC 5626 §4.1); and the INVITE auth retry no
  longer adopts the 401's To-tag (§12.1.2) — which had made **all authenticated outbound calls** fail
  with `481` against strict registrars. Plus dialog route-set routing (§12.2.1.1), dialog identity
  matching (§12.2.2), `received=`/`rport=` handling (RFC 3581), strictly in-order PRACK (RFC 3262 §4),
  SSRC-collision handling and randomized RTCP intervals (RFC 3550 §6.3.1/§8.2), precise
  transport-failure classification, 488 on an unanswerable re-INVITE/UPDATE offer (RFC 3264),
  `Via`-branch-strict response matching (§17.1.3), registrar DNS off the inbound dispatch thread,
  bounded `Dispose` joins, WebSocket listener bind retry, and a redirect fan-out cap.
- **TURN:** Send indications are no longer required to be authenticated (RFC 8656 §10), which had
  rejected RFC-conformant third-party clients.
- **SDP & media-file hardening:** `rtcp-mux` is only answered when offered (RFC 5761), bandwidth lines
  keep their original type token (no TIAS→AS corruption), an SDP missing mandatory lines or a usable
  `c=` is rejected instead of defaulting to loopback, RTX payload types stay within the 7-bit range,
  MP3 passthrough handles ID3v2 tags, ffmpeg is killed on cancellation, and the WAV parser tolerates
  partial reads.
- **Core & audio hardening:** constructor-leak fix, thread-safe peer events, a monotonic rate clock, a
  real `WebRtcClient` teardown, playback-cancellation leak and recording-writer race closed,
  `Call.Dispose` awaits the BYE before tearing down the channel, hold events fire only on a real
  change, a `LineState` rule table, a transfer can no longer wedge the call in `Transferring` on a
  signalling failure, no media-session leak when a call terminates during ICE selection, G.722 is
  transcoded statefully across frames, and Windows/Linux audio parity (mute gate, drop-oldest,
  playback metrics, no hot-path allocation) with shared PCM/codec helpers. The weak-crypto analyzers
  (CA5350/CA5351) are no longer suppressed solution-wide.

**Interop and CI**

- **Asterisk** runs the full matrix with **no skipped cases**, and a **two-leg bridged-call** suite
  verifies bidirectional, byte-exact media through the PBX, alongside two-leg scenario tests (DTMF,
  hold/unhold, attended transfer, codec-mismatch transcoding) and a concurrent-call soak.
- **FreeSWITCH** — a second PBX runs the **two-leg scenario matrix** behind a shared `IPbxFixture`
  abstraction (local-first, not in the CI gate; narrower than the Asterisk matrix). See the
  [FreeSWITCH page](interop/freeswitch.md).
- **Two new per-PR CI gates:** a **chaos/fault-injection gate** (transport loss, malformed packets,
  signalling outage, resource churn → graceful degradation, recovery, no leak) and a **performance
  gate** on the SRTP crypto hot path.
- **Comparison & capacity evidence:** scenario-by-scenario comparison against another stack, plus a
  quality-gated capacity benchmark ramping to thousands of calls against a real Asterisk. Both run
  outside regular CI.

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
