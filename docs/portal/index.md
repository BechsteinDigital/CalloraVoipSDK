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

**Available on [nuget.org](https://www.nuget.org/packages/CalloraVoipSdk) — current release: v3.1.1.**

**What works today:**

| Capability | Status |
|-----------|--------|
| SIP Register / Dial / Accept / Hangup | ✅ Production-ready |
| Hold / Unhold / Blind + Attended Transfer | ✅ Production-ready |
| RTP/SRTP media transport | ✅ Production-ready |
| Adaptive jitter buffer | ✅ Production-ready |
| Media cross-connect / bridge | ✅ Production-ready |
| Per-call media tap (frame receivers/senders for bots and streaming) | ✅ Production-ready |
| Module registry (`client.Modules`) as plugin extension point | ✅ Production-ready |
| Configurable audio codec preference | ✅ Production-ready |
| DTMF send/receive (RFC 4733) | ✅ Production-ready |
| RTCP quality metrics (measured jitter, loss, round-trip time) | ✅ Production-ready |
| Recording + Playback (WAV/MP3) | ✅ Production-ready |
| Linux + Windows audio devices | ✅ Production-ready |
| Runtime device hot-switch + controls | ✅ Production-ready |

**In progress / planned:**

| Capability | Status |
|-----------|--------|
| ICE (NAT traversal; STUN/TURN transport in place) | 🔧 In progress |
| Backend/API for signed plugin marketplace + tenant entitlements | 📋 Roadmap |

## SDK Structure

**CalloraVoipSdk.Core** — Sovereign calling foundation

Clean DDD architecture: Domain → Application → Infrastructure → public `VoipClient` facade.
No vendor lock-in. Full protocol stack owned in-house (SIP, RTP, SRTP, SDP, STUN).

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
using var client = new VoipClient(new SdkConfiguration
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

[→ Getting Started Guide](guides/getting-started.md)
