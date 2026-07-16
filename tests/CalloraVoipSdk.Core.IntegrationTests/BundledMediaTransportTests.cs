using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The shared 5-tuple of a BUNDLE group (ADR-011 B3-1): one UDP socket driving both the inbound and
/// outbound pipelines. The loopback test wires two transports over real loopback sockets and sends two
/// RTP streams from one out through the socket into the other's inbound pipeline — proving the socket
/// loop, the shared SRTP key, and MID routing compose end to end.
/// </summary>
public sealed class BundledMediaTransportTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;
    private const byte VideoPayloadType = 96;
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const uint VideoSsrc = 0x0B0B0B0B;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public async Task Two_rtp_streams_loop_back_over_the_shared_socket_to_their_sinks()
    {
        var audioTcs = Tcs();
        var videoTcs = Tcs();

        var receiverInbound = InboundPipeline(
            p => audioTcs.TrySetResult(p), p => videoTcs.TrySetResult(p));
        await using var receiver = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback() },
            receiverInbound, NullLogger<BundledMediaTransport>.Instance);
        await receiver.StartAsync();

        await using var senderTransport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback(), RemoteEndPoint = receiver.LocalEndPoint },
            InboundPipeline(_ => { }, _ => { }), NullLogger<BundledMediaTransport>.Instance);

        var outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), senderTransport, NullLogger<BundledOutboundPipeline>.Instance);
        outbound.RegisterTrack("audio", Track(AudioSsrc, AudioPayloadType, "audio"));
        outbound.RegisterTrack("video", Track(VideoSsrc, VideoPayloadType, "video"));
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });
        await outbound.SendAsync("video", new byte[] { 9, 8, 7, 6 });

        var audio = await audioTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var video = await videoTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AudioSsrc, audio.Ssrc);
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Payload.ToArray());
        Assert.Equal(VideoSsrc, video.Ssrc);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, video.Payload.ToArray());
    }

    [Fact]
    public async Task Binds_a_local_endpoint_and_disposes_cleanly()
    {
        var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback() },
            InboundPipeline(_ => { }, _ => { }), NullLogger<BundledMediaTransport>.Instance);
        await transport.StartAsync();

        Assert.NotEqual(0, transport.LocalEndPoint.Port); // an ephemeral bind resolved to a real port

        await transport.DisposeAsync(); // stops the loop and disposes the socket without hanging
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static TaskCompletionSource<RtpPacket> Tcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IPEndPoint Loopback() => new(IPAddress.Loopback, 0);

    private static BundledInboundPipeline InboundPipeline(Action<RtpPacket> onAudio, Action<RtpPacket> onVideo)
    {
        var demux = BundledRtpDemultiplexerFactory.Create(
            MidExtId,
            new Dictionary<string, IReadOnlyCollection<int>>
            {
                ["audio"] = new[] { (int)AudioPayloadType },
                ["video"] = new[] { (int)VideoPayloadType },
            });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("audio", onAudio);
        router.RegisterTrack("video", onVideo);
        var pipeline = new BundledInboundPipeline(
            router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
        pipeline.InstallInboundKeys(new SrtpContext(Material()), new SrtcpContext(Material()));
        return pipeline;
    }

    private static BundledOutboundTrack Track(uint ssrc, byte payloadType, string mid) =>
        new(ssrc, payloadType, samplesPerPacket: 160,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, mid),
            initialSequenceNumber: 1000, initialTimestamp: 5000);

    private static SrtpKeyMaterial Material() =>
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };
}
