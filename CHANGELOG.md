# Changelog

All notable changes to this project are documented in this file.

The format is based on Keep a Changelog and this repository follows Semantic Versioning (SemVer).

## [Unreleased]

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
