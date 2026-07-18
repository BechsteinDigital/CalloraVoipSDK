# Changelog

All notable changes to this project are documented in this file.

The format is based on Keep a Changelog and this repository follows Semantic Versioning (SemVer).

## [Unreleased]

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
  - **Preview status**: the WebRTC surface has not yet been validated against real browsers (Chrome/
    Firefox); its API may change before it is declared stable. A configured media port is required on
    both peers until early-bind / trickle ICE lands. Data channels (SCTP), TURN relay and simulcast are
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
