# Diagnostics

When a call misbehaves — one-way audio, a rejected register, a codec mismatch — the SIP
wire trace and the quality metrics are your two primary tools.

## SIP wire trace

Set the SDK's logging to **`Trace`** to capture the full SIP exchange, including SDP
bodies:

```csharp
new VoipConfiguration { LoggerFactory = loggerFactory };  // factory configured at Trace
```

The trace shows REGISTER/INVITE/200/ACK/BYE and, crucially, the `c=`/`m=`/`a=` SDP lines
you need to diagnose media problems (advertised address, negotiated codec, `a=crypto`).

> **Secrets are redacted:** SDES SRTP keys (`a=crypto ... inline:`) and ICE passwords
> (`a=ice-pwd:`) are masked as `<redacted>` in the trace, so wire logs are safe to ship to a
> central log system. The crypto suite name and codecs stay visible for diagnostics.

## Quality metrics

Subscribe to `QualitySnapshotChanged` (or poll `client.QualityManager`) to watch measured
jitter, packet loss, RTT and peer MOS live — see
[RTCP quality metrics](../guides/rtcp-quality-metrics.md). Rising jitter/loss localizes a
problem to the network path rather than signaling.

## Telemetry

`client.TelemetryManager` exposes diagnostic sinks/traces for wiring the SDK's signaling
observability into your own telemetry pipeline.

## A diagnosis checklist

| Symptom | First look at |
|---------|---------------|
| Register fails | Trace: the 401/403 and the `Authorization` retry; credentials/realm |
| One-way / no audio | Trace SDP `c=`/`m=` address vs. reachability; [NAT](../guides/nat-and-trunks.md) |
| Choppy audio | Quality snapshots: jitter/loss; codec mismatch in SDP |
| DTMF not detected | RFC 4733 negotiated in SDP; peer's expected DTMF mode |
| SRTP call fails | `a=crypto` present; policy `Required` vs. peer capability |

## Reproducing

Capture a `Trace` log of the failing call plus the quality snapshots around the failure —
that pair is usually enough to localize signaling vs. media vs. network. See
[Troubleshooting](troubleshooting.md) for concrete fixes.
