using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// TURN relay data-path wiring (Slice 4b): when a <c>TurnRelayChannel</c> is supplied, the shared bundle
/// transport frames every outbound datagram as ChannelData to the relay server and unwraps every inbound one
/// from it, below the packet demux — so media (and, by construction, STUN/DTLS) traverse the one bound
/// channel. These tests run a real relay-server socket in the middle and prove the wrap→relay→unwrap→decrypt→
/// route round-trip, plus the relay-mode source filter that drops datagrams that did not come through it.
/// </summary>
public sealed class BundledMediaTransportRelayTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const ushort ChannelNumber = 0x4001;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public async Task Relayed_media_round_trips_through_the_relay_server_to_the_sink()
    {
        using var relayServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)relayServer.Client.LocalEndPoint!;
        var channel = new TurnRelayChannel(relayEndPoint, ChannelNumber);

        var audioTcs = Tcs();
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions
            {
                LocalEndPoint = Loopback(),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 9999), // the peer, reached via the relay
                Relay = channel,
            },
            InboundPipeline(p => audioTcs.TrySetResult(p)), NullLogger<BundledMediaTransport>.Instance);
        await transport.StartAsync();

        var outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), transport, NullLogger<BundledOutboundPipeline>.Instance);
        outbound.RegisterTrack("audio", Track());
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        // Send audio: the transport frames the protected RTP as ChannelData addressed to the relay server.
        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });

        var relayed = await relayServer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(TurnChannelDataCodec.TryParse(relayed.Buffer, out var parsedChannel, out _));
        Assert.Equal(ChannelNumber, parsedChannel); // framed as ChannelData for our channel

        // The relay forwards it back to us (the peer's copy arrives relayed from the server's 5-tuple).
        await relayServer.SendAsync(relayed.Buffer, relayed.Buffer.Length, transport.LocalEndPoint);

        var audio = await audioTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AudioSsrc, audio.Ssrc);                        // unwrapped, decrypted and routed
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Payload.ToArray());
    }

    [Fact]
    public async Task A_datagram_that_did_not_come_through_the_relay_is_dropped()
    {
        using var relayServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)relayServer.Client.LocalEndPoint!;
        var channel = new TurnRelayChannel(relayEndPoint, ChannelNumber);

        var audioTcs = Tcs();
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback(), Relay = channel },
            InboundPipeline(p => audioTcs.TrySetResult(p)), NullLogger<BundledMediaTransport>.Instance);
        await transport.StartAsync();

        // Well-formed ChannelData for our channel, but from a source that is not the relay server: an off-path
        // injection attempt. The relay-mode source filter must drop it before it reaches the pipeline.
        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var forged = channel.Wrap(new byte[] { 0x80, 0x00, 0x11, 0x22 });
        await attacker.SendAsync(forged, forged.Length, transport.LocalEndPoint);

        await Task.Delay(300);
        Assert.False(audioTcs.Task.IsCompleted); // nothing reached the sink
    }

    // ── harness (mirrors BundledMediaTransportTests) ─────────────────────────────

    private static TaskCompletionSource<RtpPacket> Tcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };
}
