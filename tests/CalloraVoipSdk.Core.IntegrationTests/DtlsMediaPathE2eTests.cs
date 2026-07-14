using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// DTLS-SRTP media path end to end (RFC 5763/5764): two real
/// <see cref="RtpCallMediaSession"/> instances over UDP loopback perform the DTLS
/// handshake on the media socket, derive SRTP contexts from the exported keys, and
/// exchange audio — plus the fail-closed guarantees before/without a completed handshake.
/// </summary>
public sealed class DtlsMediaPathE2eTests
{
    [Fact]
    public async Task DtlsNegotiatedLegs_HandshakeOnMediaSocket_ThenAudioFlows()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        await using var sessionA = CreateSession(portA, portB, isClient: true, certA, certB.Fingerprint);
        await using var sessionB = CreateSession(portB, portA, isClient: false, certB, certA.Fingerprint);

        var frameReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionB.FrameReceived += frame => frameReceived.TrySetResult(frame.Payload.ToArray());

        await sessionB.StartAsync();
        await sessionA.StartAsync();

        // Until the handshake completes, sends are suppressed (fail-closed); keep sending
        // so the first frame after key installation proves the encrypted path end to end.
        var payload = new byte[160];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!frameReceived.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await sessionA.SendFrameAsync(new CallAudioFrame(payload, 0, 160), overall.Token);
            await Task.Delay(20, overall.Token);
        }

        var received = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task DtlsNegotiatedLeg_DropsPlainRtp_WhileUnkeyed()
    {
        var localPort = FreeUdpPort();
        var certificate = DtlsCertificate.GenerateEcdsaP256();
        var peerFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        // No DTLS peer exists — the leg stays unkeyed and must drop all plaintext RTP.
        await using var session = CreateSession(
            localPort, FreeUdpPort(), isClient: false, certificate, peerFingerprint);

        var framesDelivered = 0;
        session.FrameReceived += _ => Interlocked.Increment(ref framesDelivered);
        await session.StartAsync();

        using var sender = new UdpClient();
        var plainRtp = new byte[12 + 160];
        plainRtp[0] = 0x80; // V=2
        plainRtp[1] = 0x00; // PT=0 (PCMU)
        plainRtp[3] = 0x01; // seq
        plainRtp[11] = 0x42; // ssrc
        for (var i = 0; i < 5; i++)
        {
            plainRtp[3]++;
            await sender.SendAsync(plainRtp, new IPEndPoint(IPAddress.Loopback, localPort));
            await Task.Delay(20);
        }

        await Task.Delay(300); // Playout grace period — nothing may surface.
        Assert.Equal(0, framesDelivered);
    }

    [Fact]
    public async Task DtlsNegotiatedLeg_NeverSendsPlaintext_WhileUnkeyed()
    {
        var localPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        var certificate = DtlsCertificate.GenerateEcdsaP256();
        var peerFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        // Passive UDP "peer" that never speaks DTLS — the leg stays unkeyed forever.
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var datagramsAtPeer = 0;
        var receiveLoop = Task.Run(async () =>
        {
            while (true)
            {
                var result = await peer.ReceiveAsync();
                // DTLS ClientHello flights are fine (and absent here: we are the server
                // role) — anything RTP-shaped (first byte 128..191) is a plaintext leak.
                if (result.Buffer.Length > 0 && result.Buffer[0] >= 128)
                    Interlocked.Increment(ref datagramsAtPeer);
            }
        });

        await using var session = CreateSession(
            localPort, peerPort, isClient: false, certificate, peerFingerprint);
        await session.StartAsync();

        var payload = new byte[160];
        for (var i = 0; i < 10; i++)
        {
            await session.SendFrameAsync(new CallAudioFrame(payload, 0, 160));
            await session.SendDtmfAsync(1, 60);
            await Task.Delay(20);
        }

        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref datagramsAtPeer));
        Assert.False(receiveLoop.IsCompleted); // Loop still healthy — nothing swallowed a fault.
    }

    [Fact]
    public async Task FingerprintMismatch_KeepsMediaBlocked()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();
        var wrongFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        // A expects a fingerprint that is not B's — the handshake must fail on both
        // sides (RFC 5763 §6.7.1) and no audio may ever surface.
        await using var sessionA = CreateSession(portA, portB, isClient: true, certA, wrongFingerprint);
        await using var sessionB = CreateSession(portB, portA, isClient: false, certB, certA.Fingerprint);

        var framesDelivered = 0;
        sessionB.FrameReceived += _ => Interlocked.Increment(ref framesDelivered);

        await sessionB.StartAsync();
        await sessionA.StartAsync();

        var payload = new byte[160];
        using var sendWindow = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!sendWindow.IsCancellationRequested)
        {
            await sessionA.SendFrameAsync(new CallAudioFrame(payload, 0, 160));
            try
            {
                await Task.Delay(20, sendWindow.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.Equal(0, framesDelivered);
    }

    [Fact]
    public void DtlsNegotiatedLeg_WithoutDtlsDependencies_FailsClosedAtCreation()
    {
        var parameters = Parameters(FreeUdpPort(), FreeUdpPort(), isClient: false,
            DtlsCertificate.GenerateEcdsaP256().Fingerprint);

        Assert.Throws<InvalidOperationException>(() =>
            new RtpCallMediaSession(parameters, NullLoggerFactory.Instance));
    }

    [Fact]
    public void DtlsNegotiatedLeg_WithoutRemoteFingerprint_FailsClosedAtCreation()
    {
        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            IsDtlsNegotiated = true,
            DtlsIsClient = false,
        };

        Assert.Throws<InvalidOperationException>(() =>
            new RtpCallMediaSession(
                parameters, NullLoggerFactory.Instance, bridgeTapCodec: null,
                new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
                DtlsCertificate.GenerateEcdsaP256()));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CallMediaParameters Parameters(
        int localPort, int remotePort, bool isClient, DtlsFingerprint remoteFingerprint) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        TelephoneEventPayloadType = 101, // lets the leak test exercise the DTMF send path too
        IsDtlsNegotiated = true,
        DtlsIsClient = isClient,
        DtlsRemoteFingerprintAlgorithm = remoteFingerprint.Algorithm,
        DtlsRemoteFingerprintValue = remoteFingerprint.Value,
    };

    private static RtpCallMediaSession CreateSession(
        int localPort, int remotePort, bool isClient,
        DtlsCertificate certificate, DtlsFingerprint remoteFingerprint) =>
        new(Parameters(localPort, remotePort, isClient, remoteFingerprint),
            NullLoggerFactory.Instance, bridgeTapCodec: null,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            certificate);

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
