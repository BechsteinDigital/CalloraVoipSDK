using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end orchestration of the direct→relay data-path switch (relay 4d-4c-ii): a controlling
/// <see cref="BundledMediaSession"/> whose direct pair fails and whose relay pair validates nominates the relay
/// pair, which ChannelBinds the peer and flips the transport onto the relay data path (RFC 8656 §11–12). The TURN
/// server is a fake (the ChannelBind seam and the relayed check-echo are in-process); this asserts the whole ICE
/// nomination → ChannelBind → EnterRelayMode → SetRelayChannel chain, not the wire round-trip (that is 4d-6).
/// </summary>
public sealed class BundledMediaSessionRelayTransitionTests
{
    [Fact]
    public async Task A_nominated_relay_pair_switches_the_session_onto_the_relay_data_path()
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        var relayServer = new IPEndPoint(IPAddress.Loopback, 3478);
        // An IPv6 remote is unreachable on the IPv4 media socket, so the direct pair's checks fail fast (the send
        // throws) and the driver falls through to the relay pair in about a second — no timeout-bound exhaustion.
        var peer = new IPEndPoint(IPAddress.IPv6Loopback, 50000);

        var bound = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        BundledMediaSession? session = null;

        // The relay send path echoes a STUN success (matching the check's transaction id) back to the media
        // socket, so the relay pair's connectivity + USE-CANDIDATE checks validate and it is nominated.
        ValueTask RelaySend(ReadOnlyMemory<byte> check, IPEndPoint target, CancellationToken ct)
        {
            if (session?.LocalEndPoint is { } local && check.Length >= 20)
            {
                var response = new byte[20];
                response[0] = 0x01;
                response[1] = 0x01;
                response[4] = 0x21;
                response[5] = 0x12;
                response[6] = 0xA4;
                response[7] = 0x42;
                check.Span.Slice(8, 12).CopyTo(response.AsSpan(8));
                _ = echo.SendAsync(response, response.Length, local);
            }

            return ValueTask.CompletedTask;
        }

        Task<RelayChannelBinding> BindChannel(IPEndPoint p, CancellationToken ct)
        {
            bound.TrySetResult(p);
            return Task.FromResult(new RelayChannelBinding(new RecordingRelayDatagramChannel(relayServer)));
        }

        var options = new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RemoteEndPoint = peer,
            MidExtensionId = 3,
            Audio = new BundledTrackConfig { Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 0, SamplesPerPacket = 160 },
            DtlsIsClient = true,
            RemoteFingerprint = cert.Fingerprint,
            Ice = new IceMediaParameters(
                peer, IceEnabled: true, IceControlling: true,
                LocalIceUfrag: "cli0", LocalIcePwd: "clienticepassword1234567890",
                RemoteIceUfrag: "srv0", RemoteIcePwd: "servericepassword1234567890")
            {
                RemoteCandidates = [new IceRemoteCandidate(peer, Priority: 100)],
            },
            RelayIceBindingFactory = _ => new RelayIceBinding(
                new NoopRelayIndicationChannel(relayServer), _ => { }, RelaySend, null, BindChannel),
        };

        session = new BundledMediaSession(
            options, new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);
        await using var lease = session;
        await session.StartAsync();

        // The relay pair is nominated → its peer is ChannelBound → the transport switches onto the relay data path.
        Assert.Equal(peer, await bound.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        await WaitUntilAsync(() => session.RelayDataPathActive, TimeSpan.FromSeconds(5));
        Assert.True(session.RelayDataPathActive, "the session must commit to the relay data path after ChannelBind");
    }

    [Fact]
    public async Task The_relay_transition_starts_the_channel_rebind_and_disposes_it_on_teardown()
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        var relayServer = new IPEndPoint(IPAddress.Loopback, 3478);
        var peer = new IPEndPoint(IPAddress.IPv6Loopback, 50000);

        var rebind = new RecordingKeepAlive();
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        BundledMediaSession? session = null;

        ValueTask RelaySend(ReadOnlyMemory<byte> check, IPEndPoint target, CancellationToken ct)
        {
            if (session?.LocalEndPoint is { } local && check.Length >= 20)
            {
                var response = new byte[20];
                response[0] = 0x01;
                response[1] = 0x01;
                response[4] = 0x21;
                response[5] = 0x12;
                response[6] = 0xA4;
                response[7] = 0x42;
                check.Span.Slice(8, 12).CopyTo(response.AsSpan(8));
                _ = echo.SendAsync(response, response.Length, local);
            }

            return ValueTask.CompletedTask;
        }

        // The bind hands back the channel plus the recording rebind keepalive the session must start and dispose.
        Task<RelayChannelBinding> BindChannel(IPEndPoint p, CancellationToken ct)
            => Task.FromResult(new RelayChannelBinding(new RecordingRelayDatagramChannel(relayServer), rebind));

        var options = new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RemoteEndPoint = peer,
            MidExtensionId = 3,
            Audio = new BundledTrackConfig { Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 0, SamplesPerPacket = 160 },
            DtlsIsClient = true,
            RemoteFingerprint = cert.Fingerprint,
            Ice = new IceMediaParameters(
                peer, IceEnabled: true, IceControlling: true,
                LocalIceUfrag: "cli0", LocalIcePwd: "clienticepassword1234567890",
                RemoteIceUfrag: "srv0", RemoteIcePwd: "servericepassword1234567890")
            {
                RemoteCandidates = [new IceRemoteCandidate(peer, Priority: 100)],
            },
            RelayIceBindingFactory = _ => new RelayIceBinding(
                new NoopRelayIndicationChannel(relayServer), _ => { }, RelaySend, null, BindChannel),
        };

        session = new BundledMediaSession(
            options, new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);
        await session.StartAsync();

        await WaitUntilAsync(() => session.RelayDataPathActive, TimeSpan.FromSeconds(15));
        Assert.True(session.RelayDataPathActive);
        await WaitUntilAsync(() => rebind.Started, TimeSpan.FromSeconds(2));
        Assert.True(rebind.Started, "the transition must start the channel rebind keepalive");
        Assert.False(rebind.Disposed);

        await session.DisposeAsync();
        Assert.True(rebind.Disposed, "session teardown must dispose the channel rebind keepalive");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        for (var waited = TimeSpan.Zero; waited < timeout && !condition(); waited += TimeSpan.FromMilliseconds(25))
            await Task.Delay(25);
    }
}

// A relay data-path channel fake: frames/unwraps trivially and reports the server it is bound to (for the
// SetRelayChannel same-server check).
internal sealed class RecordingRelayDatagramChannel : IRelayDatagramChannel
{
    public RecordingRelayDatagramChannel(IPEndPoint relayServer) => RelayServer = relayServer;

    public IPEndPoint RelayServer { get; }

    public bool IsFromRelay(IPEndPoint source) => RelayEndPoint.SameEndPoint(source, RelayServer);

    public byte[] Wrap(ReadOnlySpan<byte> payload) => payload.ToArray();

    public bool TryUnwrap(ReadOnlySpan<byte> datagram, IPEndPoint source, out byte[] payload)
    {
        payload = datagram.ToArray();
        return IsFromRelay(source);
    }
}

// A keepalive fake that records whether it was started and disposed (for the channel-rebind wiring test).
internal sealed class RecordingKeepAlive : IRelayKeepAlive
{
    private volatile bool _started;
    private volatile bool _disposed;

    public bool Started => _started;
    public bool Disposed => _disposed;

    public void Start() => _started = true;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
