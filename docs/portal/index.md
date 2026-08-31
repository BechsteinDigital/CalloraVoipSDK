---
_layout: landing
---

# CalloraVoipSdk

**Build your own voice product on a sovereign telephony core.**

European B2B voice runtime for teams building calling, dialer, contact center,
or voice AI products — with full technical control over telephony, media path,
and intelligent decision logic.

## Built for

- PBX and UC vendors
- Contact center software makers
- Dialer and campaign tools
- CRM/Sales automation with calling
- Voicebot and AI agent platforms
- Fraud, spam and scam detection systems

## Current Status

Latest release: **v4.13.1** on [nuget.org](https://www.nuget.org/packages/CalloraVoipSdk) (this
documentation). 4.13.1 removes two seconds from every WebRTC call's DTLS handshake: the inbound source
filter no longer waits for ICE to nominate a pair before it will accept records from one ICE has already
authenticated. It builds on 4.13.0, which lets a consumer that **mixes** buffer inbound WebRTC audio before it is raised
(`WebRtcConfiguration.AudioReceivePlayoutDelayMs`, default 0 = raise on arrival): a mixer must produce a frame
every frame interval and otherwise reads an Opus-DTX burst as one usable frame plus silence, which a caller
hears as audio cutting out after every pause. It builds on 4.12.0, which added **simulcast when the SDK
answers** — an offered `a=simulcast:send` (the common
SFU topology, client offers and server answers) is confirmed in the answer with the received layers tagged by
RID, both directions SDK↔SDK media-proven — and **`ICall.DiversionChain`**, the full retargeting history of an
inbound call read from `History-Info` (RFC 4244) and `Diversion` (RFC 5806) alike. It builds on 4.11, which
closed the two limits the 4.10 line called out: the TURN relay data path is no longer
UDP-only — a relay candidate can carry media over a persistent **TCP/TLS** connection to the server (a stream
relay, RFC 8656 §12 ChannelData), unit-proven with browser/real-server interop tracked in the matrix — and a
WebRTC peer now **survives an ICE restart** in place (`CreateIceRestartOfferAsync`) instead of being disposed
and rebuilt. Simulcast is now negotiated in **both roles** (offerer since 4.11, answerer since 4.12); also
media-flow/silence monitoring on `ICall`, RFC 8285 two-byte header extensions and the AV1 Dependency Descriptor,
and SIP hardening.
All additive — no public API was removed or changed — on top of the multi-party WebRTC facade (4.7),
DTLS-SRTP with AEAD-AES-GCM, the self-hostable STUN/TURN server, and the stable SIP + RTP core.

