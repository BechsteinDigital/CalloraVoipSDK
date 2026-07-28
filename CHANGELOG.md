# Changelog

All notable changes to this project are documented in this file.

The format is based on Keep a Changelog and this repository follows Semantic Versioning (SemVer).

## [Unreleased]

### Added
- **Local ICE restart initiation (RFC 8445 §9)**: `ICall.RestartIceAsync()` lets the application
  restart ICE itself — new credentials on the **existing** socket, role preserved — instead of only
  *detecting* a restart the peer initiated. Pairs with the consent-loss signal on
  `ICall.IceConnectionStateChanged`, so an app can react to a dead media path and repair it.
- **RTP/RTCP port-pair reservation**: media now binds an **even RTP port with its RTCP successor
  reserved** (RFC 3550 §11), via pre-bound socket seams through `RtpSession`, the RTCP monitor and
  `VideoRtpStream`. This removes the race where the RTCP port could be taken between deriving and
  binding it; `rtcp-mux` keeps using the single muxed port.
- **AEAD-AES-GCM SRTP/SRTCP (RFC 7714)**: the `AEAD_AES_128_GCM` and `AEAD_AES_256_GCM` suites are
  implemented end to end — AEAD crypto core, SRTP and SRTCP cipher strategies, and DTLS-SRTP
  `use_srtp` negotiation where **GCM is offered preferred** (GCM-128 ahead of GCM-256, the de-facto
  WebRTC choice) with `AES_CM_128_HMAC_SHA1_80` kept as the interoperable fallback. AEAD suites use a
  12-byte salt and carry no separate HMAC auth key (§8.1). SRTCP-GCM additionally carries a
  DoS guard on malformed input.
- **SIP `PUBLISH` soft-state lifecycle (RFC 3903 §4/§6, CF-066b)**: `RefreshPublicationAsync`,
  `ModifyPublicationAsync` and `RemovePublicationAsync` drive an existing publication via
  `SIP-If-Match`, on `IVoipClient` and on `IPhoneLine`. Until now only the initial `PublishAsync`
  was reachable from the facade, so a publication could not be kept alive, changed or withdrawn.
- **WebRTC browser interop (Chrome), previously unvalidated**: the `CalloraVoipSdk.WebRtc`
  facade is now exercised end-to-end against a **real headless Chrome** (Playwright) — signalling →
  ICE → DTLS-SRTP → SRTP, with **bidirectional Opus audio** and **VP8 video decoded by the browser**
  (`framesDecoded > 0` on the SDK→browser path). Both directions are covered: the SDK as offerer
  (browser answerer) and the **browser as offerer** (SDK answerer). These tests run in the PR CI
  gate as a dedicated `BrowserInterop` job.
- **WebRTC mDNS ICE candidates (RFC 8828)**: `.local` host candidates from a browser are now
  resolved via an `IMdnsResolver` seam (default `SystemMdnsResolver` over `System.Net.Dns`) instead
  of being dropped, with the RFC-mandated single-label / single-address / fail-safe rules.
