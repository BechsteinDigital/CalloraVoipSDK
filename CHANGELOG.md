# Changelog

All notable changes to this project are documented in this file.

The format is based on Keep a Changelog and this repository follows Semantic Versioning (SemVer).

## [Unreleased]

The **`4.7.0-preview`** line. Every build off `main` is versioned `4.7.0-preview` (the stable version is
pinned only by the release tag), so no build off `main` is mistaken for a stable release. The entries below
accumulate the consumer-visible changes for 4.7.0.

### Added

#### WebRTC facade (`CalloraVoipSdk.WebRtc`)
- **Multiple video tracks before connect** — `IPeerConnection.AddVideoTrack()` (and the
  `AddVideoTrack(VideoTrackOptions)` overload for direction/codecs/simulcast/stream id) adds a further video
  track — its own `m=video` line on the shared BUNDLE transport — before the first offer, returning an
  `IVideoTrack` handle to send frames on (`var cam = peer.AddVideoTrack(); await cam.SendFrameAsync(frame, ts);`).
  Each track carries its own SSRC, so a camera and a screen-share stay separable on the wire (RFC 3550 §8.1),
  and `RemoteTrack` now exposes its `Mid` so several remote video tracks are told apart on receive. New public
  types: `IVideoTrack`, `VideoTrackOptions`, `TrackDirection`. **Preview-grade / additive:** a peer that uses
  only `WebRtcConfiguration.EnableVideo` (and no `AddVideoTrack`) emits the byte-identical 1+1 SDP as before,
  and `SendVideoFrameAsync` still addresses the primary track. **Limitation:** tracks must be added *before*
  the offer is produced — mid-call add / renegotiation throws and is a later package. Multi-track stays under
  preview reifung until that lands.
- **`IPeerConnection.RequestVideoKeyFrameAsync`** — the receiving side can now actively request a fresh video
  key frame from the peer (RFC 4585 §6.3.1 PLI): for a newly attached renderer or a decoder reset, independent
  of the existing automatic loss-driven feedback. A tolerant no-op when no BUNDLE session is negotiated, the
  bundle has no video track, the peer did not advertise `nack pli`, or the built-in 500 ms throttle still
  holds. Additive — the existing 1 audio + 1 video API is unchanged.
- **RFC 8829 signalling state observation** — `IPeerConnection.SignalingState` and the
  `SignalingStateChanged` event surface the peer's offer/answer signalling state (W3C `RTCSignalingState`),
  distinct from the ICE/DTLS transport `State`. New public enum `SignalingState` (`Stable`, `HaveLocalOffer`,
  `HaveRemoteOffer`, `Closed` — no `pranswer` path in this SDK). The offerer runs
  `Stable → HaveLocalOffer → Stable` and the answerer `Stable → HaveRemoteOffer → Stable`, with an invalid
  transition (e.g. creating an offer after close) throwing `InvalidOperationException` instead of silently
  overwriting negotiation state. **Observation + guards only, additive:** the SDP/wire/session behaviour of the
  existing single offer/answer exchange is byte-identical; re-offer / renegotiation apply remains a later
  package (a second `SetRemoteDescriptionAsync` still fails loudly).

### Changed

#### Public API surface cleanup (preview-only namespace moves)
Public types that were exported from the `Core.Infrastructure.*` layer — a violation of the "infrastructure
stays an internal implementation detail" rule — were relocated to the `Core.Application.*` layer where the
public seam belongs. These are **consumer-visible namespace changes** (permitted while in preview); consumers
referencing these types must update their `using` directives:

- **SIP telemetry contract** moved from `CalloraVoipSdk.Core.Infrastructure.Sip.Observability` to
  **`CalloraVoipSdk.Core.Application.Observability`**: `ISipTelemetrySink`, `SipEventRecord`, `SipMetricRecord`,
  `SipCdrRecord`. The type shapes are unchanged; the built-in sink implementations remain internal
  infrastructure detail. `ITelemetryManager`/`TelemetryManager` event args now surface the new namespace.
- **`TlsConfiguration`** moved from `CalloraVoipSdk.Core.Infrastructure.Security` to
  **`CalloraVoipSdk.Core.Application.Ports.Security`** and is now a pure configuration DTO. The certificate
  behavior (`GetCertificate()`, `ValidatePeerCertificateSipDomain()`) that previously lived on the type is now
  handled internally by the SIP transport and is **no longer part of the public surface**. The DTO's data
  properties (`CertificatePath`, `CertificatePassword`, `AcceptUntrustedCertificates`, `ExpectedSipDomain`)
  and the `VoipConfiguration.Tls` / `VoipOptions.Tls` configuration flow are unchanged.

Additionally, two types that were public only by accident are now `internal`, matching their intended
implementation-detail status (the public seam is the corresponding interface):

- `AesGcmRecordingEncryptionProvider` (the public contract remains
  `CalloraVoipSdk.Core.Application.Media.IRecordingEncryptionProvider`).
- `SipDomainCertificateValidator` (an internal RFC 5922 helper of the TLS transport).

### Internal / in progress (not yet consumer-visible)
- **Multi-track: mid-call add / renegotiation is still missing.** SDP (offer + answer), the media runtime, and
  the public `AddVideoTrack` API now work together for tracks declared **before** the first offer (see *Added*).
  What remains before multi-track leaves preview reifung: adding/removing a track **after** the offer
  (renegotiation, RFC 8829), *N* audio tracks, and receive-side simulcast demux. Per the claim-gating policy
  (ADR-006 §5), multi-track is described honestly as "multiple video tracks before connect", not "done".

## [4.6.0] - 2026-07-28

The 4.6 line adds a **WebRTC facade** and a **self-hostable STUN/TURN server** on top of the SIP + RTP
core, and closes every interop- and stability-critical finding of a full source audit of the code base.

Highlights: WebRTC is validated end-to-end against **real browsers** (Chromium and Firefox) and a real
**coturn**; the SIP core runs a full interop matrix against a real **Asterisk** with zero skipped cases,
plus a second PBX (**FreeSWITCH**) and a two-leg bridged call verified **byte-exact in both directions**;
SRTP gained the **AEAD-AES-GCM** suites (RFC 7714); and CI grew **chaos/fault-injection** and
**performance** gates.

> **BREAKING (from 4.5):** the SIP-facade configuration types were renamed so each facade owns a
> facade-scoped name, parallel to `WebRtcConfiguration` / `WebRtcOptions` / `AddCalloraWebRtc`:
> `SdkConfiguration` → `VoipConfiguration`, `SdkOptions` → `VoipOptions`,
> `AddCallora(...)` → `AddCalloraVoip(...)`.
> There are **no compatibility aliases** — rename these three symbols at your call sites
> (e.g. `services.AddCallora(o => …)` → `services.AddCalloraVoip(o => …)`).
> `VoipClient` and all other public types are unchanged; behaviour is identical.

### Added

