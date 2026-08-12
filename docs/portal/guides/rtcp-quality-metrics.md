# RTCP quality metrics

The SDK measures call quality from RTCP and surfaces it as snapshots you can log, chart
or alert on. Values are **measured**, not estimated placeholders.

## Consuming snapshots

```csharp
call.QualitySnapshotChanged += (_, e) =>
{
    var s = e.Snapshot;
    // marshal off the media/RTCP thread before doing real work
    logger.LogInformation("jitter={Jitter} loss={Loss} rtt={Rtt} mos={Mos}",
        s.Jitter, s.PacketLoss, s.RoundTripTime, s.PeerMos);
};
```

`client.QualityManager` also exposes the current quality state for polling scenarios.

## What is measured

| Metric | Source |
|--------|--------|
| Jitter (local + remote) | RTCP SR/RR interarrival jitter |
| Packet loss | RTCP SR/RR loss fraction / cumulative loss |
| Round-trip time | Derived from SR/RR `LSR`/`DLSR` timestamps |
| Peer MOS | RTCP-XR VoIP Metrics (RFC 3611 §4.7) when the peer sends XR |

RTT feeds the adaptive jitter buffer, so the playout delay tracks real network
conditions.

> **Known limitation:** on a low-loss/loopback path, packets that arrive too late for
> playout can currently be counted as unrecoverable loss, so the loss figure may read higher
> than the true network loss. Treat loss as directional/trend data rather than an exact
> network-loss percentage until this is fixed
> ([issue tracker](https://github.com/BechsteinDigital/callora-voip-sdk/issues)).

## Raw RTP counters

Where the quality snapshot gives derived values (ms / %), `call.RtpStatistics` exposes the
underlying RFC 3550 counters for diagnostics and billing — SSRC identifiers, packet/octet
counts, cumulative and fraction loss, and interarrival jitter in RTP units. It is `null`
until the first RTCP reporting interval has produced counters.

```csharp
var rtp = call.RtpStatistics;
if (rtp is { } s)
    logger.LogInformation("ssrc={Ssrc} sent={Sent} recv={Recv} lost={Lost}",
        s.LocalSsrc, s.PacketsSent, s.PacketsReceived, s.CumulativePacketsLost);
```

## Compound-packet tolerance

RTCP compound decoding tolerates unknown packet types (e.g. RFC 3611 XR blocks from peers
that send more than SR/RR), so a richer-than-expected report does not break parsing.

## What the SDK speaks

The support matrix below is what the wire codec actually implements — not what RTCP as a whole
defines. A peer may send anything in this list and be understood; anything outside it is skipped
without disturbing the rest of the compound.

| Packet | PT / FMT | Receive | Send |
|---|---|:--:|:--:|
| Sender Report (SR) | 200 | ✅ | ✅ |
| Receiver Report (RR) | 201 | ✅ | ✅ |
| Source Description (SDES) | 202 | ✅ | ✅ CNAME only |
| Goodbye (BYE) | 203 | ✅ | ✅ |
| Application-defined (APP) | 204 | — | — |
| Extended Report (XR) | 207 | ✅ VoIP Metrics only | — |
| Picture Loss Indication (PLI) | 206 / 1 | ✅ | ✅ |
| Full Intra Request (FIR) | 206 / 4 | ✅ | ✅ |
| Generic NACK | 205 / 1 | ✅ | ✅ |
| Transport-wide CC feedback | 205 / 15 | ✅ | ✅ |

### Gaps worth knowing before you integrate

- **XR is receive-only, and only VoIP Metrics (BT=7).** RFC 3611 defines seven other block types
  (loss/duplicate RLE, packet receipt times, receiver reference time, DLRR, statistics summary);
  those are skipped over. The SDK never emits an XR, so a peer relying on our MOS or DLRR will not
  get one.
- **Transport-wide congestion control is the draft format, not RFC 8888.** What we speak is
  draft-holmer-rmcat-transport-wide-cc-extensions-01, RTPFB **FMT=15** — the format Chrome and
  libwebrtc use and what nearly every WebRTC endpoint negotiates. RFC 8888 CCFB is RTPFB **FMT=11**
  with a different body and is *not* implemented; a peer expecting CCFB receives nothing it can
  parse.
- **No REMB (PSFB FMT=15) and no TMMBR/TMMBN (RFC 5104).** Bitrate signalling is receive-side
  transport-cc plus the SDK's own estimate, surfaced as a recommended send bitrate. A peer that
  only signals bandwidth via REMB or TMMBR will not be heard.
- **No SLI or RPSI** (PSFB FMT=2/3). Key-frame recovery is PLI and FIR.
- **SDES carries CNAME only.** Other items (NAME, EMAIL, TOOL, …) are parsed on receive but never
  sent.

None of these is a defect against a promise — they are listed here so the promise is explicit.

## Threading

`QualitySnapshotChanged` fires on the media/RTCP thread. Keep the handler
non-blocking — copy the values and hand off (see [Events](../concepts/events.md) and
[Threading](../production/threading.md)).

## Note on MOS

Peer MOS is only present when the remote endpoint emits RTCP-XR VoIP Metrics. Against
peers that send only plain SR/RR, jitter/loss/RTT are available but peer MOS may be
absent — treat it as optional.
