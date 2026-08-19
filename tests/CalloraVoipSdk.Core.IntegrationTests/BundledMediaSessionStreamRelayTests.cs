using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Proves <see cref="BundledMediaSession.AdoptStreamRelay"/> wires an adopted stream relay candidate into the
/// session (ADR-073 slice 4c-iii): it activates the candidate (wiring the inbound route and starting its receive
/// loop), hands the relay send path to the ICE agent, starts the allocation keepalive, and disposes the candidate
/// on teardown — after the ICE agent, since the relay send rides the candidate's own transport. A second adoption
/// is a no-op that disposes the redundant candidate. The attachment is a fake — this asserts the session wiring
/// and lifecycle, not the TURN control stack (covered by the transport/consent slices) nor the ICE routing
/// (covered by <c>BundledIceControlStreamRelayTests</c>).
/// </summary>
public sealed class BundledMediaSessionStreamRelayTests
{
    [Fact]
    public async Task Adopts_a_stream_relay_activating_it_and_starting_its_keepalive_and_disposes_it_on_teardown()
    {
        var session = NewSession();
        var attach = new FakeStreamRelayAttachment();

        session.AdoptStreamRelay(attach);

        Assert.True(attach.Activated, "adoption must activate the candidate (wire inbound + start its receive loop)");
        Assert.NotNull(attach.InboundRoute);           // the inbound route into the ICE agent was wired
        Assert.True(attach.KeepAliveStarted, "adoption must start the allocation keepalive");
        Assert.False(attach.Disposed);

        await session.StartAsync();                    // brings the shared transport up; keepalive stays started (idempotent)
        Assert.True(attach.KeepAliveStarted);

        await session.DisposeAsync();
        Assert.True(attach.Disposed, "teardown must dispose the adopted stream relay");
    }

    [Fact]
    public async Task A_second_adoption_is_a_no_op_and_disposes_the_redundant_candidate()
    {
        var session = NewSession();
        var first = new FakeStreamRelayAttachment();
        var second = new FakeStreamRelayAttachment();

        session.AdoptStreamRelay(first);
        session.AdoptStreamRelay(second);              // single-shot: no-op, disposes the redundant candidate

        Assert.True(first.Activated);
        Assert.False(second.Activated, "the redundant candidate must not be wired");
        Assert.True(second.Disposed, "the redundant candidate must be disposed rather than leaked");
        Assert.False(first.Disposed);                  // the adopted one stays live until teardown

        await session.DisposeAsync();
        Assert.True(first.Disposed);
    }

    private static BundledMediaSession NewSession()
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        // Ephemeral local bind (port 0); the remote is a dead port — no media flows and the DTLS handshake never
        // completes, but the adoption wiring and lifecycle are fully observable.
        var remote = new IPEndPoint(IPAddress.Loopback, 9);
        var options = new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RemoteEndPoint = remote,
            MidExtensionId = 3,
            Audio = new BundledTrackConfig { Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 0, SamplesPerPacket = 160 },
            DtlsIsClient = true,
            RemoteFingerprint = cert.Fingerprint,
            Ice = new IceMediaParameters(
                remote, IceEnabled: true, IceControlling: true,
                LocalIceUfrag: "cli0", LocalIcePwd: "clienticepassword1234567890",
                RemoteIceUfrag: "srv0", RemoteIcePwd: "servericepassword1234567890"),
        };
        return new BundledMediaSession(
            options, new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);
    }
}

// Records how the session drives an adopted stream relay through the IStreamRelayAttachment seam.
internal sealed class FakeStreamRelayAttachment : IStreamRelayAttachment
{
    public bool Activated { get; private set; }
    public bool KeepAliveStarted { get; private set; }
    public bool Disposed { get; private set; }
    public Action<IPEndPoint, byte[]>? InboundRoute { get; private set; }

    public Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> RelaySend { get; } =
        (_, _, _) => ValueTask.CompletedTask;

    public Func<IPAddress, CancellationToken, Task>? EnsurePermission => null;

    public void Activate(Action<IPEndPoint, byte[]> onInboundIndication)
    {
        Activated = true;
        InboundRoute = onInboundIndication;
    }

    public void StartKeepAlive() => KeepAliveStarted = true;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
