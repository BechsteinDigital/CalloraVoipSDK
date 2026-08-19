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

    [Fact]
    public async Task A_nominated_stream_relay_switches_the_session_media_onto_the_stream()
    {
        // End to end through the real session ICE agent: the direct path is dead (the session's remote is a dead
        // port), so the relay pair — whose checks the attachment echoes back through the agent's inbound route —
        // is the one nominated. That nomination must drive the media transition: ChannelBind over the stream and
        // EnterStreamRelayMode, after which the session's media rides the stream (ADR-073 media path 1–3/4).
        var session = NewSession();
        var attach = new EchoingStreamRelayAttachment();

        session.AdoptStreamRelay(attach);
        await session.StartAsync();

        var switched = await WaitUntilAsync(() => session.StreamRelayDataPathActive, TimeSpan.FromSeconds(15));

        Assert.True(switched, "the nominated stream relay must switch the session's media onto the stream");
        Assert.True(attach.BindChannelCalls >= 1, "the transition must ChannelBind over the stream");
        Assert.NotNull(attach.BoundPeer);

        await session.DisposeAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
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

    public IPEndPoint RelayedEndPoint { get; } = new(IPAddress.Parse("203.0.113.1"), 50000);

    public Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> RelaySend { get; } =
        (_, _, _) => ValueTask.CompletedTask;

    public Func<IPAddress, CancellationToken, Task>? EnsurePermission => null;

    public void Activate(Action<IPEndPoint, byte[]> onInboundIndication)
    {
        Activated = true;
        InboundRoute = onInboundIndication;
    }

    public void StartKeepAlive() => KeepAliveStarted = true;

    public Task<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>?> BindChannelAsync(
        IPEndPoint peer, Action<byte[]> onInboundMedia, CancellationToken ct)
        => Task.FromResult<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>?>((_, _) => ValueTask.CompletedTask);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

// A stream relay stand-in that drives its own nomination: its RelaySend echoes a Binding Success Response for
// each check back through the ICE agent's inbound route (captured in Activate), so the relay pair validates and
// the controlling agent nominates it — exercising the session's real transition orchestration without a live
// TURN stream (the transport primitive and BindChannelAsync are proven end-to-end elsewhere).
internal sealed class EchoingStreamRelayAttachment : IStreamRelayAttachment
{
    private Action<IPEndPoint, byte[]>? _inboundRoute;

    public int BindChannelCalls;
    public IPEndPoint? BoundPeer { get; private set; }

    public IPEndPoint RelayedEndPoint { get; } = new(IPAddress.Parse("203.0.113.2"), 50000);

    public EchoingStreamRelayAttachment()
    {
        RelaySend = (datagram, target, ct) =>
        {
            // A Binding Success Response (0x0101 + RFC 5389 magic cookie) echoing the check's transaction id, fed
            // back into the ICE agent through the inbound route the session wired in Activate.
            var response = new byte[20];
            response[0] = 0x01; response[1] = 0x01;
            response[4] = 0x21; response[5] = 0x12; response[6] = 0xA4; response[7] = 0x42;
            datagram.Span.Slice(8, 12).CopyTo(response.AsSpan(8));
            if (Volatile.Read(ref _inboundRoute) is { } route)
                _ = Task.Run(() => route(target, response));
            return ValueTask.CompletedTask;
        };
    }

    public Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> RelaySend { get; }

    public Func<IPAddress, CancellationToken, Task>? EnsurePermission => null;

    public void Activate(Action<IPEndPoint, byte[]> onInboundIndication)
        => Volatile.Write(ref _inboundRoute, onInboundIndication);

    public void StartKeepAlive() { }

    public Task<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>?> BindChannelAsync(
        IPEndPoint peer, Action<byte[]> onInboundMedia, CancellationToken ct)
    {
        Interlocked.Increment(ref BindChannelCalls);
        BoundPeer = peer;
        // A media send stub — the transport-level ChannelData path is proven in BundledMediaTransportStreamRelayTests.
        return Task.FromResult<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>?>((_, _) => ValueTask.CompletedTask);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
