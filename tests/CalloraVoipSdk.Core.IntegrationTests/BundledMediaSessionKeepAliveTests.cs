using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Proves <see cref="BundledMediaSession"/> drives the relay allocation keepalive's lifecycle: it is not started
/// at construction, is started once <see cref="BundledMediaSession.StartAsync"/> brings the shared transport up,
/// and is disposed on teardown (before the transport, so its Refresh(0) can still ride the socket). The keepalive
/// and the relay indication channel are fakes — this asserts the session wiring, not the TURN control stack
/// (that is covered end-to-end by WebRtcRelayBindingTests).
/// </summary>
public sealed class BundledMediaSessionKeepAliveTests
{
    [Fact]
    public async Task Starts_the_relay_keepalive_on_start_and_disposes_it_on_teardown()
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        var keepAlive = new RecordingRelayKeepAlive();
        // Ephemeral local bind (port 0) avoids a free-port race; the remote is a dead port — no media flows, the
        // DTLS handshake never completes, but the keepalive lifecycle is fully observable.
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
            RelayIceBindingFactory = _ => new RelayIceBinding(
                new NoopRelayIndicationChannel(remote),
                _ => { },
                (_, _, _) => ValueTask.CompletedTask,
                keepAlive),
        };

        var session = new BundledMediaSession(
            options, new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);

        Assert.False(keepAlive.Started); // not started at construction — only once the transport is up

        await session.StartAsync();
        Assert.True(keepAlive.Started);  // StartAsync brought the transport up and started the keepalive

        await session.DisposeAsync();
        Assert.True(keepAlive.Disposed); // teardown disposed the keepalive
    }
}

// Records the keepalive lifecycle calls the session makes.
internal sealed class RecordingRelayKeepAlive : IRelayKeepAlive
{
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

// A relay indication channel that unwraps nothing — enough to satisfy SetIndicationRelay on a direct-mode transport.
internal sealed class NoopRelayIndicationChannel : IRelayIndicationChannel
{
    public NoopRelayIndicationChannel(IPEndPoint relayServer) => RelayServer = relayServer;

    public IPEndPoint RelayServer { get; }

    public bool IsFromRelay(IPEndPoint source) => false;

    public byte[] Wrap(IPEndPoint peer, ReadOnlySpan<byte> payload) => payload.ToArray();

    public bool TryUnwrap(ReadOnlySpan<byte> datagram, IPEndPoint source, out IPEndPoint? peer, out byte[] payload)
    {
        peer = null;
        payload = Array.Empty<byte>();
        return false;
    }
}
