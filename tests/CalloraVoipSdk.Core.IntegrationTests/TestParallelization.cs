using Xunit;

// The Core integration suite drives real UDP sockets, DTLS/SRTP handshakes and ICE consent timers.
// Running its collections in parallel on a shared CI runner starves the thread pool — sync-over-async
// waits (e.g. Task.Wait) block pool threads while the worker tasks they wait on also need pool
// threads — and lets real timers slip under CPU contention. That surfaces as TaskCanceled /
// TimeoutException and a timed-out concurrency guard (RtxRetransmissionTests, IceMediaConsent…,
// RtpSymmetricLatch…), none of which are product defects: the code under test is correct (e.g.
// RtpRetransmissionBuffer locks every operation). Serialize the assembly so these timing-sensitive
// tests run without cross-test resource contention. Cost: a slower suite; benefit: deterministic runs.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
