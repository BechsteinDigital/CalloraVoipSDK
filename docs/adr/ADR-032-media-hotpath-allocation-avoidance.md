# ADR-032: Media Hot-Path and SIP-Framer Allocation Avoidance

Status: Accepted
Date: 2026-07-09

## Context

Two data paths run per-packet or per-segment for the whole lifetime of a call and therefore
dominate the SDK's steady-state allocation rate:

1. The **RTP/RTCP receive loop** — one datagram every ~20 ms per media stream (~50/s per call),
   for the entire call.
2. The **SIP stream framer** — every TCP/TLS segment on a signalling connection is appended and
   the buffered header is re-scanned on each `TryReadFrame`.

The `UdpClient.ReceiveAsync(ct)` overload the receive loop originally used allocates a fresh
`byte[]` inside every `UdpReceiveResult`, i.e. one heap allocation per inbound media packet.
`SipWireStreamFramer` appended byte-by-byte (`List<byte>.Add` per byte, a capacity check per
byte) and split the header text **twice** (once in the chunked-encoding check, once in the
Content-Length parse), allocating a second `string[]` plus its substrings for nothing.

The governing rule is ENGINEERING_RULES **K3**: on the media hot path, no allocations where
avoidable (HARD-F1), and bounded buffers with drop-oldest rather than unbounded queues
(HARD-F4). The framer is signalling-plane, not media hot path, but the same allocation-hygiene
principle applies to a per-segment loop.

### Verified current state (graphify + code)

- **The RTP receive loop rents one pooled buffer for its whole lifetime.**
  `RtpSession.RunReceiveLoopAsync` (`src/Core/Infrastructure/Rtp/Session/RtpSession.cs`,
  graphify node L462) rents a single `ArrayPool<byte>.Shared.Rent(MediaSocketDefaults.DatagramBufferBytes)`
  outside the loop, receives into it via
  `_udp.Client.ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, ct)` (socket-level,
  so the unconnected socket still yields the remote endpoint), and returns the buffer in a
  `finally`. `ProcessDatagram` takes a `ReadOnlySpan<byte>` (L514), not a `byte[]`.
- **The buffer-reuse safety invariant is upheld by copy-on-retain.** The loop is
  single-threaded (one sequential `await`), and everything retained past `ProcessDatagram` is
  copied synchronously before the next receive overwrites the buffer: the STUN and DTLS
  branches call `datagram.ToArray()` before invoking their handlers (L525, L542); the plain
  RTCP branch clones (`datagram.ToArray()`, L597); `SrtpContext.Unprotect` /
  `UnprotectRtcp` return fresh arrays; `RtpPacketCodec.Decode` copies payload and extension
  data. The in-code comment at L466-470 records exactly this reasoning.
- **The pattern was replicated, not left one-off.** `BundledMediaTransport`
  (`src/Core/Infrastructure/Rtp/BundledMediaTransport.cs` L320) rents from the same pool with
  the same `MediaSocketDefaults.DatagramBufferBytes` size — the ADR-011 multi-track transport
  reuses the same pooled-receive discipline.
- **The SIP framer bulk-copies and splits once.** `SipWireStreamFramer.Append`
  (`src/Core/Infrastructure/Sip/Transport/SipWireStreamFramer.cs` L39) is `_buffer.AddRange(bytes)`
  (one capacity check per segment). `TryReadFrame` (L59) splits the header text **once**
  (`headerText.Split("\r\n", …)`, L86) and passes the resulting `string[]` to both
  `HasChunkedTransferEncoding(lines)` and `TryParseContentLength(lines, …)`; the in-code comment
  at L84-85 names the removed double-split. The final frame is copied out with
  `GC.AllocateUninitializedArray<byte>` (L102).

## Decision

Eliminate the two highest-frequency avoidable allocations without changing observed behaviour:

1. **Pooled per-loop RTP receive buffer.** The receive loop rents one buffer from
   `ArrayPool<byte>.Shared`, receives into it via socket-level `ReceiveFromAsync`, and hands
   `ProcessDatagram` a span. Anything retained past the synchronous span-processing is copied;
   the single-threaded loop guarantees no two datagrams alias the buffer at once.
2. **Framer bulk-append + single header split.** `Append` uses `AddRange`; `TryReadFrame`
   splits the header once and shares the lines array between the two header checks.

**Crux:** the receive loop is single-threaded, so one reused buffer is safe *precisely because*
every retained byte is copied before the next `ReceiveFromAsync`; the correctness of pooling
rests on that copy-on-retain invariant, not on the pooling mechanism itself.

## Consequences