#### WebRTC facade (`CalloraVoipSdk.WebRtc`)
A signalling-neutral browser/peer surface that mirrors the four-level design of `VoipClient`. It is
**transport-only**: the SDK runs ICE, DTLS-SRTP, BUNDLE and RTP/RTCP and moves already-encoded frames —
your app owns the signalling channel and the codec.

- **`WebRtcClient` / `IWebRtcClient`**: zero-config `new WebRtcClient()` or DI via `AddCalloraWebRtc(...)`.
  `CreatePeer()` returns an `IPeerConnection`. `IWebRtcClient.Peers` tracks live peers, and the client is
  `IAsyncDisposable` with a real teardown.
- **Signalling happy path**: `ConnectAsync(IWebRtcSignaling, WebRtcRole)` drives the full RFC 8829
  offer/answer over an app-owned channel and completes when connected — the WebRTC counterpart to
  `DialAndWaitUntilConnectedAsync`. The neutral primitives (`CreateOffer`, `SetRemoteDescriptionAsync`,
  `StartAsync`) remain for callers that drive signalling themselves.
- **W3C track model**: `TrackReceived` surfaces inbound media as `RemoteTrack` (`Kind`, `StreamId` =
  remote `a=msid`, `TrackId`) carrying `EncodedFrame` (payload, RTP timestamp, key-frame flag).
- **Extension seams**: `IMediaTap` + `AttachMediaTap` observe media in both directions
  (recording/analytics/AI); `IWebRtcClientModule` + `IWebRtcClient.Modules` register facade plugins.
- **Two-facade composition**: `AddCalloraVoip(sip => …).AddWebRtc(rtc => …)` in one chain.
- **Trickle ICE + early-bind**: `LocalIceCandidateDiscovered` / `AddIceCandidateAsync` /
  `GatherCandidatesAsync` and the `IWebRtcTrickleSignaling` channel; the offer advertises
  `a=ice-options:trickle`. Early-bind gives even an ephemeral (port 0) client a live offer m-line.
- **mDNS ICE candidates (RFC 8828)**: `.local` host candidates from a browser are **resolved** through an
  `IMdnsResolver` seam (default `SystemMdnsResolver`) instead of being dropped, with the RFC-mandated
  single-label / single-address / fail-safe rules.
- **Video repair and congestion control on the BUNDLE path**: NACK/PLI/FIR key-frame recovery
  (RFC 4585 / 5104) and **RTX** (RFC 4588) — NACK/PLI on detected inbound loss, retransmission of lost
  outbound packets, recovery of the peer's RTX. Inbound PLI/FIR surfaces as the public
  `VideoKeyFrameRequested` event. **transport-cc** (RFC 8888) runs one transport-wide sequence counter,
  controller and feedback sender across every MID, with the feedback interval adapted to the inbound
  bitrate.
- **`getStats`**: `NackCount`, `PliCount`, `FramesDropped` and `AvailableOutgoingBitrateBps` on
  `WebRtcStats` are wired to the feedback and transport-cc subsystems. `FirCount` stays honestly `null` —
  the SDK requests key frames with PLI, not FIR.
- **Answerer TURN relay**: a controlled agent (SDK as answerer) behind a symmetric NAT can use its own
  relay fully — inbound STUN checks carry a receive-path `replyVia` so consent, triggered checks and
  nomination follow the relay path role-agnostically (RFC 8445), and the answerer proactively installs a
  TURN permission (RFC 8656 §9) per offerer candidate.
- **Send-side simulcast** (RFC 8853): `SendVideoFrameAsync(rid, …)`, offerer-confirmed against the answer
  with a single-stream fallback; the active `rid` reaches `IMediaTap.OnVideo` and recording (RFC 8852).
- **DTMF (RFC 4733) end-to-end** and **RTCP quality** (RFC 3550: periodic SR/RR, per-SSRC reception
  statistics, RTT from RR/SR §6.4.1, report-block paging, negotiated-clock-rate §A.8 jitter with NTP↔RTP
  extrapolation, per-SSRC/MID snapshots on `WebRtcStats`) on the BUNDLE media path.

#### Connectivity
- **Self-hostable STUN & TURN server**: `AddCalloraStunServer(...)` / `AddCalloraTurnServer(...)` with
  TURN control over UDP/TCP/TLS, inbound FINGERPRINT validation on both servers, `DONT-FRAGMENT` on the
  relay socket, and EVEN-PORT + RESERVATION-TOKEN (RFC 8656 §7). The relay lifecycle (§9/§12) runs
  permission-refresh and channel-rebind keepalives that hold an allocation alive beyond one lifetime.
  A configurable `TurnServerOptions.PublicRelayAddress` replaces the loopback fallback that used to
  advertise an unreachable relay address in multi-host deployments.
- **Local ICE restart initiation (RFC 8445 §9)**: `ICall.RestartIceAsync()` restarts ICE from the
  application — new credentials on the **existing** socket, role preserved — instead of only *detecting*
  a peer-initiated restart. With the consent-loss signal on `ICall.IceConnectionStateChanged` an app can
  now detect a dead media path **and** repair it.

#### Call control and signalling
- **Early media (RFC 3960)**: a 180/183 carrying SDP starts a **receive-only** media session before the
  call is answered. New surface: `IPhoneLine.OutboundCallRinging` (a pre-answer call handle while
  `DialAsync` is still blocking), `ICall.EarlyMediaSdp`, and DTMF in the early dialog (`SendDtmfAsync`
  while `Ringing` — IVR / AI outbound). Verified against a real Asterisk, plain and SRTP-SDES.
- **SIP `MESSAGE` (RFC 3428)**: send and receive out-of-dialog instant messages —
  `VoipClient.SendMessageAsync(...)` and the `IncomingMessage` event.
- **SIP `PUBLISH` (RFC 3903)** with the full soft-state lifecycle: `PublishAsync` plus
  `RefreshPublicationAsync`, `ModifyPublicationAsync` and `RemovePublicationAsync` (`SIP-If-Match`,
  §4/§6), on `IVoipClient` and on `IPhoneLine`.
- **REFER transfer progress subscription (RFC 3515 / 6665)**: an incoming REFER carries an
  `IReferSubscription` (`TransferRequestedEventArgs.Subscription`) reporting the referred call's
  progress, with an auto-timeout bound to the session lifetime.
- **Call termination reason**: `ICall.TerminationReason` (`CallTerminationReason`: `SipStatusCode`,
  `ReasonPhrase`, `Category`, `TerminatedBy`, `RetryAfterSeconds`) — a protocol-neutral end cause that
  tells a busy, unanswered, cancelled or rejected call apart from a generic failure. Classification
  follows the authoritative SIP response status (RFC 3261 §21), not the advisory Q.850 `Reason` header.
- **SHA-512-256 SIP digest authentication** (RFC 8760), resolving a multi-challenge deadlock; digest
  `qop=auth-int` (RFC 7616); RFC 5626 outbound `;ob` contact parameter.

