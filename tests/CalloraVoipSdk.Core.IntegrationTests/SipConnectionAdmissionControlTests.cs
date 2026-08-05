using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-3: connection admission caps the accepted inbound connections a peer can pin, both globally
/// and per source IP, so no remote can exhaust the connection budget. A lease is held for the connection's
/// lifetime and frees its slots exactly once on dispose.
/// </summary>
public sealed class SipConnectionAdmissionControlTests
{
    private static readonly IPAddress A = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress B = IPAddress.Parse("192.0.2.2");

    [Fact]
    public void Global_cap_rejects_beyond_the_limit()
    {
        var control = new SipConnectionAdmissionControl(maxGlobal: 2, maxPerRemote: 0);

        Assert.NotNull(control.TryAdmit(A));
        Assert.NotNull(control.TryAdmit(B));
        Assert.Null(control.TryAdmit(A)); // global budget of 2 is exhausted
    }

    [Fact]
    public void Releasing_a_lease_frees_a_global_slot()
    {
        var control = new SipConnectionAdmissionControl(maxGlobal: 1, maxPerRemote: 0);

        var first = control.TryAdmit(A);
        Assert.NotNull(first);
        Assert.Null(control.TryAdmit(B));

        first!.Dispose();
        Assert.NotNull(control.TryAdmit(B)); // slot freed
    }

    [Fact]
    public void Per_remote_cap_isolates_sources()
    {
        var control = new SipConnectionAdmissionControl(maxGlobal: 0, maxPerRemote: 2);

        Assert.NotNull(control.TryAdmit(A));
        Assert.NotNull(control.TryAdmit(A));
        Assert.Null(control.TryAdmit(A));    // A capped at 2
        Assert.NotNull(control.TryAdmit(B)); // B has its own budget
    }

    [Fact]
    public void Releasing_a_per_remote_lease_frees_that_source()
    {
        var control = new SipConnectionAdmissionControl(maxGlobal: 0, maxPerRemote: 1);

        var lease = control.TryAdmit(A);
        Assert.NotNull(lease);
        Assert.Null(control.TryAdmit(A));

        lease!.Dispose();
        Assert.NotNull(control.TryAdmit(A));
    }

    [Fact]
    public void Lease_dispose_is_idempotent()
    {
        var control = new SipConnectionAdmissionControl(maxGlobal: 1, maxPerRemote: 0);

        var lease = control.TryAdmit(A);
        Assert.NotNull(lease);
        lease!.Dispose();
        lease.Dispose(); // a double dispose must not release a phantom slot

        var readmit = control.TryAdmit(A);
        Assert.NotNull(readmit);
        // Only one global slot exists and it is now held by readmit; a double-release would have left a
        // phantom slot free here.
        Assert.Null(control.TryAdmit(B));
    }

    [Fact]
    public void Zero_caps_mean_unlimited()
    {
        var control = new SipConnectionAdmissionControl(maxGlobal: 0, maxPerRemote: 0);

        for (var i = 0; i < 1000; i++)
            Assert.NotNull(control.TryAdmit(A));
    }
}