Positive:
- RTP receive drops from one `byte[]` per datagram to one rented buffer per loop lifetime
  (structural, verified by inspection). Data integrity across the reused buffer is gate-proven
  by `RtpPooledReceiveBufferTests` — 60 packets of varying length (20..59 B) each carrying a
  unique fill byte; every received packet must match its exact length and marker, so a
  cross-packet bleed (wrong slice length or stale trailing bytes) fails hard.
- The framer's `frame_parse` case dropped **−720 B/op (−24 %)** and **−57 % time**
  (3008 → 2288 B/op; 5266 → 2244 ns/op) on the `sip.stream_framer.frame_parse` perf harness
  (`GC.GetAllocatedBytes`, net9.0). Neighbouring cases (`srtp.protect_unprotect`,
  `rtp.packet_codec.decode`) were unchanged — no regress. A first dedicated
  `SipWireStreamFramerTests` (6 cases) pins the parse behaviour against the refactor.

Tradeoffs / honest divergence:
- **The RTP receive-loop allocation reduction is claimed structurally, not benchmarked.** There
  is no BenchmarkDotNet number for the receive path — one rent per loop vs. one per datagram is
  a code-inspection fact, not a measured delta. Only the framer path carries a measured number.
- **Socket API change forced a teardown fix.** `Socket.ReceiveFromAsync(Memory,…,ct)` throws an
  NRE on Linux if the socket is disposed under a pending receive (the old `UdpClient.ReceiveAsync`
  tolerated it). The loop therefore cancels an internal linked CTS *first* (the pending receive
  ends cleanly with `OperationCanceledException`) and disposes the socket only after — a
  behaviour the loop now depends on for clean shutdown.
- **RTCP encode-side LINQ was deliberately left in place.** The `Select().ToArray()`/`Sum()`
  in `RtcpPacketCodec.Encode` runs only on the periodic report *send* path (~1 report per
  interval per call), not the receive hot path; the decode path is already LINQ-free.
  Micro-optimising it would be premature — assessed and consciously out of scope, not silently
  dropped.
- **Further framer headroom remains open.** A span-based header-line iteration (instead of
  `string.Split` producing substring allocations) would cut more, and is recorded as a follow-up
  rather than built.
- No claim of "B.6 DONE" — this is the two highest-frequency allocations addressed; the RTCP
  encode path and span-based header iteration remain as bounded, justified follow-ups.

## Guardrails

- The single-stream `RtpSession` receive behaviour is byte-identical through the pooling change:
  the burst test (`RtpPooledReceiveBufferTests`) fails hard on any cross-packet bleed, and the
  full send/receive/SRTP suite is the regression net.
- Any byte retained beyond `ProcessDatagram`'s synchronous span handling MUST be copied before
  the next `ReceiveFromAsync` — the loop's single-thread + copy-on-retain invariant is the
  safety basis for reusing the buffer (in-code INVARIANT, RtpSession L466-470).
- The pooled buffer is returned in a `finally`; the datagram buffer size is the shared
  `MediaSocketDefaults.DatagramBufferBytes` constant, the same one `BundledMediaTransport` rents.
- Framer behaviour (Content-Length framing, compact `l`, chunked reject, fragmented append,
  keepalive ping, missing Content-Length throw) is pinned by `SipWireStreamFramerTests`.
- Perf regressions on the framer path are gated (±15 % on the perf harness).

## Sources

- Logs: `docs/archive/agent-log/2026-07-09-dev-b6-rtp-receive-pooling.md` (pooled RTP receive
  buffer, copy-on-retain invariant, teardown fix, `RtpPooledReceiveBufferTests`),
  `2026-07-09-dev-b6-sip-framer-alloc.md` (framer `AddRange` + single split, −24 % measured,
  RTCP-LINQ assessed-and-excluded, `SipWireStreamFramerTests`).
- Code (graphify-verified): `RtpSession.cs` (`RunReceiveLoopAsync` L462, `ProcessDatagram` L514,
  copy-on-retain L525/L542/L597, INVARIANT L466-470), `BundledMediaTransport.cs` (pooled rent
  L320), `SipWireStreamFramer.cs` (`Append`/`AddRange` L39, single split L86, `TryReadFrame`
  L59), `MediaSocketDefaults.cs` (`DatagramBufferBytes`).
- Markers / RFC: ENGINEERING_RULES K3 (hot-path no-alloc / HARD-F1, bounded buffers / HARD-F4),
  RFC 3550 §A.1 (probation, referenced by the burst-test liveness threshold), RFC 5626 §4.4.1
  (double-CRLF keepalive the framer consumes).
