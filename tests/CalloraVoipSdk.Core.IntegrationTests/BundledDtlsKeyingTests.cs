using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// DTLS-SRTP keying of a bundled transport end to end (ADR-011 B3-2, RFC 5763/5764): two bundled
/// transports over loopback run one shared DTLS handshake each, and the derived contexts are installed
/// into both the inbound and outbound pipelines. Before the handshake the pipelines fail closed, so a
/// frame arriving at the peer proves keys reached both sides; a fingerprint mismatch keeps media blocked.
/// </summary>
public sealed class BundledDtlsKeyingTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;
    private const byte VideoPayloadType = 96;
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const uint VideoSsrc = 0x0B0B0B0B;

    [Fact]
    public async Task Dtls_handshake_over_the_bundle_keys_both_pipelines_and_media_flows()
    {
        var clientCert = DtlsCertificate.GenerateEcdsaP256();
        var serverCert = DtlsCertificate.GenerateEcdsaP256();

        var clientInbound = InboundPipeline(out _);
        var serverInbound = InboundPipeline(out var serverAudio);

        await using var clientTransport = Transport(clientInbound);
        await using var serverTransport = Transport(serverInbound);
        clientTransport.SetRemoteEndPoint(serverTransport.LocalEndPoint);
        serverTransport.SetRemoteEndPoint(clientTransport.LocalEndPoint);

        var clientOutbound = Outbound(clientTransport);
        var serverOutbound = Outbound(serverTransport);

        await using var clientKeying = Keying(
            isClient: true, serverTransport.LocalEndPoint, serverCert.Fingerprint, clientCert,
            clientInbound, clientOutbound, clientTransport);
        await using var serverKeying = Keying(
            isClient: false, clientTransport.LocalEndPoint, clientCert.Fingerprint, serverCert,
            serverInbound, serverOutbound, serverTransport);

        await clientTransport.StartAsync();
        await serverTransport.StartAsync();
        clientKeying.Start();
        serverKeying.Start();

        // Sends are suppressed until the handshake keys the outbound pipeline; keep sending so the first
        // frame that lands proves keys reached both the client's send path and the server's receive path.
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!serverAudio.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await clientOutbound.SendAsync("audio", new byte[] { 1, 2, 3 });
            await Task.Delay(20, overall.Token);
        }

        var audio = await serverAudio.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AudioSsrc, audio.Ssrc);
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Payload.ToArray());
    }

    [Fact]
    public async Task A_fingerprint_mismatch_keeps_media_blocked()
    {
        var clientCert = DtlsCertificate.GenerateEcdsaP256();
        var serverCert = DtlsCertificate.GenerateEcdsaP256();
        var wrongFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        var clientInbound = InboundPipeline(out _);
        var serverInbound = InboundPipeline(out var serverAudio);

        await using var clientTransport = Transport(clientInbound);
        await using var serverTransport = Transport(serverInbound);
        clientTransport.SetRemoteEndPoint(serverTransport.LocalEndPoint);
        serverTransport.SetRemoteEndPoint(clientTransport.LocalEndPoint);

        var clientOutbound = Outbound(clientTransport);
        var serverOutbound = Outbound(serverTransport);

        // The client expects a fingerprint that is not the server's — the handshake must fail (RFC 5763
        // §6.7.1) and neither side may key.
        await using var clientKeying = Keying(
            isClient: true, serverTransport.LocalEndPoint, wrongFingerprint, clientCert,
            clientInbound, clientOutbound, clientTransport);
        await using var serverKeying = Keying(
            isClient: false, clientTransport.LocalEndPoint, clientCert.Fingerprint, serverCert,
            serverInbound, serverOutbound, serverTransport);

        await clientTransport.StartAsync();
        await serverTransport.StartAsync();
        clientKeying.Start();
        serverKeying.Start();

        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!window.IsCancellationRequested)
        {
            await clientOutbound.SendAsync("audio", new byte[] { 1, 2, 3 });
            try { await Task.Delay(20, window.Token); }
            catch (OperationCanceledException) { break; }
        }

        Assert.False(serverAudio.Task.IsCompleted); // no frame ever decrypted — media stayed blocked
    }

    [Fact]
    public async Task Dtls_inbound_filter_follows_ice_renomination_to_a_different_source()
    {
        var clientCert = DtlsCertificate.GenerateEcdsaP256();
        var serverCert = DtlsCertificate.GenerateEcdsaP256();

        var clientInbound = InboundPipeline(out _);
        var serverInbound = InboundPipeline(out var serverAudio);

        await using var clientTransport = Transport(clientInbound);
        await using var serverTransport = Transport(serverInbound);
        clientTransport.SetRemoteEndPoint(serverTransport.LocalEndPoint);
        serverTransport.SetRemoteEndPoint(clientTransport.LocalEndPoint);

        var clientOutbound = Outbound(clientTransport);
        var serverOutbound = Outbound(serverTransport);

        // The client's DTLS keying starts pointed at a STALE endpoint (an initial SDP candidate that is not
        // the working pair), so its strict inbound source filter drops the server's handshake flights — the
        // exact pre-nomination state the earlier tests never reproduce (there initial == working). The send
        // side already targets the real server via the transport, so only the inbound filter is wrong.
        var stale = new IPEndPoint(IPAddress.Loopback, 1); // never an ephemeral bind → differs from the server
        await using var clientKeying = Keying(
            isClient: true, stale, serverCert.Fingerprint, clientCert,
            clientInbound, clientOutbound, clientTransport);
        await using var serverKeying = Keying(
            isClient: false, clientTransport.LocalEndPoint, clientCert.Fingerprint, serverCert,
            serverInbound, serverOutbound, serverTransport);

        await clientTransport.StartAsync();
        await serverTransport.StartAsync();
        clientKeying.Start();
        serverKeying.Start();

        // While the DTLS filter points at the stale endpoint, the server's flights are dropped: nothing keys.
        using (var blocked = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
        {
            while (!blocked.IsCancellationRequested)
            {
                await clientOutbound.SendAsync("audio", new byte[] { 1, 2, 3 });
                try { await Task.Delay(20, blocked.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        Assert.False(serverAudio.Task.IsCompleted); // blocked by the stale DTLS source filter

        // ICE nominates the real pair → the DTLS filter follows it (the fix) → the handshake completes.
        clientKeying.SetRemoteEndPoint(serverTransport.LocalEndPoint);

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!serverAudio.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await clientOutbound.SendAsync("audio", new byte[] { 1, 2, 3 });
            await Task.Delay(20, overall.Token);
        }

        var audio = await serverAudio.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Payload.ToArray());
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledMediaTransport Transport(BundledInboundPipeline inbound) =>
        new(new BundledMediaTransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            inbound, NullLogger<BundledMediaTransport>.Instance);

    private static BundledInboundPipeline InboundPipeline(out TaskCompletionSource<RtpPacket> audio)
    {
        var audioTcs = new TaskCompletionSource<RtpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        audio = audioTcs;
        var demux = BundledRtpDemultiplexerFactory.Create(
            MidExtId,
            new Dictionary<string, IReadOnlyCollection<int>>
            {
                ["audio"] = new[] { (int)AudioPayloadType },
                ["video"] = new[] { (int)VideoPayloadType },
            });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("audio", p => audioTcs.TrySetResult(p));
        router.RegisterTrack("video", _ => { });
        // No keys installed here — the DTLS handshake installs them.
        return new BundledInboundPipeline(router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
    }

    private static BundledOutboundPipeline Outbound(IBundledDatagramSender sender)
    {
        var pipeline = new BundledOutboundPipeline(
            new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("audio", Track(AudioSsrc, AudioPayloadType, "audio"));
        pipeline.RegisterTrack("video", Track(VideoSsrc, VideoPayloadType, "video"));
        return pipeline;
    }

    private static BundledOutboundTrack Track(uint ssrc, byte payloadType, string mid) =>
        new(ssrc, payloadType, samplesPerPacket: 160,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, mid),
            initialSequenceNumber: 1000, initialTimestamp: 5000);

    private static BundledDtlsKeying Keying(
        bool isClient,
        IPEndPoint remoteEndPoint,
        DtlsFingerprint expectedRemoteFingerprint,
        DtlsCertificate certificate,
        BundledInboundPipeline inbound,
        BundledOutboundPipeline outbound,
        IBundledDatagramSender sender)
    {
        return new BundledDtlsKeying(
            isClient, remoteEndPoint, expectedRemoteFingerprint,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certificate,
            inbound, outbound, sender, onHandshakeFailed: () => { }, NullLoggerFactory.Instance);
    }
}
