# Callora capacity-quality evidence — 2026-07-28

## Scope

This is machine-bound diagnostic evidence, not a global SDK limit. Host profile: Nobara Linux 44,
.NET 10.0.6, x64, 16 logical processors, approximately 31 GiB RAM. Asterisk and the SDK testhost
shared the machine; unrelated existing user containers were not stopped.

Each stage used 20-ms PCMU frames, a 10-second settling period and a 30-second exact measurement
window. The gate and fields are defined in
[`docs/maintainers/capacity-quality-benchmark.md`](../maintainers/capacity-quality-benchmark.md).
Every completed run tore down to zero Asterisk channels.

## Initial pre-fix results

The original permissive experiment that only required RTP progression is not comparable to this
gate.

With Workstation GC, the cumulative diagnostic ramp showed:

| Calls | Connected/channels | App/timing passed | Complete RTP evidence | Main observation |
| ---: | ---: | ---: | ---: | --- |
| 512 | 512 | 512 | 509 | p99 ≤26 ms; 3 RTCP bind failures |
| 1024 | 1024 | 1024 | 1011 | p99 ≤28 ms; delivery ≥99.93%; 13 RTCP bind failures |
| 1536 | 1536 | 1161 | 1505 | 375 calls exceed the 40-ms p99 gate; worst p99 43 ms |
| 2048 | 2048 | 0 | 1976 | all calls exceed p99; worst p99 47 ms |
| 2560 | 44 at window end | 0 | 2435 | hard degradation; inbound gap up to 4.30 s |

The later 2816/3072 diagnostic stages were not capacity candidates: terminated calls remained in
the accumulated call list. The benchmark now refuses to continue a cumulative ramp when prior-stage
calls are no longer connected.

A fair cumulative Server-GC run (`512,1024,1280`) materially changed the media result:

| Calls | App/timing passed | Complete RTP evidence | Strict passed | Process/PBX CPU |
| ---: | ---: | ---: | ---: | ---: |
| 512 | 512 | 507 | 507 | 7.22% / 5.40% of machine |
| 1024 | 1024 | 1002 | 1002 | 12.87% / 9.09% |
| 1280 | 1280 | 1249 | 961 | 15.31% / 11.72% |

At 1280 with Server GC, all calls stayed connected, application delivery was at least 99.93
percent, p99 was at most 32 ms and the longest edge-inclusive gap was 46.36 ms. No inbound sequence
gap was observed on calls with available RFC 3550 evidence. This proves a machine-specific
application-media lower bound of 1280 under that runtime profile. It does **not** prove a strict
1280-call Callora quality SLA: 31 calls lacked complete RTCP evidence, and additional broader RTCP
counter windows fell below the 99-percent RTP-delivery gate.

Managed allocations remained high: approximately 1.99 GiB in 30 seconds at 1024 calls and
2.43 GiB at 1280 calls. Server GC reduced the measured collections to 30/0/0 and 37/0/0
(Gen0/Gen1/Gen2), whereas Workstation GC at 1024 produced 158/158/0. The public receive path creates
a `MediaFrameReceivedEventArgs` and enumerates a copied invocation list per frame; the full
allocation total also includes RTP/session and benchmark activity, so a profiler is required before
assigning every byte to that callback.

## Post-fix calibrated results

After atomic RTP/RTCP port-pair reservation was merged, a cumulative Server-GC confirmation run
at 512, 1024 and 1280 calls produced complete RTP/RTCP evidence and strict quality for every call.
No `Address already in use` RTCP bind warning remained.

The first follow-up used 16 media-pump workers, matching the host's logical CPU count. At 1336
calls, 27 calls missed only the 40-ms p99 gate. Twenty-two outbound failures belonged to one
generator shard (indices separated by 16), while delivery and RTP/RTCP evidence remained healthy.
Repeating the same stages with 32 workers removed that false boundary:

| Calls | Strict passed | Complete RTP evidence | Process/PBX CPU | Observation |
| ---: | ---: | ---: | ---: | --- |
| 1336 | 1336 | 1336 | 16.53% / 13.29% | all strict gates passed |
| 1344 | 1344 | 1344 | 16.41% / 12.70% | all strict gates passed |
| 1408 | 1408 | 1408 | 17.83% / 14.27% | independently repeated strict stage |
| 1536 | 1536 | 1536 | 20.92% / 17.10% | all strict gates passed |
| 1664 | 1664 | 1664 | 22.14% / 18.08% | all strict gates passed |
| 1792 | 1792 | 1792 | 23.50% / 19.23% | single strict window; not repeatable |
| 1920 | 1893 | 1920 | 24.57% / 20.32% | 27 inbound p99 values at 41 ms |

At 1920 calls, every call stayed connected and full-duplex, every call retained complete RTP/RTCP
evidence, no call fell below the 99-percent application or RTP delivery gates, no inbound sequence
gap was observed, and the longest gap stayed below 50 ms. The failed calls crossed only the strict
40-ms p99 threshold by one millisecond. This is functional full-duplex evidence, not a strict
1920-call claim.

A fresh 1792-call repetition demonstrated the scheduler transition rather than a deterministic
hard limit: 128 calls recorded inbound p99 values of 41–43 ms, while all calls remained connected,
retained complete RTP/RTCP evidence, stayed above 99-percent delivery and had no gap above 61 ms.
The repeatedly demonstrated strict lower bound on this setup is therefore 1408 calls; 1792 is a
single-window strict observation inside a variable timing region.

RAM did not define the observed boundary. At 1920 calls, the SDK testhost used approximately
0.85 GiB current/1.10 GiB peak working set and Asterisk approximately 0.41 GiB. Managed
allocations were approximately 3.35 GiB over 30 seconds (about 120 MB/s). The first failing signal
was scheduler timing, while aggregate SDK/PBX CPU remained below 45 percent of the 16-logical-CPU
machine. The operator-selected Office profile was reported as capped at 65 percent without turbo,
so this remains a deliberately conservative shared-host measurement.

## Product findings

1. **RTCP port-pair ownership — post-fix evidence.** Atomic RTP/RTCP socket-pair reservation and
   ownership transfer removed the probabilistic bind failure from the repeated high-call runs.
   Capacity reports continue to require active RTCP and complete counter evidence per call.
2. **Aligned raw counter observation.** Public `ICall.RtpStatistics` updates on a five-second RTCP
   cadence. Its counter window is necessarily broader than the exact application timing window,
   which can create boundary-sensitive delivery ratios under changing load. Expose an immediate,
   thread-safe current-counter snapshot or a windowed delta API. Also expose raw remote receiver
   report sequence counters if exact outbound sequence-gap counts are required.
3. **Media hot-path scheduling.** The first post-calibration failure is inbound playout timing, not
   RAM, connection loss or RTP transport. `RtpCallMediaSession` currently owns one 20-ms
   `PeriodicTimer` per call. Profile TimerQueue/ThreadPool wake latency around 1664–1920 calls before
   deciding whether to shard playout scheduling.
4. **Media hot-path allocation.** Profile the sender, RTP decode/playout and public
   `MediaReceiver` callback. Eliminate avoidable per-frame allocations and copied invocation lists,
   then repeat with an explicit production runtime profile. Server GC and the power profile remain
   part of every defensible capacity claim.

These results must not be marketed as a fixed maximum. They establish a reproducible strict lower
bound, a higher non-repeatable strict observation and a functional timing-transition region for
one concrete machine and profile.
