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

Latest stable package: **v4.5.0** on [nuget.org](https://www.nuget.org/packages/CalloraVoipSdk).
The **4.6 line is in preview** (this documentation): it adds the WebRTC facade, DTLS-SRTP and a
self-hostable STUN/TURN server on top of the stable SIP + RTP core.

> **How to read the status column.** *Stable* = mature, heavily covered by the RFC-oriented test
> suite, and the intended production surface. *Preview* = implemented but not yet validated against
> a broad interop matrix — validate for your environment first. The production-proven NAT path is
> symmetric RTP (comedia), which needs no ICE or STUN. Known gaps and interop defects are tracked
> openly in the [issue tracker](https://github.com/BechsteinDigital/callora-voip-sdk/issues).

**Core (SIP + RTP):**

| Capability | Status |
|-----------|--------|
| SIP Register / Dial / Accept / Hangup | ✅ Stable |
| Hold / Unhold / Blind + Attended Transfer | ✅ Stable |
| RTP media transport | ✅ Stable |
| SRTP + SRTCP media encryption (SDES, offer & answer; RFC 4568 / RFC 3711) | ✅ Stable |
| DTLS-SRTP media encryption (RFC 5763, opt-in) | 🧪 Preview |
| Adaptive jitter buffer | ✅ Stable |
| Media cross-connect / bridge | ✅ Stable |
| Per-call media tap (frame receivers/senders for bots and streaming) | ✅ Stable |
| Module registry (`client.Modules`) as plugin extension point | ✅ Stable |
| Configurable audio codec preference | ✅ Stable |
| DTMF send/receive (RFC 4733) | ✅ Stable |
| RTCP quality metrics (measured jitter, loss, round-trip time) | ✅ Stable |
| Recording + Playback (WAV/MP3) | ✅ Stable |
| Linux + Windows audio devices | ✅ Stable |
| Runtime device hot-switch + controls | ✅ Stable |
| Encoded video: send/receive, transport-cc bitrate recommendation, keyframe feedback ([transport-only](guides/video-calls.md)) | ✅ Stable (single-stream) |

**Preview / in progress:**

| Capability | Status |
|-----------|--------|
| WebRTC facade: peer connections, SDK-driven signalling, W3C tracks, media taps ([transport-only](guides/webrtc.md)) | 🧪 Preview (not browser-validated; no SCTP; UDP TURN only) |
| Self-hostable STUN / TURN server (RFC 5389 / 5766) | 🧪 Preview (validate against your clients) |
| ICE for NAT traversal (RFC 8445/7675: role + tie-breaker, check-list FSM, nomination, inbound/triggered checks, consent freshness, restart) | 🧪 Preview — opt-in, unproven in production |
| Backend/API for signed plugin marketplace + tenant entitlements | 📋 Roadmap |

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

[→ Getting Started](getting-started/install.md) · [Core Concepts](concepts/voipclient.md) · [Guides](guides/making-calls.md) · [Interop](interop/matrix.md) · [Production](production/lifecycle-dispose.md)
