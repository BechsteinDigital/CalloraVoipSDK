# Capacity and sizing

**Short answer: there is no published "N calls" number for CalloraVoipSdk, and you should distrust
anyone who gives you one.** Concurrent-call capacity is a property of *your* host, runtime, GC mode,
power profile and PBX — not of the SDK. What we publish instead is a **reproducible measurement
method** plus one fully-documented reference run, so you can measure your own envelope with the
same gate and compare like with like.

## The quality gate

Capacity is only meaningful together with the quality bar it was measured at. A call counts as
passing only when it stayed connected for the whole measurement window **and** both directions
satisfy every one of:

| Gate | Threshold |
|------|-----------|
| Application and RTP delivery | ≥ 99 % of the time-derived expectation |
| p99 frame interval | ≤ 40 ms |
| Longest silence (including window edges) | < 250 ms |
| Packet loss | < 1 % |
| RTP/RTCP jitter | ≤ 30 ms |

Evidence is recorded per call and per direction: application frames (`IMediaSender.SendAsync` /
`IMediaReceiver.FrameReceived`), RTP counters from `ICall.RtpStatistics`, p50/p95/p99 frame
intervals, interarrival jitter, longest silence, gap-class counts, and RFC 3550 extended-sequence
gaps. A call with incomplete RTP/RTCP evidence does **not** pass, even if audio kept flowing.

## Three classifications — read the label

- **Strict capacity** — the largest stage where *every* call passed *every* gate. One failure of
  any kind rejects the stage.
- **Functional full-duplex evidence** — every call stayed connected, exchanged media, kept complete
  RTP/RTCP evidence and met the delivery/loss/silence gates, but at least one strict *timing* gate
  failed. Useful diagnostic; **not** a capacity claim.
- **Generator-limited evidence** — the load generator, not the SDK, was the bottleneck. Recognisable
  by an outbound failure cluster whose call indices share a remainder modulo the worker count.
  Must never be presented as an SDK boundary.

Near the scheduler boundary, strict outcomes vary between repetitions. That is why results are
always reported as *"largest repeatedly validated stage"* plus *"any higher single-window
observation"* — never as one universal maximum.

## Reference run (2026-07-28)

One concrete host: 8-core / 16-thread Intel i7-11800H, Nobara Linux 44, .NET 10, ~31 GiB RAM,
**Server GC**, a restricted "Office" power profile (capped ~65 %, no turbo), with a co-located
Asterisk `Echo()` container sharing the same machine. 20-ms PCMU frames, 10 s settling, 30 s exact
measurement window.

| Calls | Result |
|------:|--------|
| 1,408 | **Strict — repeatedly validated.** Every call passed every gate, in independent runs |
| 1,792 | Strict in a single window; a repetition put 128 calls at a 41–43 ms inbound p99 |
| 1,920 | **Functional full-duplex.** All calls connected with complete RTP/RTCP evidence and ≥ 99 % delivery; 27 calls recorded a 41 ms inbound p99 against the 40 ms limit |

Resource picture at 1,920 calls: SDK testhost ≈ 0.85 GiB working set (1.10 GiB peak), Asterisk
≈ 0.41 GiB, managed allocations ≈ 3.35 GiB over 30 s (~120 MB/s), combined SDK + PBX CPU below 45 %
of the machine. **RAM and CPU were not the binding constraint — inbound playout *scheduling* was.**

## What this means for your sizing

- **These numbers describe that host and that profile.** Treat 1,408 as evidence that the SDK is not
  the bottleneck at four-figure call counts on a laptop-class CPU, not as your number.
- **GC mode matters a lot.** Workstation GC at 1,024 calls produced 158/158/0 (Gen0/1/2)
  collections; Server GC produced 30/0/0. Use Server GC for concurrency.
- **Power/frequency policy matters.** The reference run was deliberately measured on a throttled
  profile, so it is conservative rather than best-case.
- **Co-locating the PBX costs you headroom.** Asterisk shared the same 16 logical CPUs.
- **The first thing to fail is timing, not memory.** Size for scheduler headroom, and watch p99
  frame interval rather than RAM.

## Measure your own envelope

The benchmark ships with the repository. It is marked `SoakLong` + `Capacity` and is deliberately
excluded from PR and release CI — it takes a machine to itself:

```bash
dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 \
  --filter "Category=Capacity"
```

Defaults ramp from 64 to 4,096 calls. For a focused run around a boundary you already suspect:

```bash
CALLORA_CAPACITY_LEVELS=512,1024,1280 \
CALLORA_CAPACITY_REPORT=/tmp/callora-capacity.json \
dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 \
  --filter "Category=Capacity"
```

Every run serializes its full profile — host, runtime, thresholds, worker count, per-stage results
and teardown state — into the JSON report. Any capacity claim that does not come with that report
is not a validated claim.

The complete profile, environment variables, report fields and interpretation limits are documented
for maintainers in
[`docs/maintainers/capacity-quality-benchmark.md`](https://github.com/BechsteinDigital/callora-voip-sdk/blob/main/docs/maintainers/capacity-quality-benchmark.md),
and the full reference-run write-up — including the pre-fix results and what changed — in
[`docs/audit/2026-07-28-capacity-quality-evidence.md`](https://github.com/BechsteinDigital/callora-voip-sdk/blob/main/docs/audit/2026-07-28-capacity-quality-evidence.md).

## Known observability limit

Inbound RTP exposes exact local sequence-range evidence. For **outbound** RTP the public surface
exposes sent-packet counters plus the peer's latest RTCP receiver-report loss and jitter, but not
the peer's extended-highest-sequence counter — so an exact outbound sequence-gap count cannot be
derived, and the benchmark reports it as `null` rather than inventing one. Adding raw remote
receiver-report counters to the public quality snapshot would close that gap.