#### Media security
- **AEAD-AES-GCM SRTP/SRTCP (RFC 7714)**: the `AEAD_AES_128_GCM` and `AEAD_AES_256_GCM` suites are
  implemented end to end — AEAD crypto core, SRTP and SRTCP cipher strategies, and DTLS-SRTP `use_srtp`
  negotiation where **GCM is offered preferred** (GCM-128 ahead of GCM-256) with
  `AES_CM_128_HMAC_SHA1_80` kept as the interoperable fallback. AEAD suites use a 12-byte salt and carry
  no separate HMAC auth key (§8.1); SRTCP-GCM adds a DoS guard on malformed input.
- **Recording encryption streams in constant memory** (`VREC2`): an AES-GCM-**HKDF STREAM** construction
  encrypts and decrypts in fixed-size chunks, so a recording of any length uses about one chunk of memory
  instead of being loaded whole. Each file draws a random salt and nonce prefix and derives a **per-file
  key with HKDF-SHA256**, so the long-term key never reuses an AES-GCM (key, nonce) pair across files;
  within a file each chunk carries a distinct nonce (prefix + chunk index + last-chunk flag), which binds
  chunk order and makes truncation detectable.

#### Media transport
- **RTP/RTCP port-pair reservation**: media binds an **even RTP port with its RTCP successor reserved**
  (RFC 3550 §11) through pre-bound socket seams, removing the race where the RTCP port could be taken
  between deriving and binding it. `rtcp-mux` keeps using the single muxed port.

#### Project
- `THIRD-PARTY-NOTICES.md` — license attribution for all runtime dependencies.

### Changed
- **Testing and CI**: two new per-PR gates — a **chaos/fault-injection gate** that injects transport
  loss, malformed and adversarial packets, a signalling outage and resource churn under fault and asserts
  graceful degradation, recovery **and** leak-freedom; and a **performance gate** holding the SRTP
  per-packet crypto hot path above a catastrophic-regression throughput floor. Both run as their own
  bounded jobs. Alongside them: the Asterisk interop job, a **browser-interop matrix** (Chromium +
  Firefox via Playwright), a **coturn** TURN E2E, `SoakShort` on PRs and `SoakLong` nightly.
- **Interop coverage**: the Asterisk matrix runs with **no skipped cases** — register (happy + failure),
  in/outbound calls with live RTP, codec negotiation, SRTP-SDES, DTMF, hold/unhold, blind and attended
  transfer, session timers, early media, TCP/TLS. A **two-leg bridged-call** suite verifies
  **bidirectional, byte-exact media** through the PBX (RTP counters both ways, local and remote RTCP
  quality, byte-identical PCMU payload), covering DTMF, hold, attended transfer and codec-mismatch
  transcoding; a **concurrent-call soak** runs N parallel bridged calls; and a PBX-agnostic `IPbxFixture`
  abstraction lets the **two-leg scenario matrix** (bridged media plain/SDES/transcoded, byte-exact
  content, RTCP, hold/unhold, attended transfer, DTMF, concurrent-call soak) run against
  **FreeSWITCH** as well — local-first, not in the PR gate, and narrower than the Asterisk matrix.
  - Run outside the PR CI gate by design: the two-leg **SRTP** content check (`InteropLocalMedia`) and
    the **FreeSWITCH** matrix (`InteropFreeSwitch`), both local-first.
- **Comparison and capacity evidence**: a comparison suite runs the same scenarios (hold, remote
  rejection, remote BYE recovery, PBX restart recovery, caller cancellation, termination reasons) against
  another stack so behavioural differences are recorded rather than assumed; a **quality-gated capacity
  benchmark** (ramping to thousands of calls against a real Asterisk echo with a per-call/per-direction
  quality gate) establishes a machine-capacity envelope, with a calibrated load generator. Both are
  deliberately outside regular CI — see
  [`docs/maintainers/capacity-quality-benchmark.md`](docs/maintainers/capacity-quality-benchmark.md).
- **Build**: the weak-crypto analyzers **CA5350/CA5351** and the cancellation-forwarding analyzer
  **CA2016** are no longer suppressed solution-wide.
- **`InternalsVisibleTo` is documented as an intentional, audited design**: the rejected alternatives
  (making the shared types public, or duplicating them per assembly) are recorded in `AssemblyInfo.cs` —
  the internals stay internal and are shared narrowly with first-party assemblies only.
