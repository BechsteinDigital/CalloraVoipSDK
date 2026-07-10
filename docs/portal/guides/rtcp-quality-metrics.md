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

## Compound-packet tolerance

RTCP compound decoding tolerates unknown packet types (e.g. RFC 3611 XR blocks from peers
that send more than SR/RR), so a richer-than-expected report does not break parsing.

## Threading

`QualitySnapshotChanged` fires on the media/RTCP thread. Keep the handler
non-blocking — copy the values and hand off (see [Events](../concepts/events.md) and
[Threading](../production/threading.md)).

## Note on MOS

Peer MOS is only present when the remote endpoint emits RTCP-XR VoIP Metrics. Against
peers that send only plain SR/RR, jitter/loss/RTT are available but peer MOS may be
absent — treat it as optional.
