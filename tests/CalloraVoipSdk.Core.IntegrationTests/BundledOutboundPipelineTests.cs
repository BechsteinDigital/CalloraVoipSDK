using System.Net;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The bundled transport's outbound pipeline (ADR-011 B2c-in-4): per-m-line tracks stamp their MID and
/// advance their own SSRC/sequence/timestamp, the shared outbound SRTP context encrypts every packet,
/// and it leaves over the shared 5-tuple. BUNDLE media is DTLS-SRTP only, so sends fail closed until the
/// key is installed. The full-loop test feeds the sent datagrams back through the inbound pipeline
/// (B2c-in-3) to prove send and receive compose over one shared key.
/// </summary>
public sealed class BundledOutboundPipelineTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;
    private const byte VideoPayloadType = 96;
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const ushort InitialSeq = 1000;
    private const uint InitialTimestamp = 5000;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");
    private static readonly IPEndPoint Peer = new(IPAddress.Loopback, 40000);

    [Fact]
    public async Task Two_tracks_send_stamped_with_their_mid_and_loop_back_to_the_right_sink()
    {
        var (outbound, sender) = Outbound();
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });
        await outbound.SendAsync("video", new byte[] { 9, 8, 7, 6 }, marker: true);

        // Round-trip the two sent datagrams through the inbound pipeline over a matching receiver key.
        var inbound = Inbound(out var audio, out var video);
        foreach (var datagram in sender.Datagrams)
            inbound.ProcessInboundDatagram(datagram, Peer);

        var a = Assert.Single(audio);
        Assert.Equal(AudioSsrc, a.Ssrc);
        Assert.Equal(AudioPayloadType, a.PayloadType);
        Assert.Equal(new byte[] { 1, 2, 3 }, a.Payload.ToArray());

        var v = Assert.Single(video);
        Assert.Equal(VideoSsrc, v.Ssrc);
        Assert.Equal(VideoPayloadType, v.PayloadType);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, v.Payload.ToArray());
    }

    [Fact]
    public async Task Each_track_advances_its_own_sequence_number()
    {
        var (outbound, sender) = Outbound();
        var receiver = new SrtpContext(Material());
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        await outbound.SendAsync("audio", new byte[] { 1 });
        await outbound.SendAsync("audio", new byte[] { 2 });
        await outbound.SendAsync("video", new byte[] { 3 });

        Assert.Equal(InitialSeq, Decode(sender.Datagrams[0], receiver).SequenceNumber);     // audio #1
        Assert.Equal(InitialSeq + 1, Decode(sender.Datagrams[1], receiver).SequenceNumber); // audio #2
        Assert.Equal(InitialSeq, Decode(sender.Datagrams[2], receiver).SequenceNumber);     // video, own space
    }

    [Fact]
    public async Task A_send_before_keying_fails_closed_and_does_not_consume_the_sequence_cursor()
    {
        var (outbound, sender) = Outbound();

        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 }); // suppressed — no key yet

        Assert.Empty(sender.Datagrams);
        Assert.Equal(1, outbound.SuppressedSends);

        // After keying the very next packet is still the first sequence number — the drop did not
        // advance the cursor, so the peer sees a contiguous stream from the first real send.
        var receiver = new SrtpContext(Material());
        outbound.InstallOutboundKey(new SrtpContext(Material()));
        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });

        Assert.Equal(InitialSeq, Decode(Assert.Single(sender.Datagrams), receiver).SequenceNumber);
    }

    [Fact]
    public async Task Sending_on_an_unregistered_mid_throws()
    {
        var (outbound, _) = Outbound();
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await outbound.SendAsync("screenshare", new byte[] { 1 }));
    }

    [Fact]
    public async Task Packet_sent_fires_after_a_successful_send()
    {
        var (outbound, _) = Outbound();
        outbound.InstallOutboundKey(new SrtpContext(Material()));
        RtpPacket? sent = null;
        outbound.PacketSent += p => sent = p;

        await outbound.SendAsync("audio", new byte[] { 7 });

        Assert.NotNull(sent);
        Assert.Equal(AudioSsrc, sent!.Ssrc);
    }

    [Fact]
    public async Task Sending_advances_the_tracks_sender_report_counters()
    {
        var (outbound, _) = Outbound();
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });
        await outbound.SendAsync("audio", new byte[] { 4, 5 });

        var report = Assert.Single(outbound.SnapshotSenderReports()); // only the sent track reports
        Assert.Equal(AudioSsrc, report.Ssrc);
        Assert.Equal(2, report.PacketCount);
        Assert.Equal(5, report.OctetCount);                       // 3 + 2 payload octets, headers excluded
        Assert.Equal(InitialTimestamp + 160u, report.LastRtpTimestamp); // 2nd packet's ts (cursor advanced by samplesPerPacket)
    }

    [Fact]
    public async Task Rtcp_send_fails_closed_until_the_srtcp_key_is_installed()
    {
        var (outbound, sender) = Outbound();

        await outbound.SendRtcpAsync(new byte[] { 0x80, 0xC8, 0, 0 }, CancellationToken.None); // suppressed

        Assert.Empty(sender.Datagrams);
        Assert.Equal(1, outbound.RtcpSuppressedSends);
        Assert.Equal(0, outbound.RtcpPacketsSent);
    }

    [Fact]
    public async Task Rtcp_send_protects_and_leaves_over_the_shared_socket_after_keying()
    {
        var (outbound, sender) = Outbound();
        var receiver = new SrtcpContext(Material());
        outbound.InstallOutboundRtcpKey(new SrtcpContext(Material()));

        // A minimal well-formed RTCP compound (an empty Receiver Report) is enough to round-trip through SRTCP.
        var plaintext = new RtcpPacketCodec().Encode(
            new[] { new RtcpReceiverReport { Ssrc = AudioSsrc } });
        await outbound.SendRtcpAsync(plaintext, CancellationToken.None);

        Assert.Equal(1, outbound.RtcpPacketsSent);
        var protectedDatagram = Assert.Single(sender.Datagrams);
        Assert.NotEqual(plaintext, protectedDatagram); // it left encrypted, not as plaintext

        // The paired inbound SRTCP context recovers the original compound.
        var recovered = receiver.UnprotectRtcp(protectedDatagram);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public async Task Timestamped_send_uses_the_explicit_timestamp_and_leaves_the_cursor_untouched()
    {
        var (outbound, sender) = Outbound();
        var receiver = new SrtpContext(Material());
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        await outbound.SendTimestampedAsync("audio", new byte[] { 1 }, marker: true, AudioPayloadType, timestamp: 99999);
        await outbound.SendAsync("audio", new byte[] { 2 }); // uses the running cursor, still at its initial value

        Assert.Equal(99999u, Decode(sender.Datagrams[0], receiver).Timestamp);
        Assert.Equal(InitialTimestamp, Decode(sender.Datagrams[1], receiver).Timestamp);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static (BundledOutboundPipeline pipeline, CapturingSender sender) Outbound()
    {
        var sender = new CapturingSender();
        var pipeline = new BundledOutboundPipeline(
            new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("audio", Track(AudioSsrc, AudioPayloadType, "audio"));
        pipeline.RegisterTrack("video", Track(VideoSsrc, VideoPayloadType, "video"));
        return (pipeline, sender);
    }

    private static BundledOutboundTrack Track(uint ssrc, byte payloadType, string mid) =>
        new(ssrc, payloadType, samplesPerPacket: 160,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, mid),
            InitialSeq, InitialTimestamp);

    private static BundledInboundPipeline Inbound(out List<RtpPacket> audio, out List<RtpPacket> video)
    {
        var captured = (audio: new List<RtpPacket>(), video: new List<RtpPacket>());
        var demux = BundledRtpDemultiplexerFactory.Create(
            MidExtId,
            new Dictionary<string, IReadOnlyCollection<int>>
            {
                ["audio"] = new[] { (int)AudioPayloadType },
                ["video"] = new[] { (int)VideoPayloadType },
            });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("audio", captured.audio.Add);
        router.RegisterTrack("video", captured.video.Add);
        var pipeline = new BundledInboundPipeline(
            router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
        pipeline.InstallInboundKeys(new SrtpContext(Material()), new SrtcpContext(Material()));
        audio = captured.audio;
        video = captured.video;
        return pipeline;
    }

    private static RtpPacket Decode(byte[] srtpDatagram, SrtpContext receiver) =>
        new RtpPacketCodec().Decode(receiver.Unprotect(srtpDatagram));

    private static SrtpKeyMaterial Material() =>
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };

    private sealed class CapturingSender : IBundledDatagramSender
    {
        public List<byte[]> Datagrams { get; } = [];

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            Datagrams.Add(datagram.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
