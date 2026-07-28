# Machine-specific call-capacity benchmark

`CalloraCapacityBenchmarkTests` determines a capacity envelope for one concrete host, runtime,
Docker setup and benchmark profile. It is not a fixed Callora product limit. Calls are accumulated
in ascending stages against an Asterisk `Echo()` endpoint; the run stops at the first stage that
does not satisfy every quality gate.

The test is marked `SoakLong` and `Capacity`. It is deliberately excluded from regular PR and
release CI.

## Run

Docker and .NET 10 are required. The default profile starts at 64 calls, ramps to 4096, waits
10 seconds after each stage, and measures for 30 seconds:

```bash
dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 \
  --filter "Category=Capacity"
```

Use explicit levels for a small smoke or for a focused follow-up around a previously observed
boundary:

```bash
CALLORA_CAPACITY_LEVELS=64,128,256 \
CALLORA_CAPACITY_REPORT=/tmp/callora-capacity.json \
dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 \
  --filter "Category=Capacity"
```

The relevant environment variables are:

| Variable | Default | Meaning |
| --- | ---: | --- |
| `CALLORA_CAPACITY_START` | `64` | First automatically generated stage |
| `CALLORA_CAPACITY_CEILING` | `4096` | Highest automatically generated stage and SDK call guard |
| `CALLORA_CAPACITY_LEVELS` | unset | Explicit ascending comma-separated stages |
| `CALLORA_CAPACITY_SETUP_PARALLELISM` | `8` | Concurrent dial operations |
| `CALLORA_CAPACITY_REPETITIONS` | `1` | Measurements per stage |
| `CALLORA_CAPACITY_SETTLE_SECONDS` | `10` | Stabilization before each measurement |
| `CALLORA_CAPACITY_MEDIA_SECONDS` | `30` | Exact frame-timing window |
| `CALLORA_CAPACITY_MEDIA_WORKERS` | auto-calibrated | Shared 20-ms media-pump workers; starts at logical CPUs and doubles until the highest configured stage has at most 64 calls per worker (maximum 256) |
| `CALLORA_CAPACITY_ASTERISK_NOFILE` | `65536` | Container soft/hard open-file limit |
| `CALLORA_CAPACITY_REPORT` | temporary JSON path | Atomic checkpoint and final report |
| `CALLORA_CAPACITY_CONTINUE_AFTER_FAILURE` | `false` | Diagnostic-only continuation after a failed quality stage |

### Load-generator calibration

`CalloraCapacityMediaPump` is part of the test load generator, not the SDK. Each worker sends its
assigned calls sequentially every 20 ms. An under-provisioned pump can therefore cross the 40-ms
p99 gate before the SDK does. The characteristic signature is an outbound failure cluster whose
call indices share the same remainder modulo `MediaWorkers`, while delivery, RTP evidence and
inbound timing remain healthy.

The default worker count is calibrated from the highest configured stage. It starts with the
logical CPU count and doubles until no worker owns more than 64 configured calls. This preserves
the CPU-sized default for smaller runs and selected 32 workers for the validated 1336–1920-call
profile on a 16-logical-CPU host. An explicit `CALLORA_CAPACITY_MEDIA_WORKERS` override remains
available for controlled A/B diagnosis, but its value is serialized into the report and results
from different worker profiles are not directly comparable.

## Evidence and gate

Each call is evaluated separately for outbound and inbound audio. The report intentionally
distinguishes application observations from RTP transport evidence:

- application frames record calls to `IMediaSender.SendAsync` that completed and frames delivered
  through `IMediaReceiver.FrameReceived`;
- RTP packets come from `ICall.RtpStatistics`, sampled over an RTCP counter window of at least the
  30-second measurement duration;
- first/last frame timestamps, p50/p95/p99 frame intervals, interarrival jitter, the longest
  edge-inclusive silence, and counts of gaps over 100/250/500/1000 ms are retained per direction;
- inbound sequence gaps and duplicate/late-packet deltas use the RFC 3550 extended sequence
  counters, including 16-bit sequence wrap;
- process and Asterisk CPU/memory, managed allocations/collections, setup percentiles, channel
  counts and cleanup are recorded for the stage.

A call passes only when it stayed connected for the complete window and both directions satisfy:

- application and RTP delivery are at least 99 percent of their time-derived expectation;
- p99 frame interval is at most 40 ms;
- the longest silence, including window edges, is below 250 ms;
- packet loss is below 1 percent;
- RTP/RTCP jitter is at most 30 ms.

The 500-ms silence class remains in the report as a diagnostic even though the stricter 250-ms
maximum already fails the gate. Thresholds can be overridden with
`CALLORA_CAPACITY_MIN_DELIVERY_RATIO`, `CALLORA_CAPACITY_MAX_P99_INTERVAL_MS`,
`CALLORA_CAPACITY_MAX_SILENCE_MS`, `CALLORA_CAPACITY_MAX_PACKET_LOSS_RATIO`, and
`CALLORA_CAPACITY_MAX_JITTER_MS`; overrides are part of the serialized profile and therefore cannot
silently change the meaning of a result.

`CALLORA_CAPACITY_CONTINUE_AFTER_FAILURE=true` does not weaken or reinterpret the gate. Failed
stages remain failed and `FirstUnstableTarget`/`LargestValidatedCallCount` retain their strict
meaning; the option merely gathers later diagnostic stages when investigating a known
observability or resource failure.

## Capacity classifications

- **Strict capacity** is the largest stage where every call passes every configured per-direction
  gate. A single p99, delivery, silence, loss, jitter, connection or evidence failure rejects it.
- **Functional full-duplex evidence** means every call stayed connected, sent and received media,
  retained complete RTP/RTCP evidence and met the delivery/loss/silence gates, but at least one
  strict timing gate failed. It is diagnostic evidence, not a strict capacity claim.
- **Generator-limited evidence** has the media-pump shard signature described above. It measures
  the configured test generator and must not be presented as an SDK boundary.

Near the scheduler boundary, strict outcomes can vary between repetitions even when all calls
remain functional. Reports must therefore state both the largest repeatedly validated stage and
any higher single-window observation instead of publishing one universal maximum.

## Interpretation limits

Inbound RTP exposes exact local sequence-range evidence. For outbound RTP, the current public SDK
surface exposes sent-packet counters plus the peer's latest RTCP receiver-report loss and jitter,
but not the peer's extended-highest-sequence counter. Therefore
`Outbound.RtpSequenceGapPackets` is explicitly `null`; the test does not invent an exact outbound
gap count from a percentage. Adding raw remote receiver-report counters to the public quality
snapshot would close that observability gap.

The initial evidence run exposed a non-multiplexed RTCP port-ownership race. The SDK now reserves
and transfers the RTP/RTCP socket pair atomically before SDP publication. Post-fix runs still
require `RtcpActiveAtEnd`, RTCP packet counters and `RtpEvidenceCompleteCalls` for every call; the
benchmark does not infer that the transport fix worked merely because RTP audio continued.

The previous high-call experiment that only required some RTP progress does not prove this quality
SLA and must not be compared as if it used the same gate. A validated capacity claim requires the
JSON profile, host/runtime data, every stage result, and clean teardown with zero remaining Asterisk
channels.
