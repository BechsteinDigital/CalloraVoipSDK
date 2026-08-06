# CalloraVoipSdk 4.8.0

**A stack-wide hardening release, plus mutual TLS per SIP line and a public PCM transcoding surface.**
4.8.0 closes a long series of protocol-layer review findings across every wire boundary — DTLS-SRTP,
STUN, TURN, RTP/RTCP, SDP, SIP and the WebRTC media plane — and adds two additive public capabilities.

The security work is defensive throughout: fail-closed decoding, constant-time comparisons, anti-amplification
and anti-slowloris deadlines, source binding on plaintext legs, and bounded wire-DoS caps at every parser and
listener. **The public API only grew** — `PublicApi.approved.txt` gained entries and lost none — so an existing
consumer that does not opt into the new configuration surfaces stays behaviour-identical. Every cap sits far
above any real signalling or media, so legitimate traffic is unaffected.

## Headline features

- **Mutual TLS per SIP line, with a certificate from memory (#183).** Outbound SIP over TLS/WSS now presents a
  client certificate when one is configured — sent only if the registrar asks for it (RFC 8446 §4.4.2), so
  behaviour against registrars that do not request one is byte-identical. Two lines to the *same* registrar can
  present *different* identities: the identity is bound to the line, keyed into the connection pool
  (`(transport, addr:port, identity)`), and stamped on every request the line originates (REGISTER, INVITE and
  in-dialog requests, MESSAGE, PUBLISH). Configure it in memory via `TlsConfiguration.ClientCertificate` (an
  `X509Certificate2` you own and dispose — the SDK never disposes it), or override per connect boundary with
  `ConnectOptions.LineTls`. Per-line domain identity and trust stay fail-closed (RFC 5922). See ADR-064.
- **Public PCM transcoding surface (#205).** `IAudioPayloadCodec` + `AudioPayloadCodecFactory` (in
  `CalloraVoipSdk.Audio.Abstractions`) let a server-side consumer transcode Opus / G.711 (A-law/µ-law) / G.722
  ↔ PCM16 without taking a direct dependency on Concentus or NAudio — neither appears in any public signature.
  The prototypical use case is an SFU that decodes N−1 legs to mix a phone participant into a WebRTC conference.
  I/O is PCM16 little-endian `byte[]`; one instance per stream direction. Shipped transitively via the
  `CalloraVoipSdk` meta-package — no new `PackageReference`. See ADR-065.

## Security hardening

- **DTLS-SRTP:** constant-time fingerprint comparison (RFC 8122), zeroed private-identity staging bytes, a
  stateless HelloVerifyRequest cookie before the amplified certificate flight (RFC 6347 §4.2.1), a handshake
  deadline (`DtlsHandshakeOptions.HandshakeTimeout`, default 20 s), an ordered single-writer egress with real
  error propagation, and — new in this release — **post-handshake association servicing**: a control-receive
  loop that notices a peer `close_notify`/alert, ends the association deterministically, and stops media from
  flowing under a keying channel the peer considers closed (RFC 8827 §6.5). See ADR-066.
- **STUN/TURN:** auth-response integrity binding, a stateless HMAC nonce (amplification cap), TCP/TLS slowloris
  deadlines on both servers, fail-closed `ALTERNATE-SERVER`/decode handling, and immediate teardown of a surplus
  TURN relay allocation (RFC 8656 §7/§3.9).
- **RTP/RTCP wire-DoS caps:** RTCP compound budgets, a transport-cc feedback expansion cap, depacketiser frame
  caps (with discard telemetry), and a BUNDLE reception-state cap — an authenticated peer can no longer exhaust
  process memory through compound floods, header-field expansion, unterminated frames or SSRC spray.
- **SIP / app-domain:** fail-closed ICE (no session without a validated candidate pair), an atomic per-line
  concurrent-call cap, bounded inbound connection/session/transaction admission with slowloris deadlines, and a
  redacted `SipCredentials.ToString()` (the cleartext password no longer reaches logs).
- **STUN wire fail-closed:** `StunMessageCodec.Decode` now rejects a whole malformed message rather than
  accepting a partial one, closing an auth-bypass primitive.

## Correctness fixes

- **Inbound audio carries its real RTP timestamp** (RFC 3550 §5.1) — an SFU no longer stamps forwarded audio at
  `0`.
- **SDP BUNDLE is grouped semantically** (RFC 5888/8843/9143), a **remote answer is validated against the local
  offer** (RFC 3264 §6 / RFC 8829), and the **offerer sends the codec the answer accepted** (RFC 3264 §6.1).
- **DTLS handshake timeouts are classified by wall-clock**, so a stalled handshake reports a timeout rather than
  a generic protocol error.

## Upgrading

- **No breaking change.** All new APIs are additive; a fixed configuration that does not use them is unaffected.
- **Deprecation:** `TlsConfiguration.AcceptUntrustedCertificates` is now an `[Obsolete]` alias for
  `TrustMode = SipTlsTrustMode.DangerousAcceptAnyChain`. It still compiles and behaves as before; migrate to
  `TrustMode` at your convenience.
- **Notable behaviour changes** (correct, but observable): a non-conforming STUN server that omits
  MESSAGE-INTEGRITY on a credentialed Binding success now triggers a safe host-only fallback; plaintext legs
  discard foreign RTP/RTCP; a 416 no longer downgrades `sips:` to `sip:`; a structurally mismatched SDP answer
  is rejected; and `SipCredentials.ToString()` prints `***` for the password. See `CHANGELOG.md` for the full
  list.

## Status notes

- Full ICE and the browser-facing WebRTC peer remain **opt-in and not yet browser-interop-certified** — this
  release hardens them but does not change that posture. Validate for your trunk/peer before enabling.
- The DTLS close-block work (#190/#191) is wired for the WebRTC/bundle media plane; the SIP media-session owner
  reaction to a peer close is a documented follow-up (a SIP leg ends via BYE, not a mid-call DTLS `close_notify`).

See [`CHANGELOG.md`](CHANGELOG.md) for the concise per-change entry, and
[`docs/adr/ADR-064-per-line-sip-mutual-tls.md`](docs/adr/ADR-064-per-line-sip-mutual-tls.md),
[`docs/adr/ADR-065-public-pcm-transcoding-surface.md`](docs/adr/ADR-065-public-pcm-transcoding-surface.md) and
[`docs/adr/ADR-066-dtls-post-handshake-association-servicing.md`](docs/adr/ADR-066-dtls-post-handshake-association-servicing.md)
for the architecture decisions.
