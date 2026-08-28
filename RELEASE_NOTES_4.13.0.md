# CalloraVoipSdk 4.13.0

**Inbound WebRTC audio can now be buffered before it is raised, so a consumer that mixes gets a steady
cadence instead of whatever the network delivered.** One new opt-in setting,
`WebRtcConfiguration.AudioReceivePlayoutDelayMs`, default 0 — every existing consumer is byte-identical to
4.12.0. Purely additive: one new public member, nothing removed or changed (verified against
`PublicApi.approved.txt`).

## Highlights

### A jitter buffer on the WebRTC receive path (#381)

Until now, packets arriving on a WebRTC audio track were raised the moment they came off the wire. For a
peer that **forwards** that is correct and should stay that way: the browser at the far end runs its own
jitter buffer (NetEQ), and a second one here would only add latency to the same job.

A peer whose consumer **mixes** is in the opposite position. It must produce one frame every frame interval
from whatever each source has delivered by then, and it cannot wait. Handed raw arrivals it reads a burst as
a single usable frame and the rest as silence.

**Opus DTX makes that the normal case rather than the exception.** A browser sends nothing at all while
nobody speaks, so the packets that follow a pause arrive together. The audible result is audio that cuts out
after every pause and returns a few seconds later — which is how this was found, in a two-party conference
between a browser and a telephone: phone to browser was clean, browser to phone stuttered.

Two things confirm the shape of the fix rather than merely the fix itself:

- The **SIP path in this SDK has always worked this way.** `RtpCallMediaSession` buffers arrivals and drains
  them from a playout loop. Only the WebRTC receive path did not.
- **Janus** reached the same design for its mixing plugin: put the packet in the jitter buffer on arrival and
  decode from the buffer on the participant's own cadence. A pure SFU needs no buffer; a mixer does.

### Using it

```csharp
var config = new WebRtcConfiguration
{
    // 0 (default) raises packets on arrival — correct for forwarding.
    // A mixing consumer sets a starting delay; the buffer adapts from there.
    AudioReceivePlayoutDelayMs = 60,
};
```

Buffering costs latency, which is why it is off by default: a forwarding peer would pay it for nothing.

### Behaviour, for anyone enabling it

| Behaviour | Why it is that way |
|---|---|
| One buffer per audio m-line, at that **track's** clock rate | 48 kHz Opus and 8 kHz G.711 in one bundle would otherwise be scheduled six times off |
| The buffer resets when the SSRC changes | A new source brings its own sequence space; without the reset it reads as wild reordering — a leg that goes silent after a renegotiation and returns seconds later |
| Packets due together are released together | A burst that arrived together is due together; releasing one per interval would rebuild the backlog the buffer exists to undo |
| A gap is passed on **as a gap**, never concealed | This layer carries encoded RTP and does not know the codec — repeating an Opus frame is an artefact. The SIP path conceals because it delivers into G.711, where repetition is benign and a silent gap is not |
| A throwing subscriber is logged, not fatal | One bad subscriber must not end the playout loop and take every other track on the session with it |

Arrival and playout both read `MonotonicClock`: a wall-clock step mid-call would otherwise corrupt the
schedule.

## Compatibility

Purely additive. One new public member (`WebRtcConfiguration.AudioReceivePlayoutDelayMs`); nothing removed or
changed, verified against `PublicApi.approved.txt`. With the setting left at its default of 0, sessions are
byte-identical to 4.12.0. SemVer: **MINOR**.

See [`CHANGELOG.md`](CHANGELOG.md) for the itemised list.
