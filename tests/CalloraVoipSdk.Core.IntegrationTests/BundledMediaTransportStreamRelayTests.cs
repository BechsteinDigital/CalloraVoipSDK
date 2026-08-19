using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The stream relay media path on the shared transport (ADR-073 media path 2/4, #240): once a stream relay pair
/// is nominated, <see cref="BundledMediaTransport.EnterStreamRelayMode"/> routes every media send over the stream
/// transport's ChannelData path — a different transport chosen by nomination (the reference model), not the
/// in-place UDP socket switch — and <see cref="BundledMediaTransport.InjectRelayedInbound"/> feeds the stream's
/// relayed inbound into the same pipeline. This proves the full send-over-stream → inject → decrypt → route
/// round-trip, that inbound injection is inert before the mode is entered, and the mutual exclusion with the UDP
/// whole-socket relay.
/// </summary>
public sealed class BundledMediaTransportStreamRelayTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;
    private const uint AudioSsrc = 0x0A0A0A0A;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public async Task Stream_relay_mode_routes_media_over_the_stream_and_injects_inbound()
    {
        var peer = new IPEndPoint(IPAddress.Loopback, 9999); // reached only over the stream relay
        var audioTcs = new TaskCompletionSource<RtpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback(), RemoteEndPoint = peer },
            InboundPipeline(p => audioTcs.TrySetResult(p)), NullLogger<BundledMediaTransport>.Instance);
        await transport.StartAsync();

        // Nomination of a stream relay pair: media now rides the stream transport's send (stand-in here records
        // what would be framed as ChannelData over the stream), not the UDP socket.
        var onStream = new List<byte[]>();
        transport.EnterStreamRelayMode((datagram, ct) =>
        {
            lock (onStream) onStream.Add(datagram.ToArray());
            return ValueTask.CompletedTask;
        });

        var outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), transport, NullLogger<BundledOutboundPipeline>.Instance);
        outbound.RegisterTrack("audio", Track());
        outbound.InstallOutboundKey(new SrtpContext(Material()));
        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });

        byte[] protectedRtp;
        lock (onStream)
        {
            Assert.Single(onStream);      // the protected RTP went over the stream send, not the UDP socket
            protectedRtp = onStream[0];
        }

        // The peer's copy arrives relayed inbound over the stream (the inner payload of a ChannelData frame):
        // injected into the same pipeline, attributed to the peer, and decrypted + routed to the audio sink.
        transport.InjectRelayedInbound(protectedRtp, peer);

        var audio = await audioTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AudioSsrc, audio.Ssrc);
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Payload.ToArray());
    }

    [Fact]
    public async Task InjectRelayedInbound_is_inert_before_stream_relay_mode()
    {
        var peer = new IPEndPoint(IPAddress.Loopback, 9999);
        var surfaced = 0;

        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback(), RemoteEndPoint = peer },
            InboundPipeline(_ => Interlocked.Increment(ref surfaced)), NullLogger<BundledMediaTransport>.Instance);
        await transport.StartAsync();

        // No stream relay pair nominated yet → injection is a no-op (nothing rides the stream before then).
        transport.InjectRelayedInbound(new byte[] { 0x80, 0x00, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0, 0 }, peer);
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref surfaced));
    }

    [Fact]
    public async Task EnterStreamRelayMode_conflicts_with_the_udp_whole_socket_relay()
    {
        var peer = new IPEndPoint(IPAddress.Loopback, 9999);
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback(), RemoteEndPoint = peer },
            InboundPipeline(_ => { }), NullLogger<BundledMediaTransport>.Instance);
        await transport.StartAsync();

        transport.EnterRelayMode(new IPEndPoint(IPAddress.Loopback, 3478), onControl: null); // UDP whole-socket relay

        Assert.Throws<InvalidOperationException>(() =>
            transport.EnterStreamRelayMode((_, _) => ValueTask.CompletedTask));
    }

    private static IPEndPoint Loopback() => new(IPAddress.Loopback, 0);

    private static BundledInboundPipeline InboundPipeline(Action<RtpPacket> onAudio)
    {
        var demux = BundledRtpDemultiplexerFactory.Create(
            MidExtId,
            new Dictionary<string, IReadOnlyCollection<int>> { ["audio"] = new[] { (int)AudioPayloadType } });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("audio", onAudio);
        var pipeline = new BundledInboundPipeline(
            router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
        pipeline.InstallInboundKeys(new SrtpContext(Material()), new SrtcpContext(Material()));
        return pipeline;
    }

    private static BundledOutboundTrack Track() =>
        new(AudioSsrc, AudioPayloadType, samplesPerPacket: 160,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "audio"),
            initialSequenceNumber: 1000, initialTimestamp: 5000);

    private static SrtpKeyMaterial Material() =>
        new(MasterKey, MasterSalt, SrtpCryptoSuite.AesCm128HmacSha1_80);
}
