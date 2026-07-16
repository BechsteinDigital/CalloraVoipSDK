using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The bundle adapter to the call media-session contract (ADR-011 B5-wire b-2): a BUNDLE call runs
/// through <see cref="ICallMediaSession"/> just like the single-stream path. The audio media path
/// (<see cref="ICallMediaSession.SendFrameAsync"/> / <see cref="ICallMediaSession.FrameReceived"/>)
/// works end to end; the features not yet wired on the bundle (DTMF, RTCP-mux) fail closed.
/// </summary>
public sealed class BundledCallMediaSessionTests
{
    private const byte AudioPayloadType = 0;
    private const uint ClientAudioSsrc = 0x0A0A0A0A;
    private const uint ServerAudioSsrc = 0x0C0C0C0C;

    [Fact]
    public async Task Audio_frames_flow_between_two_adapters_over_a_dtls_keyed_bundle()
    {
        var (client, server) = CreatePair();
        await using var clientLease = client;
        await using var serverLease = server;

        var received = new TaskCompletionSource<CallAudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.FrameReceived += f => received.TrySetResult(f);

        await server.StartAsync();
        await client.StartAsync();

        var payload = new byte[] { 9, 8, 7, 6 };
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!received.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await client.SendFrameAsync(new CallAudioFrame(payload, AudioPayloadType, DurationRtpUnits: 160));
            await Task.Delay(20, overall.Token);
        }

        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, frame.Payload);
        Assert.Equal(AudioPayloadType, frame.PayloadType);
    }

    [Fact]
    public async Task Dtmf_is_not_supported_on_a_bundle_leg()
    {
        await using var adapter = SingleAdapter();
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SendDtmfAsync(1, 60));
    }

    [Fact]
    public async Task Rtcp_mux_send_is_not_supported_on_a_bundle_leg()
    {
        await using var adapter = SingleAdapter();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.SendRtcpMuxDatagramAsync(new byte[] { 0x80, 200, 0, 0 }));
    }

    [Fact]
    public async Task The_adapter_exposes_no_video_stream_and_reports_its_audio_ssrc()
    {
        await using var adapter = SingleAdapter();

        Assert.Null(adapter.Video);
        var snapshot = adapter.GetRtpSnapshot();
        Assert.Equal(ClientAudioSsrc, snapshot.LocalSsrc);
        Assert.NotEqual(default, snapshot.CapturedAtUtc);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledCallMediaSession SingleAdapter() =>
        new(BundledMediaSessionBuilder.Build(
            AudioParams(local: 0, remote: 6000, isClient: true, RemoteFp),
            video: null, midExtensionId: 3, audioMid: "audio", audioSsrc: ClientAudioSsrc,
            videoMid: null, videoSsrc: null,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance));

    private static readonly DtlsFingerprint RemoteFp = new() { Algorithm = "sha-256", Value = "AA:BB:CC" };

    private static (BundledCallMediaSession Client, BundledCallMediaSession Server) CreatePair()
    {
        var clientCert = DtlsCertificate.GenerateEcdsaP256();
        var serverCert = DtlsCertificate.GenerateEcdsaP256();

        for (var attempt = 1; ; attempt++)
        {
            var portA = FreeUdpPort();
            var portB = FreeUdpPort();
            BundledCallMediaSession? client = null;
            try
            {
                client = new BundledCallMediaSession(BundledMediaSessionBuilder.Build(
                    AudioParams(portA, portB, isClient: true, serverCert.Fingerprint),
                    null, 3, "audio", ClientAudioSsrc, null, null,
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), clientCert, NullLoggerFactory.Instance));
                var server = new BundledCallMediaSession(BundledMediaSessionBuilder.Build(
                    AudioParams(portB, portA, isClient: false, clientCert.Fingerprint),
                    null, 3, "audio", ServerAudioSsrc, null, null,
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), serverCert, NullLoggerFactory.Instance));
                return (client, server);
            }
            catch (SocketException) when (attempt < 8)
            {
                client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static CallMediaParameters AudioParams(
        int local, int remote, bool isClient, DtlsFingerprint remoteFingerprint) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, local),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remote),
        PayloadType = AudioPayloadType,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        IsDtlsNegotiated = true,
        DtlsIsClient = isClient,
        DtlsRemoteFingerprintAlgorithm = remoteFingerprint.Algorithm,
        DtlsRemoteFingerprintValue = remoteFingerprint.Value,
        IceEnabled = false, // audio flows over the DTLS-keyed transport; ICE consent is covered elsewhere
    };

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