- **WebRTC bundle video RTCP feedback and repair (Issue #14/#7)**: the BUNDLE media path now runs
  NACK/PLI/FIR key-frame recovery (RFC 4585 / 5104) and **RTX** (RFC 4588) — it sends NACK/PLI on
  detected inbound video loss, retransmits lost outbound packets as RTX, and recovers the peer's
  RTX. Inbound PLI/FIR is surfaced as a public `VideoKeyFrameRequested` event on `IPeerConnection`.
- **WebRTC transport-wide congestion control (transport-cc, RFC 8888 / draft-holmer)**: one
  transport-wide sequence counter, controller and feedback sender across every MID on the bundle,
  with the receive-side feedback interval adapted to the inbound bitrate (libwebrtc `[50, 250] ms`
  policy) rather than a fixed period.
- **WebRTC video stats (getStats)**: `NackCount`, `PliCount`, `FramesDropped` and
  `AvailableOutgoingBitrateBps` on `WebRtcStats` are now wired to the bundle video RTCP-feedback and
  transport-cc subsystems (previously null). `FirCount` stays honestly null (the SDK emits no FIR).
- **Public call termination reason (Issue #103)**: `ICall.TerminationReason`
  (`CallTerminationReason`: `SipStatusCode`, `ReasonPhrase`, `Category`, `TerminatedBy`,
  `RetryAfterSeconds`), a protocol-neutral end cause so consumers can tell a busy, unanswered,
  cancelled or rejected call apart from a generic failure. Classification is driven by the
  authoritative SIP response status (RFC 3261 §21), not the Q.850 `Reason` header (matching PJSIP /
  SIPSorcery / Twilio).

### Fixed
- **SDP, media-file and security hardening (Issue #16)**:
  - *SDP offer/answer*: `rtcp-mux` is only answered when it was **offered** (RFC 5761 §5.1.1) instead
    of being asserted from local options; a bandwidth line is re-serialised with its **original type
    token** (`AS`/`TIAS`/…) rather than silently turning TIAS into AS; an offer missing a mandatory
    `v=`/`s=`/`t=` line, or a media description with no usable `c=`, is **rejected** instead of quietly
    defaulting to `127.0.0.1`; the static-payload-type fallback is bounded to the IANA range (0–34,
    RFC 3551 §6) so a dynamic PT can no longer be mis-matched by number; the RTCP port derivation no
    longer throws on port 65535; and RTX payload-type assignment stays within the 7-bit maximum (127),
    skipping RTX for a codec when no free PT remains.
  - *Media files*: the MP3 passthrough skips a leading ID3v2 tag and resynchronises to the first frame
    header; the ffmpeg process tree is **killed on cancellation** instead of leaking; the MP3
    transcoding writer is created through an async factory (no sync-over-async in the constructor);
    and the WAV header parser tolerates **partial reads** (`ReadExactly`).
  - *Security*: subject-alternative-name matching parses **ASN.1** directly instead of the
    locale- and platform-dependent text of `X509Extension.Format`; the TLS certificate load is
    double-checked under a lock (no duplicate `X509Certificate2` on a concurrent first use); and the
    recording encryption key is zeroed after use.
  - *Recording encryption rewritten to stream in constant memory* (`VREC2`): an AES-GCM-**HKDF
    STREAM** construction encrypts and decrypts in fixed-size chunks, so a recording of any length
    uses about one chunk of memory instead of being loaded whole. Each file draws a random salt and
    nonce prefix and derives a **per-file key with HKDF-SHA256**, so the long-term key never reuses an
    AES-GCM (key, nonce) pair across files; within a file each chunk carries a distinct nonce
    (prefix + chunk index + last-chunk flag), which binds chunk order and makes truncation detectable.
  - *WebRTC/common*: the media socket binds to the **address family of the configured
    `LocalEndPoint`**, so IPv6 endpoints work (it was hard-coded to IPv4); a DNS failure names the
    host that could not be resolved; and the scheduler only wakes its worker when the **head** entry
    is cancelled.
- **Client facade & audio-backend hardening (Issue #18)** — the whole checklist:
  - *Facade/DI*: the `VoipClient` constructor no longer leaks transport/registration/signalling/audio
    when construction fails midway; `AddCalloraWebRtc` validates `WebRtcOptions` on start (symmetric to
    the SIP side); `PeerConnection` event accessors are lock-guarded (no lost handlers under concurrent
    subscribe/unsubscribe); the rate clock is **monotonic** (`Stopwatch`) instead of
    `DateTime.UtcNow.Ticks`; `ConnectAsync` can no longer hang on an uncooperative trickle channel;
    `WebRtcClient` is `IAsyncDisposable` with a real teardown; argument validation is consistent
    (`DialAndWaitUntilConnectedAsync` now checks `line`/`targetUri`); forwarded events carry the facade
    as `sender`; the obsolete messages and the `WebRtcStats` docs are honest again.
  - *Audio backends*: Windows/Linux parity — Windows gained playback metrics and drop-oldest semantics
    (was drop-newest), `SetOutputVolume` respects mute, and `[SupportedOSPlatform]` is annotated; the
    Linux playback hot path no longer allocates per callback and the PortAudio init/terminate refcount
    is balanced; capture-path sends are observed instead of fire-and-forget; the Linux-only outbound
    codec adaptation and the dead `FramesPerBuffer` option were reconciled; shared PCM/codec helpers
    were extracted so resampling, codec resolution and G.722 are no longer duplicated per platform.
  - *Build*: the weak-crypto analyzers **CA5350/CA5351** and the cancellation-forwarding analyzer
    **CA2016** are no longer suppressed solution-wide.
- **Core application/domain hardening (Issue #17)** — the whole checklist: the `ICall` event contract
  is documented as **not buffered** (matching the implementation); an *initial* `Unregistered` no
  longer aborts a pending registration; default audio corrects itself when media parameters arrive
  late or change mid-call; the playback-session cancellation leak and the recording writer/teardown
  race are closed; `Call.Dispose` awaits the best-effort BYE (bounded) before disposing the channel;
  the inbound `Idle→Ringing` transition reaches the aggregate `CallManager.CallStateChanged`;
  `PhoneLineManager` unsubscribes its per-line handlers; `HoldStateChanged` fires only on an actual
  change; `CallMediaOrchestrator.Dispose` observes and logs teardown faults instead of discarding the
  `ValueTask`s; `LineState` gained a `LineStateRules` table; and the dead `CallErrorEventArgs` type was
  removed.
- **SIP stack hardening (#13)**, a batch of RFC-conformance and robustness fixes:
  - Transport-failure classification is now precise (`SipTransactionTransportException` only), so a
    non-transport error such as a failed PRACK no longer triggers candidate failover and a synthetic
    503.
  - A re-INVITE or UPDATE offer that cannot be answered is rejected with **488 Not Acceptable Here**
    (RFC 3264 §6 / RFC 3311 §5.2) instead of returning a fresh offer as the answer.
  - A response without a top-`Via` branch no longer matches a client transaction (RFC 3261 §17.1.3).
  - An explicitly configured transaction `Timeout` is honoured even when it equals 64×T1.
  - Trusted-registrar DNS resolution moved off the inbound dispatch thread, with bounded retry
    back-off — an inbound INVITE before the first registration no longer blocks the transport.
  - `Dispose` on the stream and WebSocket connections joins the receive loop with a bounded timeout
    instead of blocking indefinitely.
  - The WebSocket listener retries the bind on a fresh port, closing the TOCTOU window between the
    port probe and the actual bind.
  - Redirect fan-out is capped (a malicious 3xx with many Contacts can no longer expand into an
    unbounded chain of INVITE transactions); the UAS trust model is documented as an explicit design
    decision.
  - Assorted clean-ups: `Random.Shared` instead of `new Random()`, the inbound `User-Agent` is
    configurable, and dead code (redirect-Contact expression, identity transport normalisation,
    UAS-level `Max-Forwards` decrement) removed.
- **Convenience:** the registration auth failure reason is now read from the line state instead of a
  missable event, closing a race where `ConnectResult.Error` could stay null (F005b follow-up).
- **WebRTC answerer TURN relay (K1–K5)**: a controlled agent (SDK as answerer) behind a symmetric
  NAT can now use its own TURN relay fully. Inbound STUN checks are tagged with a receive-path
  `replyVia`, so consent, triggered checks and nomination follow the relay path role-agnostically
  (RFC 8445, like libwebrtc / pjnath / SIPSorcery), and the answerer proactively installs a TURN
  permission (RFC 8656 §9) for each offerer candidate IP so the inbound relay check is not silently
  dropped. Behaviour-preserving on direct paths (`replyVia = null`).
- **Media/RTP security hardening (Issue #14)**:
  - RTP SSRC, initial sequence number and initial timestamp are now seeded with a CSPRNG over the
    full 32-bit range (RFC 3550 §5.1/§8.1) instead of a non-crypto PRNG that never set the high bit.
  - The symmetric-RTP (comedia) latch no longer re-points outbound media at an unauthenticated new
    source — the CVE-2017-14099 / AST-2017-005 pattern. The latch runs **after** SSRC/sequence
    validation, and re-latching away from an established source only happens on a keyed (SRTP/DTLS)
    call.
  - Per-context SRTP master key material (both the DTLS-SRTP exporter block and the SDES inline
    key/salt) is now zeroed after session-key derivation instead of lingering on the managed heap
    for the session lifetime.
  - RTP loss detection, the jitter buffer, and the BUNDLE / SIP-path RTT (DLSR) now run off a
    monotonic clock rather than wall-clock time; transport-cc feedback is sent on a periodic timer
    rather than per packet.
- **STUN/TURN/ICE hardening (Issue #15)**:
  - The DNS-SRV query transaction id is now a CSPRNG value over the full 16-bit range (RFC 5452 §10)
    instead of a non-crypto PRNG, closing a theoretical DNS-spoofing window.
  - `StunMessageCodec` throws on a body over 65535 bytes instead of silently truncate-casting to a
    corrupt length word (RFC 5389 §6).
  - The short-term credential lookup now matches strictly on username **and** credential type, so a
    long-term entry can no longer be handed to a short-term MESSAGE-INTEGRITY check (whose HMAC key
    derives differently, RFC 5389 §10).
  - The RFC 7635 access-token second-fraction divisor is corrected to 2^16.
- **Call flow (Issue #103 / caller cancellation)**:
  - An outbound SIP rejection (e.g. 486/480/603) now returns a Terminated call carrying its
    `TerminationReason` instead of throwing and losing the call reference.
  - Cancelling `DialAsync` while the outbound INVITE is still ringing now sends a SIP **CANCEL**
    (RFC 3261 §9.1) and keeps the call reachable, instead of letting the peer ring until its own
    timeout; a connect-timeout during a ringing dial maps to `Timeout` (not `Failed`) and a caller
    cancellation to `Canceled`.

### Changed
- **Two new per-PR CI gates**: a **chaos/fault-injection gate** (CORE-011) injects transport loss,
  malformed and adversarial packets, a signalling outage and resource churn under fault, and asserts
  graceful degradation, recovery **and** no descriptor/resource leak; and a **performance gate** holds
  the SRTP per-packet crypto hot path above a catastrophic-regression throughput floor. Both run as
  their own bounded CI jobs, so a hung scenario fails loudly instead of stalling the run.
- **Comparison and capacity evidence**: a `CompetitorInteropTests` suite runs the same scenarios
  (hold, remote rejection, remote BYE recovery, PBX restart recovery, caller cancellation, termination
  reasons) against a comparison stack so behavioural differences are recorded rather than assumed, and
  a **quality-gated capacity benchmark** (`Category=Capacity`, ramping 64→4096 calls against a real
  Asterisk echo with a per-call/per-direction quality gate) establishes a machine-capacity envelope.
  The capacity load generator was calibrated against recorded evidence so the numbers describe the
  machine, not the harness. Both are deliberately **outside regular CI**; see
  [`docs/maintainers/capacity-quality-benchmark.md`](docs/maintainers/capacity-quality-benchmark.md).
- **Browser interop is a matrix, not a single engine**: the WebRTC browser suite runs the three
  scenarios (audio, VP8 video, browser-as-offerer) per engine behind a `BrowserEngine` abstraction —
  today **Chromium and Firefox**, both installed and executed in the CI `browser-interop` job. WebKit
  has an engine but skips when the browser is absent. *Measure-first result:* Firefox negotiates
  DTLS-SRTP with **AES-CM**, so the AEAD suites were never an interop blocker.
- **`InternalsVisibleTo` is documented as an intentional, audited design** (#17.14): each grant was
  verified load-bearing, and the rejected alternatives (making the shared types public, or duplicating
  them per assembly) are recorded in `AssemblyInfo.cs` — the internals stay internal and are shared
  narrowly with first-party assemblies only.
- **Interop coverage**: two-leg scenario tests over the bridged call (DTMF end-to-end, hold/unhold,
  attended transfer, codec-mismatch transcoding), a **concurrent-call soak** (N parallel bridged
  calls), and a PBX-agnostic **`IPbxFixture`** abstraction so the matrix can be run against further
  PBXs (FreeSWITCH — see below).
  - The two-leg **SRTP** content check is excluded from the PR CI job (trait
    `Category=InteropLocalMedia`): the double SRTP decrypt/re-encrypt at the bridge is not reliably
    measurable on shared CI runners. It remains a hard local check; the plain two-leg content check
    and the single-leg SDES tests stay in the CI gate.
- **FreeSWITCH interop**: a second PBX runs the full matrix through the `IPbxFixture` abstraction
  against a real FreeSWITCH container — register smoke, media matrix (RTCP, codec-mismatch, SDES),
  DTMF/hold/transfer, and a concurrent-call soak. (Local-first; excluded from the PR CI gate for the
  same Docker-networking reason as the browser-safe Asterisk path.)
- **WebRTC GA guardrails** — three declared limits are now loud diagnostics instead of silent traps
  (the full features remain post-GA):
  - A non-UDP (TCP/TLS) TURN transport passed to `CalloraWebRtcBuilder.WithTurnServer` /
    `WithIceServers` is rejected with an `ArgumentException` instead of being silently accepted while
    no relay candidate is gathered.
  - No common DTLS-SRTP profile now yields a descriptive error (naming GCM-only / Firefox interop,
    RFC 7714) instead of an anonymous `insufficient_security` alert.
  - A second `SetRemoteDescriptionAsync` (re-offer / ICE-restart) throws `InvalidOperationException`
    instead of silently overwriting the session (which leaked the transport and duplicated handlers).
- **WebRTC / TURN E2E against real servers**: the relay stack now has an end-to-end test against a
  real **coturn** server (Docker) — long-term-auth allocation and the answerer relay receive path —
  in addition to the in-process fake, and the **browser-interop** suite runs in the PR CI gate via a
  dedicated Playwright/Chromium job.

## [4.6.0-preview.3] - 2026-07-25

Adds early media, SIP MESSAGE, SIP PUBLISH and REFER progress reporting, and closes every
interop- and stability-critical finding of the full source audit. No breaking API changes.

### Added
- **Early media (RFC 3960, F011)**: a 180/183 response carrying SDP now starts a **receive-only**
  media session before the call is answered. New public surface: `IPhoneLine.OutboundCallRinging`
  (a pre-answer call handle while `DialAsync` is still blocking), `ICall.EarlyMediaSdp` (the early
  SDP), and DTMF in the early dialog (`ICall.SendDtmfAsync` while `Ringing` — IVR / AI outbound).
  Verified end-to-end against a real Asterisk (plain and SRTP-SDES early media).
- **SIP `MESSAGE` (RFC 3428, CF-066a)**: send and receive out-of-dialog instant messages —
  `VoipClient.SendMessageAsync(...)` and the `IVoipClient.IncomingMessage` event.
- **SIP `PUBLISH` (RFC 3903, CF-066b)**: publish event state (e.g. presence) via
  `VoipClient.PublishAsync(...)`, returning a `PublishResult` with the assigned SIP-ETag and
  granted lifetime. (The facade methods to refresh, modify or remove a publication follow in the
  next release.)
- **REFER transfer progress subscription (RFC 3515 / 6665, CF-045)**: an incoming REFER now carries
  an `IReferSubscription` (`TransferRequestedEventArgs.Subscription`) that reports the referred
  call's progress, with an auto-timeout bound to the session lifetime.

### Fixed
- **SIP:** re-ACK on a retransmitted 2xx of a confirmed dialog (RFC 3261 §13.2.2.4, #3) — a lost
  initial ACK no longer lets the UAS retransmit until timeout and tear the call down.
- **SIP:** digest retry on the refresh paths (#4) — a 401/407 on a session-timer refresh UPDATE
  (RFC 4028) no longer terminates a healthy dialog with BYE, and the SUBSCRIBE refresh
  (RFC 6665) retries with credentials; the artificial 60-second `Expires` clamp was removed and a
  refresh-delay floor prevents a busy loop.
- **SIP:** the `+sip.instance` contact parameter is emitted as a bare token (RFC 5626 §4.1, #5) —
  the parameter *name* is no longer quoted, which strict registrars could reject.
- **SIP:** the INVITE auth retry no longer adopts the To-tag of the 401 response
  (RFC 3261 §12.1.2) — this had made **all authenticated outbound calls** fail with
  `481 Call/Transaction Does Not Exist` against strict registrars such as Asterisk.
- **SRTP:** SRTCP uses an 80-bit auth tag for every suite (RFC 4568 §6.2, #6) — the 32-bit
  truncation of RFC 3711 §5.2 applies to SRTP only. Fixes mutual RTCP auth failures with
  libsrtp-based peers once `AES_CM_128_HMAC_SHA1_32` is negotiated.
- **SRTP:** SDES keying over insecure signalling now warns (RFC 4568 §7), with an opt-in
  `RequireSecureSignalingForSdes` that fails closed.
- **TURN:** Send indications are no longer required to carry MESSAGE-INTEGRITY (RFC 8656 §10, #7);
  they are permission-checked like ChannelData, which had rejected RFC-conformant third-party
  clients on the indication data path.
- **TURN:** a configurable `TurnServerOptions.PublicRelayAddress` (#8) replaces the silent
  loopback fallback that advertised an unreachable relay address in multi-host deployments.
- **Core:** a failed or cancelled transfer no longer wedges the call in `Transferring` (#9); the
  attended transfer gained the missing `Connected` state guard, and `CallStateRules` is back in
  sync with the API guards.
- **Core:** no media-session leak when a call terminates while ICE selection is still running
  (#10); a per-call generation counter also ensures only the newest negotiation installs a session.
- **Audio:** G.722 is transcoded statefully across frame boundaries (#11) — the ADPCM predictor
  state is no longer reset every 20 ms frame, removing audible artefacts.
- **RTP:** the media sockets' kernel receive buffer is no longer fixed at 8 KiB (#12) and is
  configurable, preventing kernel drops at video bitrates.
- **Media metrics:** late-arriving packets are no longer counted as unrecoverable loss (F002).
- **NAT:** the corrective re-REGISTER is applied on UDP only (F010) — over TCP/TLS the established
  connection carries the routing (RFC 5626), so rewriting the contact to the reflected SNAT address
  no longer breaks registration behind NAT.

### Changed
- **Interop coverage**: the Asterisk suite runs with **all cases green and none skipped** (early
  media unblocked). Added a **two-leg bridged-call** suite that verifies **bidirectional,
  byte-exact media** through the PBX — RTP counters both ways, local and remote RTCP quality, and
  byte-identical PCMU payload in both directions.

## [4.6.0-preview.2] - 2026-07-22

A large RFC-compliance and hardening release on top of the preview.1 WebRTC facade
(145 source files changed, ~9.9k insertions since preview.1). No breaking API changes.

### Added
- **Self-hostable STUN & TURN server**: `AddCalloraStunServer(...)` / `AddCalloraTurnServer(...)`
  server-hosting facade with TURN control over UDP/TCP/TLS (end-to-end covered), inbound
  FINGERPRINT validation on both servers, `DONT-FRAGMENT` on the relay socket, and EVEN-PORT
  + RESERVATION-TOKEN (RFC 8656 §7).
- **TURN relay lifecycle** (RFC 8656 §9/§12, CF-003): permission-refresh and channel-rebind
  keepalive loops that hold a real allocation alive across more than one lifetime.
- **RTCP quality on the WebRTC/BUNDLE media path** (RFC 3550, CF-004a–g): periodic Sender and
  Receiver Reports, per-SSRC reception statistics, RTT from RR/SR (§6.4.1), report-block paging
  without overflow loss, negotiated-clock-rate §A.8 jitter with NTP↔RTP SR extrapolation, and
  per-SSRC/MID quality snapshots surfaced on `WebRtcStats` (e.g. `JitterMs`); verified two-peer
  over real DTLS-SRTCP (CF-004g).
- **DTMF (RFC 4733 telephone-event) end-to-end on the WebRTC/BUNDLE path** (CF-007).
- **SHA-512-256 SIP digest authentication** (RFC 8760, CF-001), resolving a multi-challenge
  authentication deadlock; digest `qop=auth-int` (RFC 7616, CF-067a); RFC 5626 outbound `;ob`
  contact parameter (CF-067b).
- `THIRD-PARTY-NOTICES.md` — license attribution for all runtime dependencies.

### Fixed
- **SIP in-dialog routing** now follows the dialog route set (loose/strict, RFC 3261 §12.2.1.1)
  instead of the last response source; in-dialog digest signs the effective request-URI behind a
  strict router (CF-014).
- **Dialog identity matching** (§12.2.2, CF-013): the tag gate returns 481 on mismatch; a
  To-tag-less BYE no longer terminates the dialog.
- **`received=`/`rport=`** handling centralized in the transaction layer (§18.2.1 / RFC 3581,
  CF-040); a bare `;rport` reply now targets the real source port, not the sent-by port.
- **PRACK** is strictly in-order (RFC 3262 §4, CF-044); gaps are not acknowledged and chain
  faults propagate.
- **Digest nonce-count** coupled to the nonce (RFC 7616 §3.4, CF-042); the INVITE 422 retry
  increments `nc` instead of replaying it and raises Session-Expires/Min-SE (RFC 4028, CF-047).
- **SUBSCRIBE** uses the digest challenge selector (§22, CF-043); **SRV** records are chosen with
  weight randomization (RFC 2782/3263, CF-041); **REFER** emits active/progress NOTIFY
  (RFC 3515 §2.4.4, partial, CF-045).
- **Wire robustness**: multi-value header split respects `<…>`; tag extraction is LWS/quote/
  escape aware (§7.3.1/§25.1, CF-046); reason-phrase control-character hardening (§7.2, CF-067a).
- **DTMF timestamp cursor** advances on the SIP and BUNDLE paths, with an RFC-4733 §2.5.1.4 audio
  send-gate during tone playout.
- **SSRC collision** handling (RFC 3550 §8.2, CF-005: RTCP BYE + SSRC reseed); randomized RTCP
  interval (§6.3.1), teardown BYE (§6.6) and opaque CNAME (RFC 7022, CF-006).
- TURN-over-TLS end-to-end certificate handling made Windows-SChannel compatible.

### Changed
- **Testing & CI**: interop + soak + audit test package (L0–L3 harness, a living audit register,
  a REGISTER interop pass against a real Asterisk container; `SoakShort` on PRs, `SoakLong`
  nightly, `Interop` on Docker); Dependabot (NuGet + Actions) and CI hygiene.
- **Documentation & open-source readiness**: full source audit; README and DocFX portal aligned
  to it with honest maturity/interop status; versioned docs (4.5 / 4.6); maintainer docs;
  `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, PR/issue templates.

## [4.6.0-preview.1] - 2026-07-18

### Added
- **Public WebRTC facade (preview, transport-only)** — a signalling-neutral browser/peer surface that
  mirrors the four-level design of `VoipClient`, in the `CalloraVoipSdk.WebRtc` namespace:
  - **`WebRtcClient` / `IWebRtcClient`** (Level 1): zero-config `new WebRtcClient()` or DI via
    `AddCalloraWebRtc(...)`. `CreatePeer()` returns an `IPeerConnection` that runs ICE, DTLS-SRTP,
    BUNDLE and RTP/RTCP internally. The app owns signalling and the codec — the SDK packetises and
    moves bytes, it never encodes or decodes.
  - **Signalling happy path**: `IPeerConnection.ConnectAsync(IWebRtcSignaling, WebRtcRole)` drives the
    full RFC 8829 offer/answer over an app-owned channel and completes when connected — the WebRTC
    counterpart to `DialAndWaitUntilConnectedAsync`. The neutral primitives (`CreateOffer`,
    `SetRemoteDescriptionAsync`, `StartAsync`) remain for callers that drive signalling themselves.
  - **W3C track model**: `IPeerConnection.TrackReceived` surfaces inbound media as `RemoteTrack`
    (`Kind`, `StreamId` = remote `a=msid`, `TrackId`) carrying `EncodedFrame` (payload, RTP timestamp,
    key-frame flag, presentation-time seam). Grouping by `StreamId` keeps a participant's audio and
    video together; per-track delivery keeps them separable.
  - **L2 multi-peer manager**: `IWebRtcClient.Peers` tracks the live peer connections.
  - **L3 extension seams**: `IMediaTap` + `IPeerConnection.AttachMediaTap` observe media in both
    directions (recording/analytics/AI); `IWebRtcClientModule` + `IWebRtcClient.Modules` register
    facade plugins (programmatically or auto-attached from DI).
  - **Two-facade composition**: `AddCalloraVoip(sip => …).AddWebRtc(rtc => …)` configures the SIP and
    WebRTC facades in one chain; each facade owns its own options object.
  - Samples: `examples/CalloraVoipSdk.Sample.WebRtcPeer` (and further WebRTC samples) show connect,
    tracks, taps and DI end-to-end.
  - **Trickle ICE + early-bind**: `IPeerConnection.LocalIceCandidateDiscovered` /
    `AddIceCandidateAsync` / `GatherCandidatesAsync` (server-reflexive gathering from configured STUN)
    and the `IWebRtcTrickleSignaling` channel; `ConnectAsync` drives the trickle choreography when the
    signalling implements it, and the offer advertises `a=ice-options:trickle`. Early-bind gives even an
    ephemeral (port 0) client a live offer m-line — a fixed, reachable port is still recommended for NAT
    reachability without TURN.
  - **Send-side simulcast** (RFC 8853): `IPeerConnection.SendVideoFrameAsync(rid, …)` sends per-layer
    frames, negotiated with offerer-side answer confirmation (falls back to a single stream when the
    answerer does not confirm the rids). The active `rid` is surfaced to `IMediaTap.OnVideo` and to
    recording (RFC 8852). Receive-side RID demux is a later slice.
  - **Preview status**: the WebRTC surface has not yet been validated against real browsers (Chrome/
    Firefox); its API may change before it is declared stable. Data channels (SCTP) and TURN relay are
    not included.

### Changed
- **BREAKING (from 4.6): SIP-facade configuration types renamed** so each facade owns a facade-scoped
  name (parallel to `WebRtcConfiguration` / `WebRtcOptions` / `AddCalloraWebRtc`) and the `Callora*`
  names are freed for the upcoming composition layer:
  - `SdkConfiguration` → `VoipConfiguration`
  - `SdkOptions` → `VoipOptions`
  - `AddCallora(...)` → `AddCalloraVoip(...)`

  There are no compatibility aliases. **Migration**: rename these three symbols at your call sites
  (e.g. `services.AddCallora(o => …)` → `services.AddCalloraVoip(o => …)`; `new SdkConfiguration { … }`
  → `new VoipConfiguration { … }`). `VoipClient` and all other public types are unchanged; behaviour is
  identical.

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
