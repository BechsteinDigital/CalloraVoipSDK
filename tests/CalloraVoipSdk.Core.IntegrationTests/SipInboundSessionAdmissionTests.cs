using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #279: the inbound-session ceiling is enforced by reserving a slot, not by reading the session table's
/// count — a count check sits apart from the insert, so concurrent INVITEs would all observe the same free
/// slot. These tests pin the reservation bookkeeping: it holds under contention, and every refusal or release
/// hands the slot back rather than burning it.
/// </summary>
public sealed class SipInboundSessionAdmissionTests
{
    private static readonly IPAddress RemoteA = IPAddress.Parse("203.0.113.9");
    private static readonly IPAddress RemoteB = IPAddress.Parse("203.0.113.10");

    [Fact]
    public void Admission_stops_at_the_global_ceiling_and_resumes_after_a_release()
    {
        var admission = new SipInboundSessionAdmission(maxConcurrentSessions: 2);

        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("a", RemoteA));
        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("b", RemoteA));
        Assert.Equal(SipInboundSessionAdmissionOutcome.GlobalCapReached, admission.TryAdmitInbound("c", RemoteA));

        admission.ReleaseInbound("a");
        Assert.Equal(1, admission.ReservedSlots);
        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("c", RemoteA));
        Assert.Equal(2, admission.ReservedSlots);
    }

    [Fact]
    public void A_per_remote_refusal_hands_the_global_slot_back()
    {
        var admission = new SipInboundSessionAdmission(maxConcurrentSessions: 8, maxPerRemote: 1);

        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("a", RemoteA));
        Assert.Equal(
            SipInboundSessionAdmissionOutcome.PerRemoteCapReached,
            admission.TryAdmitInbound("b", RemoteA));

        // The global slot claimed on the way in must not stay held for a request that was refused — otherwise
        // one remote hammering its own ceiling would drain the global budget it never got to use.
        Assert.Equal(1, admission.ReservedSlots);
        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("c", RemoteB));
    }

    [Fact]
    public void Outbound_sessions_take_a_slot_without_being_refused()
    {
        var admission = new SipInboundSessionAdmission(maxConcurrentSessions: 1);

        // An outgoing call is never refused by the inbound ceiling, but it occupies the same table — so it
        // counts, exactly as the previous _sessions.Count check counted it.
        admission.ReserveOutbound();
        Assert.Equal(1, admission.ReservedSlots);
        Assert.Equal(SipInboundSessionAdmissionOutcome.GlobalCapReached, admission.TryAdmitInbound("a", RemoteA));

        admission.ReleaseSlot();
        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("a", RemoteA));
    }

    [Fact]
    public void Admission_enforces_the_ceiling_atomically_under_contention()
    {
        const int workers = 32;
        var admission = new SipInboundSessionAdmission(maxConcurrentSessions: 1);

        // Released simultaneously against a ceiling of 1: the reservation must be indivisible, so exactly one
        // of the threads wins regardless of scheduling.
        var outcomes = new SipInboundSessionAdmissionOutcome[workers];
        using var release = new Barrier(workers);
        var threads = Enumerable.Range(0, workers)
            .Select(i => new Thread(() =>
            {
                release.SignalAndWait();
                outcomes[i] = admission.TryAdmitInbound($"call-{i}", RemoteA);
            }))
            .ToArray();

        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(1, outcomes.Count(o => o == SipInboundSessionAdmissionOutcome.Admitted));
        Assert.Equal(1, admission.ReservedSlots);
    }

    [Fact]
    public void Clear_drops_every_reservation()
    {
        var admission = new SipInboundSessionAdmission(maxConcurrentSessions: 2);

        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("a", RemoteA));
        Assert.Equal(SipInboundSessionAdmissionOutcome.Admitted, admission.TryAdmitInbound("b", RemoteA));

        admission.Clear();

        Assert.Equal(0, admission.ReservedSlots);
        Assert.Equal(0, admission.CountFor(RemoteA));
    }
}