- **Documentation**: full source audit; README and DocFX portal aligned to it with honest maturity and
  interop status; versioned docs; maintainer docs; `SECURITY.md`, `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, PR/issue templates.

### Fixed

#### SIP
- **Re-ACK on a retransmitted 2xx** of a confirmed dialog (RFC 3261 §13.2.2.4) — a lost initial ACK no
  longer lets the UAS retransmit until timeout and tear the call down.
- **The INVITE auth retry no longer adopts the To-tag of the 401 response** (§12.1.2). This had made
  **all authenticated outbound calls** fail with `481 Call/Transaction Does Not Exist` against strict
  registrars such as Asterisk — the most severe finding of the audit.
- **Digest retry on the refresh paths** — a 401/407 on a session-timer refresh UPDATE (RFC 4028) no
  longer terminates a healthy dialog with BYE, and the SUBSCRIBE refresh (RFC 6665) retries with
  credentials; the artificial 60-second `Expires` clamp is gone and a delay floor prevents a busy loop.
- **`+sip.instance`** is emitted as a bare token (RFC 5626 §4.1) — the parameter *name* was quoted, which
  strict registrars could reject.
- **In-dialog routing** follows the dialog route set (loose/strict, §12.2.1.1) instead of the last
  response source; in-dialog digest signs the effective request-URI behind a strict router.
- **Dialog identity matching** (§12.2.2): the tag gate returns 481 on mismatch; a To-tag-less BYE no
  longer terminates the dialog.
- **`received=`/`rport=`** handling centralised in the transaction layer (§18.2.1 / RFC 3581); a bare
  `;rport` reply targets the real source port.
- **PRACK** is strictly in-order (RFC 3262 §4); gaps are not acknowledged and chain faults propagate.
- **Digest nonce-count** coupled to the nonce (RFC 7616 §3.4); the INVITE 422 retry increments `nc` and
  raises Session-Expires/Min-SE (RFC 4028).
- **SUBSCRIBE** uses the digest challenge selector (§22); **SRV** records are chosen with weight
  randomisation (RFC 2782/3263).
- **Wire robustness**: multi-value header split respects `<…>`; tag extraction is LWS/quote/escape aware
  (§7.3.1/§25.1); reason-phrase control-character hardening (§7.2).
- **Transport-failure classification** is precise (`SipTransactionTransportException` only), so a
  non-transport error such as a failed PRACK no longer triggers candidate failover and a synthetic 503.
- A **re-INVITE or UPDATE offer that cannot be answered** is rejected with **488 Not Acceptable Here**
  (RFC 3264 §6 / RFC 3311 §5.2) instead of returning a fresh offer as the answer.
- A response **without a top-`Via` branch** no longer matches a client transaction (§17.1.3); an
  explicitly configured transaction `Timeout` is honoured even when it equals 64×T1.
- **Trusted-registrar DNS** resolution moved off the inbound dispatch thread with bounded retry back-off;
  `Dispose` on the stream and WebSocket connections joins the receive loop with a bounded timeout; the
  WebSocket listener retries the bind on a fresh port (TOCTOU).
- **Redirect fan-out is capped** — a malicious 3xx with many Contacts can no longer expand into an
  unbounded chain of INVITE transactions.
- **NAT**: the corrective re-REGISTER applies on UDP only — over TCP/TLS the established connection
  carries the routing (RFC 5626), so rewriting the contact to the reflected SNAT address no longer breaks
  registration behind NAT.

#### Media security
- **SRTCP uses an 80-bit auth tag for every suite** (RFC 4568 §6.2) — the 32-bit truncation of RFC 3711
  §5.2 applies to SRTP only. Fixes mutual RTCP auth failures with libsrtp-based peers once
  `AES_CM_128_HMAC_SHA1_32` is negotiated.
- **SDES over insecure signalling now warns** (RFC 4568 §7), with an opt-in
  `RequireSecureSignalingForSdes` that fails closed.
- **Symmetric-RTP latch hardened** — the comedia latch no longer re-points outbound media at an
  unauthenticated new source (the CVE-2017-14099 / AST-2017-005 pattern): it runs **after** SSRC/sequence
  validation, and re-latching away from an established source only happens on a keyed (SRTP/DTLS) call.
- **SRTP master key material is zeroed** after session-key derivation (DTLS-SRTP exporter block and SDES
  inline key/salt) instead of lingering on the managed heap.
- **RTP SSRC, initial sequence number and initial timestamp are seeded from a CSPRNG** over the full
  32-bit range (RFC 3550 §5.1/§8.1) instead of a non-crypto PRNG that never set the high bit.
- **TLS**: subject-alternative-name matching parses **ASN.1** directly instead of the locale- and
  platform-dependent text of `X509Extension.Format`; the certificate load is double-checked under a lock.
- **TURN-over-TLS** certificate handling made Windows-SChannel compatible.

#### TURN / STUN / ICE
- **Send indications are no longer required to carry MESSAGE-INTEGRITY** (RFC 8656 §10); they are
  permission-checked like ChannelData, which had rejected RFC-conformant third-party clients.
- **DNS-SRV transaction ids** use a CSPRNG over the full 16-bit range (RFC 5452 §10).
- **`StunMessageCodec` rejects a body over 65535 bytes** instead of truncate-casting to a corrupt length
  word (RFC 5389 §6); short-term credential lookup matches strictly on username **and** credential type;
  the RFC 7635 access-token second-fraction divisor is corrected to 2^16.
- **ICE termination race**: no media-session leak when a call terminates while ICE selection is still
  running; a per-call generation counter ensures only the newest negotiation installs a session.

#### Media and audio
- **G.722 is transcoded statefully across frame boundaries** — the ADPCM predictor state was reset every
  20 ms frame, producing audible artefacts.
- **The media sockets' kernel receive buffer** is no longer fixed at 8 KiB and is configurable,
  preventing kernel drops at video bitrates.
- **Loss detection, the jitter buffer and the BUNDLE / SIP-path RTT (DLSR)** run off a **monotonic
  clock** rather than wall-clock time; transport-cc feedback is sent on a periodic timer.
- **Late-arriving packets are no longer counted as unrecoverable loss.**
- **Windows/Linux audio parity**: Windows gained playback metrics and **drop-oldest** semantics (was
  drop-newest), `SetOutputVolume` respects mute, and `[SupportedOSPlatform]` is annotated; the Linux
  playback hot path no longer allocates per callback and the PortAudio init/terminate refcount is
  balanced; capture-path sends are observed instead of fire-and-forget; shared PCM/codec helpers replace
  the per-platform duplication of resampling, codec resolution and G.722.
- **Media files**: the MP3 passthrough skips a leading ID3v2 tag and resynchronises to the first frame
  header; the ffmpeg process tree is killed on cancellation; the MP3 transcoding writer is created
  through an async factory; the WAV header parser tolerates partial reads.

#### SDP
- **`rtcp-mux` is only answered when it was offered** (RFC 5761 §5.1.1) instead of being asserted from
  local options.
- **A bandwidth line keeps its original type token** (`AS`/`TIAS`/…) instead of silently turning TIAS
  into AS.
- **An offer missing a mandatory `v=`/`s=`/`t=` line, or a media description with no usable `c=`, is
  rejected** instead of quietly defaulting to `127.0.0.1`.
- The **static-payload-type fallback is bounded to the IANA range** (0–34, RFC 3551 §6); the RTCP port
  derivation no longer throws on port 65535; **RTX payload-type assignment stays within 127**, skipping
  RTX for a codec when no free PT remains.

#### Core and client
- **Transfer** can no longer wedge the call in `Transferring` on a signalling failure; attended transfer
  gained the missing `Connected` guard and `CallStateRules` is back in sync with the API guards.
- **Call flow**: an outbound rejection (486/480/603) returns a terminated call carrying its
  `TerminationReason` instead of throwing and losing the call reference; cancelling `DialAsync` while the
  INVITE is ringing sends a real **CANCEL** (§9.1) and keeps the call reachable; a connect timeout during
  a ringing dial maps to `Timeout` and a caller cancellation to `Canceled`.
- **Connect/dial result mapping**: `ConnectAsync` short-circuits on a terminal `LineState.Failed` and
  surfaces the auth error instead of waiting out the full timeout with a null error;
  `ConnectTimeout` bounds the entire dial-and-wait.
- **Lifecycle**: the `VoipClient` constructor no longer leaks transport/registration/signalling/audio on
  a mid-way failure; the playback-session cancellation leak and the recording writer/teardown race are
  closed; `Call.Dispose` awaits the best-effort BYE (bounded) before disposing the channel;
  `CallMediaOrchestrator.Dispose` observes and logs teardown faults instead of discarding the
  `ValueTask`s; `PhoneLineManager` unsubscribes its per-line handlers.
- **Events**: `PeerConnection` event accessors are lock-guarded; the rate clock is monotonic; forwarded
  events carry the facade as `sender`; the inbound `Idle→Ringing` transition reaches the aggregate
  `CallManager.CallStateChanged`; `HoldStateChanged` fires only on an actual change; the `ICall` event
  contract is documented as **not buffered**, matching the implementation.
- `LineState` gained a `LineStateRules` table; the dead `CallErrorEventArgs` type was removed.

## [4.5.0] - 2026-07-15

### Added
- **Public video API (transport-only)**: send and receive encoded video frames over the public
  facade. `client.Media.CreateVideoReceiver()` exposes inbound frames via
  `IVideoReceiver.FrameReceived` (a `VideoFrame`: encoded payload, RTP payload type, 90 kHz
  timestamp, key-frame flag); `client.Media.CreateVideoSender().SendAsync(VideoFrame)` injects
  outbound encoded frames. The SDK negotiates the video m-line, packetizes/depacketizes RTP
  (VP8, H.264) and moves the bytes — it **never encodes or decodes**. Bring your own codec.
- **Recommended outbound video bitrate**: `IVideoSender.RecommendedBitrateBps` and
  `NetworkQuality` (Good/Fair/Poor) are derived from transport-cc feedback, with a
  `RecommendedBitrateChanged` event so you can size your encoder to the network in one line
  (`sender.RecommendedBitrateChanged += (_, e) => encoder.SetBitrate(e.RecommendedBitrateBps)`).
- **Keyframe handling**: inbound frames carry `VideoFrame.IsKeyFrame` (VP8 P-bit, H.264 IDR);
  `IVideoSender.KeyFrameRequested` fires when the peer requests a fresh reference frame (RTCP
  PLI/FIR) so you can force an intra frame. On inbound loss the SDK reports it to the peer
  (Generic NACK + throttled PLI, RFC 4585) gated on the peer's advertised feedback. FIR is
  honoured on receive but not generated.
- **Default-video convenience**: `client.AttachDefaultVideoAsync(call)` /
  `DetachDefaultVideoAsync(call)` wire an application-supplied `IVideoDevice` (your codec
  package: capture + encode + decode + render) to the call, mirroring
  `AttachDefaultAudioAsync`. The device is resolved from dependency injection and receives the
  negotiated codec via `VideoConnectionParameters`; without a registered `IVideoDevice` the
  attach fails closed (the core ships no codec). Negotiated video parameters are readable on
  `ICall.MediaParameters.Video` (`CallVideoParameters`).
- **Example**: `examples/CalloraVoipSdk.Sample.VideoCalling` wires a video call over the public
  API only, including the bitrate hook against a marked `StubVideoEncoder` placeholder.

## [4.4.1] - 2026-07-11

### Fixed
- **Native Opus in the platform audio devices**: the Linux and Windows backends now decode and
  encode Opus (RFC 7587) natively at 48 kHz. Previously a call that negotiated Opus was silently
  mis-decoded as G.722 through `AttachDefaultAudioAsync` (unintelligible audio); Opus stays opt-in
  via `PreferredAudioCodecs`.

## [4.4.0] - 2026-07-11

Additive public-API capabilities closing developer-experience gaps from the `IVoipClient`
reachability analysis. No breaking changes.

### Added
- **Default SIP transport selection (CORE-016)**: `SdkConfiguration.DefaultTransport` /
  `SdkOptions.DefaultTransport` / `CalloraBuilder.WithTransport` with a public `SipTransport`
  enum (Udp/Tcp/Tls/Ws/Wss). Default stays UDP; lets TCP/TLS-only enterprise proxies pick the
  outbound transport instead of relying on a `sips:`/`;transport=` target URI.
- **Opt-in public media address (CORE-017)**: `SipAccount.PublicMediaHost` forces the SDP media
  connection line (`c=`) for CGNAT / static 1:1 NAT. Default (unset) keeps the auto-resolved,
  symmetric-RTP-friendly address unchanged.
- **ICE observability on `ICall` (CORE-018)**: `ICall.IceSnapshot` (`CallIceSnapshot`) exposes the
  final ICE state and selected local/remote candidate pair (RFC 8445) after selection.
- **Custom outbound headers + remote identity (CORE-019)**: `DialOptions.CustomHeaders` are now
  applied to the INVITE (protected headers and header-injection attempts are refused);
  `ICall.RemoteAssertedIdentity` (P-Asserted-Identity, RFC 3325) and `ICall.Diversion` (RFC 5806)
  surface read-only on inbound calls.
- **SRTP suite / SRTCP status (CORE-023)**: `CallMediaParameters.SrtpSuite` is now public and a new
  `IsSrtcpEncrypted` flag reports SRTCP protection (RFC 3711 §3.4); key material stays internal.
- **Raw RTP statistics (CORE-024)**: `ICall.RtpStatistics` (`CallRtpStatistics`) exposes raw
  RFC 3550 counters (SSRC, packet/octet counts, cumulative/fraction loss, interarrival jitter).

## [4.3.5] - 2026-07-10

Security and robustness fixes surfaced by an adversarial production-readiness review, plus
internal decomposition. No public API or breaking changes.

### Fixed
- **Stream framer memory-exhaustion DoS**: TCP/TLS/WS framing now enforces hard header/body
  limits instead of buffering an unbounded `Content-Length` or never-terminating header.
- **SIP-over-WebSocket handshake (RFC 7118)**: the client offers, and the server echoes, the
  `sip` subprotocol, so strict/WebRTC-adjacent SIP-WS servers no longer fail at the handshake.
- **TLS/WSS certificate validation**: outbound TLS and WSS now present the SIP **domain** for
  SNI and certificate name validation instead of the resolved IP, so standard certificates
  validate without `AcceptUntrustedCertificates`.
- **Trace log secret leak**: SDES SRTP keys (`a=crypto ... inline:`) and ICE passwords
  (`a=ice-pwd:`) are redacted in wire-trace logs by default.

### Changed
- **Versioning**: released assemblies now carry the git-tag version across PackageVersion,
  AssemblyVersion, FileVersion and InformationalVersion (previously stuck at the 0.9.0 fallback).
- **Release pipeline**: the package workflow runs the full test suite before packing/publishing.
- **Docs**: corrected overclaims — platform audio backends decode PCMU/PCMA/G.722 only (Opus is
  not transcoded by the device), thread-safety is qualified, `MediaFrame` carries encoded (not
  PCM) payload, and trace redaction is documented.

### Internal
- Decomposed three ~1000-line signaling/transport files into injected collaborators
  (`SipForkedInviteHandler`, `SipOutboundConnectionPool`, `SipCallSessionEventDispatcher`) with
  no behaviour change.

## [4.3.4] - 2026-07-10

Attended transfer is now RFC 5589 (REFER carrying an RFC 3891 `Replaces`). No breaking
changes — the public `ICall.AttendedTransferAsync` signature is unchanged.

### Fixed
- **Attended transfer now sends REFER with a `Replaces`** (RFC 5589 / RFC 3891): the REFER's
  `Refer-To` targets the consultation party and embeds a URI-escaped `Replaces` identifying the
  consultation dialog (`to-tag` = the target's tag, `from-tag` = ours). REFER/Replaces-capable
  PBXs (e.g. Asterisk, FreeSWITCH, 3CX) can now actually join the two dialogs. Previously a plain
  REFER without `Replaces` was sent, which such PBXs could not complete. It falls back to a plain
  REFER when the consultation dialog has no tags yet. Endpoints without any REFER-transfer support
  (e.g. a FRITZ!Box on PSTN legs) still reject the REFER — bridge the media instead.

## [4.3.3] - 2026-07-10

Documentation-only release — no code changes versus 4.3.2. Cut as a tag so the
restructured documentation portal is published to GitHub Pages.

### Documentation
- Restructured the documentation portal around a 7-section information architecture:
  Overview · Getting Started · Core Concepts · Guides · Interop · Production ·
  Commercial Modules (Architecture kept as a supplementary deep-dive section).
- Concept and guide pages grounded in the verified public API surface.
- Interop matrix states verification status honestly: only FRITZ!Box is marked verified
  (real interop test); sipgate/Asterisk/FreeSWITCH/3CX are configuration guidance, not
  yet formally verified.
- Commercial-module pages clearly marked "in development — not yet available".

## [4.3.2] - 2026-07-09

Documentation-only release — no code changes versus 4.3.1. Cut as a tag so the corrected
documentation portal is published to GitHub Pages.

### Documentation
- Corrected the ICE status row on the documentation portal to the released state (opt-in,
  unproven in production) instead of the outdated "STUN/TURN transport in place" wording.
- Added the GitHub Pages documentation link (badge + reference) to the README.

## [4.3.1] - 2026-07-09

Bug fixes from live calls and review, plus consumer-facing API documentation. No breaking changes.

### Fixed
- **Jitter estimator on a stalled RTP timestamp**: a burst of packets repeating the same RTP
  timestamp (comfort noise, or an audio-payload repeat) spiked the RFC 3550 §6.4.1 jitter estimate
  and ratcheted the adaptive playout delay to its cap mid-call via false late-drops. Such repeats
  are now treated as playout-redundant, so the estimator and delay stay stable.
- **Registration removal on stop/dispose** (RFC 3261 §10.2.2): the unregister used a fresh Call-ID
  and CSeq 1, which registrars did not recognise as removing the binding — the old binding lingered
  until expiry and could fork inbound calls into a dead second binding. It now reuses the
  registration's Call-ID and next CSeq (binding identity written under the same lock the unregister
  snapshot reads).
- **Best-effort hangup on `Call.Dispose`**: a faulted BYE during teardown was dropped as an
  unobserved task exception; it is now observed and logged.

### Documentation
- Documented the **event threading contract** on `ICall`/`IPhoneLine`/`VoipClient` (which thread
  each event fires on; handlers must not block or throw), the **`ICall` error contract** (which
  methods throw vs. return `CallActionResult`), and filled XML-doc gaps across the public consumer
  types.

## [4.3.0] - 2026-07-09

SDES/SRTP hardening across the whole media lifecycle — the SDK now offers SRTP itself, protects
RTCP, and rekeys on re-INVITE — plus two remotely triggerable receive-loop DoS fixes surfaced by
review. All additive — no breaking changes.

### Added
- **Offer SDES SRTP as the caller** (RFC 4568): outbound calls now advertise `RTP/SAVP` with an
  `a=crypto` line and engage SRTP end-to-end, instead of only answering SRTP on inbound calls.
  Gated by the SRTP policy (offered unless `Disabled`); a single suite (`AES_CM_128_HMAC_SHA1_80`)
  keeps the offer/answer key match unambiguous.
- **SRTCP** (RFC 3711 §3.4): a negotiated SRTP call now encrypts and authenticates its RTCP, not
  just its RTP. RTCP was previously sent in the clear even under SRTP. SRTCP derives its own session
  keys (KDF labels 3/4/5) independent of the RTP keystream.
- **SRTP rekey on re-INVITE** (RFC 3264 §8): a re-INVITE whose negotiated media changes (a fresh
  peer or local SDES key, endpoint, or codec) re-keys the running media session — both for our own
  hold/unhold and for an inbound re-INVITE. A re-INVITE that changes nothing does not churn media.

### Fixed
- **Hold/unhold no longer downgrades SRTP** to plain RTP: a re-INVITE on a running secure call
  re-advertises the live key (`RTP/SAVP` + `a=crypto`) instead of falling back to `RTP/AVP`.

### Security
- **Receive-loop DoS on malformed short packets**: a short RTP- or RTCP-looking datagram (below the
  SRTP/SRTCP minimum length) threw an uncaught `ArgumentException` that permanently terminated the
  media receive loop — a remotely triggerable denial of service of all inbound media. Both the RTP
  and the (new) SRTCP receive paths now drop such packets cleanly.

## [4.2.0] - 2026-07-09

Protocol-correctness fixes (including two real bugs surfaced while adding test coverage) and
peer call-quality reporting. All additive — no breaking changes.

### Added
- **Peer MOS in the quality snapshot** (RFC 3611 §4.7): RTCP-XR VoIP Metrics are now consumed, so
  `CallQualitySnapshot` carries the peer-reported listening- and conversational-quality MOS
  (`RemoteMosListeningQuality` / `RemoteMosConversationalQuality`, null when unavailable). The
  decoder already parsed XR; the quality monitor previously ignored it.

### Fixed
- **Expires precedence and responses** (RFC 3261 §10.2.1.1 / §10.3): the `Expires` header is no
  longer stripped from responses, and registration-lifetime selection now gives the Contact
  `expires` parameter precedence over the top-level `Expires` header. SUBSCRIBE lifetime now
  honours the 200 OK `Expires`.
- **Unbounded stale-nonce retry** (RFC 2617): a registrar answering `stale=true` repeatedly could
  spin the client into an endless REGISTER loop; stale retries are now bounded.
- **SHA-512-256 digest** (RFC 7616): it was advertised but never worked (.NET has no SHA-512/256
  primitive, so it always failed at the hash step) — it is no longer advertised, so a challenge for
  it is cleanly rejected instead of silently failing.

### Changed
- Digest authentication gained known-answer coverage for the MD5-sess and SHA-256-sess session
  variants (RFC 7616 §3.4.2). No behaviour change.

## [4.1.0] - 2026-07-09

Substantial ICE (RFC 8445) and consent-freshness (RFC 7675) work: bidirectional connectivity
checks on the shared media socket, built on the 4.0.0 candidate-gathering and check-list
foundation. ICE stays opt-in and all changes are additive — no breaking changes.

### Added
- **Inbound ICE connectivity checks** (RFC 8445 §7.3): incoming STUN Binding requests on the
  media 5-tuple are authenticated (MESSAGE-INTEGRITY), role conflicts resolved (§7.3.1.1),
  USE-CANDIDATE nomination honoured (§7.3.1.5), and answered with a verifiable Success or 487
  response — demultiplexed from RTP on the shared media socket (RFC 7983).
- **ICE role derivation and shared tie-breaker**: the controlling role is derived from the SDP
  offer/answer direction (offerer = controlling, RFC 8445 §5.1.1) rather than a fixed default, and
  the 64-bit tie-breaker (§5.2) is derived so both directions of an agent resolve a role conflict
  identically.
- **Consent freshness** (RFC 7675): periodic STUN consent checks on the nominated pair over the
  media socket with transaction matching; on consent loss the agent ceases media transmission
  (§5.1) while keeping the socket open for a possible ICE restart.
- **Triggered connectivity checks** (RFC 8445 §7.3.1.4): a confirming check is sent back to the
  peer-reflexive source of an accepted inbound check (§7.3.1.3).

### Changed
- `CallMediaParameters` gains an additive `IceControlling` flag (default preserves prior
  behaviour) carrying the derived ICE role from signalling into the media layer.

### Notes
- ICE remains opt-in. Remaining optional follow-ups (not required for the above to function):
  surfacing consent loss to the application for a terminate / ICE-restart decision, re-nomination
  onto a validated peer-reflexive path (media already adapts via symmetric RTP), and delayed-offer
  role edge cases (self-correcting via role-conflict resolution).

## [4.0.0] - 2026-07-09

SRTP-secured media and Opus, plus a round of protocol-correctness fixes and hardening on top
of the 3.2.0 NAT trunk work. One breaking change (the `DialOptions` namespace move) makes this
a major release; everything else is source-compatible for consumers that already set a password.

### Added
- **Opus codec** (RFC 7587) via the managed Concentus library — opt-in through
  `PreferredCodecNames = ["opus"]`, with a mirrored dynamic payload type, 48 kHz RTP clock, and
  a wire↔µ-law bridge transcoder. The default codec set is unchanged when Opus is not requested.
- **SRTP/SDES secured media** (RFC 4568 / RFC 3711): crypto-suite negotiation that answers with
  its own key, an encrypted RTP media path (AES-CM + HMAC-SHA1 auth tag), and tamper rejection.
- **RTCP-XR decoding** (RFC 3611): inbound Extended Reports are now parsed instead of skipped —
  the VoIP Metrics block (§4.7: loss/discard, burst/gap, RTT, MOS-LQ/CQ, jitter buffer) is
  surfaced as `RtcpExtendedReport`.
- **SDP `o=` session versioning** (RFC 4566 §5.2 / RFC 3264 §5): the origin line carries a stable
  session id and an incrementing version across offer/answer, hold and un-hold.

### Changed (BREAKING)
- `DialOptions` moved from `CalloraVoipSdk.Core.Application.Calls` to
  `CalloraVoipSdk.Core.Domain.Calls` so the Domain no longer depends on the Application
  layer. It is a Domain value object with no behavior change. Migration: replace
  `using CalloraVoipSdk.Core.Application.Calls;` with `using CalloraVoipSdk.Core.Domain.Calls;`
  (or fully qualify).

### Changed
- `SipAccount.Password` is now optional (was required). SIP authentication is challenge-driven
  (RFC 3261 §22), so a password is only needed to answer a `401`/`407`. Registration against a
  registrar that does not challenge now succeeds without one (e.g. IP-authenticated trunks);
  if a challenge arrives and no password is configured, registration fails with a clear,
  specific error instead of a generic rejection. Non-breaking: existing code that sets a
  password is unaffected.
- The jitter buffer now seeds its RTT estimate to 100 ms (was 0) so the adaptive playout floor
  has a budget before the first RTCP report; the first real RTCP RTT still replaces the seed
  outright, keeping convergence fast.

### Fixed
- A retransmitted out-of-dialog INVITE that carries the same top-Via branch is now treated as a
  retransmission rather than answered `482 Loop Detected` (RFC 3261 §8.2.2.2 / §17.2.3): only a
  differing branch on the same Call-ID/From-tag/CSeq is a merged request.
- Forked-INVITE handling failures (fork ACK/BYE) are logged at Warning instead of Debug, so a
  dangling forked leg is visible.

### Security
- SRTP context hardening: RTP header-length bounds checks (malformed packets are rejected, not
  mis-parsed), key material zeroed on dispose (RFC 3711 §9.4), and thread-safe protect/unprotect.

### Internal
- DDD layer hygiene (the Domain no longer references Application/Infrastructure), hot-path
  allocation reductions on the RTP receive and SIP stream-framing paths, ICE candidate selection
  moved off the SIP signaling thread, and a substantially expanded protocol regression suite
  (real Digest authenticator known-answers, dialog route set, RFC 4028 session timers, RTCP-XR).

## [3.2.0] - 2026-07-08

Inbound calls over a public SIP trunk (sipgate SIPconnect) behind NAT, without STUN —
verified end-to-end by a real call and packet capture: stable registration, symmetric-RTP
media, and in-dialog ACK/BYE traversal with a clean dialog teardown.

### Added
- SIP-trunk inbound over NAT: symmetric RTP (comedia) so media flows without STUN or ICE,
  a NAT-public contact learned from the registrar's `received=`/`rport=` (RFC 3261 §18.2.1 /
  RFC 3581), and inbound line matching by registrar peer or registered domain (not just the
  exact username) for trunk DIDs.
- Configuration surfaces (all with backward-compatible defaults):
  - `SipAccount.PublicSipHost` / `PublicSipPort` — manual public contact override.
  - `SipAccount.InboundNumbers` — DID whitelist for multi-line disambiguation.
  - `SipAccount.AcceptTrunkInbound` (default `true`) — opt out to a strict 1:1 username match.
  - `ReregisterOptions.RefreshRatio` / `MinRefreshInterval` / `MaxCorrectiveReregistrations`.
  - `SdkConfiguration.InboundMediaTimeout` (`0` disables) / `HangupHeldCallOnMediaSilence`.
- Media-inactivity timeout: a connected call whose inbound RTP goes silent is torn down as a
  NAT-safe fallback for a far-end BYE that never reaches our in-dialog Contact.

### Fixed
- Responses now echo the request's `Record-Route` header fields in order (RFC 3261 §12.1.1).
  Without this the peer's route set was empty and sent the ACK to our 2xx (and any BYE)
  straight to our Contact from an un-primed far-end node, which a restricted NAT drops —
  the confirmed root cause of ACK/BYE never arriving over the trunk.
- Registration refresh no longer churns: the effective lifetime is taken from our own binding
  (RFC 3261 §10.3) instead of the first `expires=` in a multi-binding Contact header, which
  counted down between polls and collapsed the refresh interval.
- ICE candidates are only advertised when the remote offer includes ICE (RFC 8445); an
  unsolicited ICE description made non-ICE peers send STUN to the RTP port and blocked media.

### Changed
- The NAT-learned public contact is published via a volatile immutable record (thread-safe
  cross-thread reads), corrective re-registrations are bounded per cycle to prevent a
  re-register storm on pathological NATs, and trusted-registrar DNS is resolved off the
  inbound-INVITE path.

## [3.1.2] - 2026-07-08

First real-world verification of the ICE gathering path (STUN against a public server
with an active call) and a rebuilt ICE test suite.

### Fixed
- ICE STUN gathering no longer fails with "address already in use": the binding query
  is sent through the call's reserved RTP media socket (SendTo/ReceiveFrom, socket
  ownership stays with the call) instead of binding a second socket to the media port.
- STUN server DNS resolution now picks an address matching the media socket's address
  family — hosts whose AAAA record resolves first (e.g. stun.l.google.com) failed with
  "address family not supported by protocol" from an IPv4-bound socket.

### Added
- Deterministic ICE agent test suite: candidate gathering (host/srflx/relay with
  fallbacks), pair selection including relay/srflx fallback and retry behavior, and
  all failure reasons. Loopback regression tests run a real STUN binding query over a
  reserved socket against the in-repo STUN server.

## [3.1.1] - 2026-07-08

### Fixed
- RFC 3550 jitter estimator: the arrival-time conversion to RTP units saturated on a
  double-to-uint overflow, so reported jitter converged to the frame interval (20 ms)
  on a clean link instead of ~0. Arrival units are now computed in integer math and
  truncated modulo 2^32 like every RTP timestamp.
- Round-trip time measured from RTCP receiver reports (LSR/DLSR) now feeds the adaptive
  jitter buffer; media metrics previously reported the static 60 ms initialization
  default as if it were a measurement.

## [3.1.0] - 2026-07-08

Hardening release driven by the first real-world interop test against an AVM Fritz!Box
(inbound call to an AI agent, G.711 µ-law passthrough).

### Added
- `SdkConfiguration.PreferredAudioCodecs` / `SdkOptions.PreferredAudioCodecs`: ordered
  audio codec preference by SDP encoding name ("PCMU", "PCMA", "G722"). Drives offers,
  answers and the primary codec of RTP sessions consistently; unsupported names are
  ignored, no match falls back to the SDK defaults.
- `ISdpNegotiator.TryParseMediaParameters(remoteSdp, localEndPoint, localOptions)`
  overload (default interface method, backwards compatible) honoring the codec preference.
- SIP wire trace diagnostics: every sent/received request and response is logged at
  Trace level with method/status, remote endpoint, transport, CSeq and full body (SDP
  appears verbatim); session termination logs now include the RFC 3326 reason.

### Fixed
- Advertised media address: a wildcard/loopback signaling bind is no longer advertised
  verbatim in SDP towards a non-loopback peer (peers dropped the call right after
  answering). The OS route towards the remote signaling endpoint is probed instead —
  no DNS involved — and RTP/RTCP now bind to the same resolved address.
- SDP codec negotiation matches static payload types (PCMU/PCMA/G722) listed on the
  m-line without rtpmap attributes (RFC 3551 defaults). Previously the answer to such
  offers contained only telephone-event and no audio codec, rejected with 488.
- Reliable provisional responses (RFC 3262) are only used when the INVITE carries
  `Require: 100rel`. Answering merely-supporting callers with `Require: 100rel` stalled
  the 200 OK behind PRACK retransmit timeouts — the caller kept ringing after accept.
- RTCP compound decoding skips unrecognized packet types per RFC 3550 §6.1 (e.g.
  RTCP-XR, type 207) instead of discarding the whole datagram — inbound sender/receiver
  reports from peers that append XR blocks are processed again.

## [3.0.0] - 2026-07-07

### Added
- Generic module registry as the SDK extension point: `IVoipClientModule`, `ModuleRegistry`, `IVoipClient.Modules`. Modules from separate packages register via DI (`IVoipClientModule` services before `AddCallora`) or programmatically via `client.Modules.Register(...)`; typed resolution via `Get<T>`/`TryGet<T>`. The `OnAttached` hook hands modules the owning client; modules only become resolvable after the hook completed.
- Per-call media tap pinned as a public, tested contract: parallel `IMediaReceiver` fan-out, detach/dispose isolation, `IMediaSender` injection and format discovery via `ICall.MediaParameters`. XML docs now state the blocking contract (`FrameReceived` runs synchronously on the media path; consumers must buffer).

### Changed
- `VoipClient` construction now disposes already created runtime resources when a module throws during `OnAttached`, then rethrows the original error.

### Removed
- **Breaking:** `ModuleOperationResult` removed (unreferenced since 2.0.0).

## [2.0.0] - 2026-07-07

### Removed
- **Breaking:** Unimplemented module facades removed from the public SDK surface: `IConferencingModule`, `IConferenceSession`, `IRealtimeModule`, `ICallRealtimeBridge`, `IAudioFrameStreamTransport`, `IWebSocketModule`, `IWebSocketAudioTransportModule` and related option/event types.
- **Breaking:** `IVoipClient`/`VoipClient` properties `ConferenceManager`, `RealtimeManager` and `WebSocketManager` removed. `ModuleManager` no longer exposes `Conferencing`, `Realtime`, `WebSocket` or their availability flags.
- **Breaking:** `SessionManager.ActiveConferences` removed.
- These features previously threw `ModuleFeatureUnavailableException` on every call; they return as separate plugin packages built on the public media API.

### Added
- `net9.0` and `net10.0` target frameworks in addition to `net8.0`.

### Fixed
- SRTP RFC 3711 IV derivation and auth handling.
- PRACK wait race during reliable provisional handling.
- CANCEL transaction branch handling; BYE is now sent when CANCEL loses the INVITE race.

## [1.0.2] - 2026-04-17

### Added (previously listed under Unreleased)
- `SdkOptionsValidator` with startup validation for DI-based configuration.

### Changed (previously listed under Unreleased)
- `AddCallora(...)` now validates options on startup (`ValidateOnStart`).
- `CalloraHostedService` now coordinates lifecycle through `VoipClient` runtime hooks.
- DI path no longer uses `SdkConfiguration.Services` escape-hatch.
- `RegisterAndWaitAsync(...)` is now marked obsolete in favor of `ConnectAsync(...)`.

### Deprecated
- `SdkConfiguration.Services` is deprecated and will be removed after `v1.0`.

## [0.9.0] - 2026-04-14

### Added
- DDD-based source layout under `src/Core`, `src/Client`, `src/Modules`, `src/Audio`, `src/Hosting`, `src/Licensing` and `src/Abstractions`.
- Convenience-first `VoipClient` managers and module facades (`ConferenceManager`, `PlaybackManager`, `RecordingManager`, `ModuleManager`, `SessionManager`, `DeviceManager`, `QualityManager`, `PolicyManager`, `TelemetryManager`).
- Full DI entrypoint (`AddCallora`, `IVoipClient`, builder overrides, hosted lifecycle wrapper).
- Updated docs and docfx configuration for the new structure.

### Fixed
- Namespace and project-reference mismatches after modular split.
- Architecture tests and documentation paths aligned to `src/Core`.
