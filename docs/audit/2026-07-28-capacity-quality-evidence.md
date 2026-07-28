# Callora capacity-quality evidence — 2026-07-28

## Scope

This is machine-bound diagnostic evidence, not a global SDK limit. Host profile: Nobara Linux 44,
.NET 10.0.6, x64, 16 logical processors, approximately 31 GiB RAM. Asterisk and the SDK testhost
shared the machine; unrelated existing user containers were not stopped.

Each stage used 20-ms PCMU frames, a 10-second settling period and a 30-second exact measurement
window. The gate and fields are defined in
[`docs/maintainers/capacity-quality-benchmark.md`](../maintainers/capacity-quality-benchmark.md).
Every completed run tore down to zero Asterisk channels.

## Results

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

## Product findings

1. **RTCP port-pair ownership — highest priority.** `SipCoreCallChannel` reserves RTP only
   (`src/Core/Infrastructure/Sip/Adapters/SipCoreCallChannel.cs`), `SdpUtilities` derives RTCP as
   RTP+1, and `CallRtcpQualityMonitor` later binds that unreserved port. Repeated runs captured
   `SocketException: Address already in use`; RTP audio continued while quality reporting was
   disabled. Reserve and ownership-transfer the RTP/RTCP pair atomically, with a deterministic
   collision test.
2. **Aligned raw counter observation.** Public `ICall.RtpStatistics` updates on a five-second RTCP
   cadence. Its counter window is necessarily broader than the exact application timing window,
   which can create boundary-sensitive delivery ratios under changing load. Expose an immediate,
   thread-safe current-counter snapshot or a windowed delta API. Also expose raw remote receiver
   report sequence counters if exact outbound sequence-gap counts are required.
3. **Media hot-path allocation and scheduling.** Profile the sender, RTP decode/playout and public
   `MediaReceiver` callback at 1024–1536 calls. Eliminate avoidable per-frame allocations and copied
   invocation lists, then repeat with an explicit production runtime profile. The strong difference
   between Workstation and Server GC must be reflected in deployment guidance and benchmark claims.

Until item 1 is fixed, strict capacity runs are expected to fail probabilistically before the
audible media limit and must not be marketed as a fixed maximum.