> **How to read the status column.** *Stable* = mature, covered by the RFC-oriented test suite and
> by automated interop, and the intended production surface. *Opt-in* = shipped and tested, but off
> by default and not yet proven in production traffic — validate for your environment first. The
> production-proven NAT path is symmetric RTP (comedia), which needs no ICE or STUN. The SIP + RTP
> core is exercised by an **automated interop suite against a real Asterisk (PJSIP) container in
> CI** — calls, media, codecs, SRTP-SDES, DTMF, transfer, session timers, early media and TCP/TLS,
> plus a two-leg bridged call with byte-exact bidirectional media (currently all cases green, none
> skipped). The WebRTC path is validated in CI against **real Chromium and Firefox** and a real
> **coturn** relay (see the [interop matrix](interop/matrix.md)). Known gaps and interop defects are
> tracked openly in the [issue tracker](https://github.com/BechsteinDigital/callora-voip-sdk/issues).

**Core (SIP + RTP):**

| Capability | Status |
|-----------|--------|
| SIP Register / Dial / Accept / Hangup | ✅ Stable |
| Hold / Unhold / Blind + Attended Transfer | ✅ Stable |
| REFER transfer progress subscription (RFC 3515 / 6665) | ✅ Stable |
| Early media (RFC 3960): pre-answer receive-only media + DTMF in the early dialog | ✅ Stable |
| RTP media transport | ✅ Stable |
| SRTP + SRTCP media encryption (SDES, offer & answer; RFC 4568 / RFC 3711) | ✅ Stable |
| DTLS-SRTP media encryption (RFC 5763, incl. AEAD-AES-GCM per RFC 7714) | ✅ Stable |
| Adaptive jitter buffer | ✅ Stable |
| Media cross-connect / bridge | ✅ Stable |
| Per-call media tap (frame receivers/senders for bots and streaming) | ✅ Stable |
| Module registry (`client.Modules`) as plugin extension point | ✅ Stable |
| Configurable audio codec preference | ✅ Stable |
| DTMF send/receive (RFC 4733) | ✅ Stable |
| SIP MESSAGE send & receive (RFC 3428) | ✅ Stable |
| SIP PUBLISH: event-state publish / refresh / modify / remove (RFC 3903) | ✅ Stable |
| RTCP quality metrics (measured jitter, loss, round-trip time) | ✅ Stable |
| Recording + Playback (WAV/MP3) | ✅ Stable |
| Linux + Windows audio devices | ✅ Stable |
| Runtime device hot-switch + controls | ✅ Stable |
| Encoded video: send/receive, transport-cc bitrate recommendation, keyframe feedback ([transport-only](guides/video-calls.md)) | ✅ Stable (single-stream) |

**WebRTC and NAT traversal:**

| Capability | Status |
|-----------|--------|
| WebRTC facade: peer connections, SDK-driven signalling, W3C tracks, media taps ([transport-only](guides/webrtc.md)) | ✅ Stable — browser-validated (Chromium + Firefox, both roles) |
| WebRTC video repair & congestion control: NACK/PLI/FIR, RTX, transport-cc, `getStats` | ✅ Stable |
| Send-side simulcast (RFC 8853 / 8852) | ✅ Stable |
| Data channels (SCTP) | 🚫 Not included |
| Self-hostable STUN / TURN server (RFC 5389 / 5766 / 8656) | ✅ Stable — verified against coturn (UDP relay only) |
| ICE for NAT traversal (RFC 8445/7675: role + tie-breaker, check-list FSM, nomination, inbound/triggered checks, consent freshness, restart incl. `RestartIceAsync`) | ⚙️ Opt-in — off by default, unproven in production trunks |
| ICE-TCP candidates (RFC 6544) | 🚫 Not included (deliberate — trunk calls use symmetric RTP) |
| Backend/API for signed plugin marketplace + tenant entitlements | 📋 Roadmap |

**Multi-party / SFU enablement (4.7, [WebRTC guide](guides/webrtc.md)):**

All of these are **additive and transport-only** — a peer that uses none of them negotiates
byte-identical SDP to 4.6. The SDK stays a peer: it **forwards, it does not mix or transcode**.

| Capability | Status |
|-----------|--------|
| Multiple video tracks over one BUNDLE (`AddVideoTrack`, own `m=video` + SSRC per track, `RemoteTrack.Mid`) | ✅ Stable |
| Multiple audio tracks over one BUNDLE (`AddAudioTrack`, per-participant `a=msid`) | ✅ Stable |
| Mid-call renegotiation (RFC 8829): apply the track delta live, no transport / DTLS / ICE / SRTP rebuild | ✅ Stable |
| Signalling state observation (`SignalingState`, `SignalingStateChanged` — W3C `RTCSignalingState`) | ✅ Stable |
| Receive-side simulcast demux (RFC 8853 / 8852): per-RID reassembly, layer id on `EncodedFrame.Rid` | ✅ Stable (forwarding-only — layer choice is the app's) |
| Per-peer send-bitrate recommendation from transport-cc (`RecommendedOutgoingBitrateBps`, `RecommendedBitrateChanged`) | ✅ Stable |
| On-demand key-frame request per track (`RequestVideoKeyFrameAsync(mid)`, RFC 4585 §6.3.1 PLI) | ✅ Stable |
| ICE restart on a connected peer (`CreateIceRestartOfferAsync`, re-offer rotating the ICE ufrag) | ✅ Stable |
| TURN relay over TCP/TLS (stream relay, RFC 8656 §12 ChannelData) | ✅ Available (unit-proven; browser/real-server interop tracked in the matrix) |
| Audio mixing / transcoding (a real conference server) | 🚫 Not included — out of scope by design |

## Choose your integration depth

CalloraVoipSdk uses a progressive API. Start with managed workflows for registration,
dialing, default audio, playback and recording. When the product needs more control, the
same call remains available through typed `IPhoneLine`/`ICall` contracts, encoded media
receivers/senders, cross-connect, custom devices, telemetry and modules.

The boundary is deliberate: supported call, media and extension seams are public, while
transport/parser implementation types and arbitrary wire mutation remain internal.
[Choose the right depth →](concepts/progressive-api.md)

## SDK Structure

**CalloraVoipSdk.Core** — Sovereign calling foundation

Clean DDD architecture: Domain → Application → Infrastructure → public `VoipClient` facade.
No vendor lock-in. Full protocol stack owned in-house (SIP, RTP, SRTP, DTLS-SRTP, SDP,
STUN/TURN client **and** server) — no external SIP/RTP/ICE library.

**Commercial plugins** *(private feed, licensed separately — in development)*

The SDK core is open and free. Advanced capabilities ship as paid plugins on a private
feed, built on the public module registry and media-tap contract:

- **Callora.Realtime** — bridge call audio to realtime AI APIs (e.g. OpenAI Realtime)
  with pacing, backpressure and barge-in; the foundation for AI voice agents
- **Callora.WebSocket** — raw call-audio streaming over WebSocket
- **Callora.Privacy** — redaction, consent management, policy gates, audit trail
- **Callora.Risk** — spam/scam signals, call risk screening, PBX abuse prevention
- **Callora.Intelligence** — AMD, sentiment, transcription, local model integration

Interested in early access? Contact [info@bechstein.digital](mailto:info@bechstein.digital).

## Quickstart

```csharp
using var client = new VoipClient(new VoipConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent = "MySoftphone/1.0"
});

var connectResult = await client.ConnectAsync(new SipAccount
{
    Username = "1001",
    Password = "secret",
    SipServer = "pbx.example.com"
});

if (!connectResult.IsSuccess || connectResult.Line is null)
    throw new InvalidOperationException($"Connect failed: {connectResult.Status}");

var dialResult = await client.DialAndWaitUntilConnectedAsync(
    connectResult.Line,
    "sip:1002@pbx.example.com");

if (!dialResult.IsSuccess || dialResult.Call is null)
    throw new InvalidOperationException($"Dial failed: {dialResult.Status}");

await client.AttachDefaultAudioAsync(dialResult.Call);
await dialResult.Call.HangupAsync();
```

[→ Getting Started](getting-started/install.md) · [Progressive API](concepts/progressive-api.md) · [Core Concepts](concepts/voipclient.md) · [Guides](guides/making-calls.md) · [WebRTC](guides/webrtc.md) · [Interop](interop/matrix.md) · [Production](production/lifecycle-dispose.md) · [Capacity](production/capacity.md)
