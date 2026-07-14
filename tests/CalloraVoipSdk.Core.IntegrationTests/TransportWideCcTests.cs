using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-wide sequence number (transport-cc / RFC 8888), BWE slice 3: the negotiated
/// <c>a=extmap</c> id is surfaced into the video parameters, and the RTP session stamps a monotonic
/// transport-wide counter in an RFC 8285 header extension on outgoing packets when the id is set.
/// </summary>
public sealed class TransportWideCcTests
{
    private const string TransportCc = RtpHeaderExtensionUris.TransportWideCc;
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 41000);
    private static readonly RtpPacketCodec RtpCodec = new();

    // ── SDP resolution: surface the negotiated extmap id ─────────────────────────

    [Fact]
    public void Media_parameters_surface_the_transport_cc_extension_id()
    {
        var parameters = SdpUtilities.TryParseMediaParameters(
            VideoOfferWithExtmap($"a=extmap:5 {TransportCc}\r\n"), LocalAudio,
            new SdpMediaNegotiationOptions { Video = new SdpVideoNegotiationOptions { Port = 41002 } });

        Assert.NotNull(parameters?.Video);
        Assert.Equal((byte)5, parameters!.Video!.TransportWideCcExtensionId);
    }

    [Fact]
    public void Media_parameters_have_no_transport_cc_id_without_the_extmap()
    {
        var parameters = SdpUtilities.TryParseMediaParameters(
            VideoOfferWithExtmap(string.Empty), LocalAudio,
            new SdpMediaNegotiationOptions { Video = new SdpVideoNegotiationOptions { Port = 41002 } });

        Assert.NotNull(parameters?.Video);
        Assert.Null(parameters!.Video!.TransportWideCcExtensionId);
    }

    [Fact]
    public void Media_parameters_ignore_an_unrelated_extmap()
    {
        var parameters = SdpUtilities.TryParseMediaParameters(
            VideoOfferWithExtmap("a=extmap:5 urn:ietf:params:rtp-hdrext:unknown\r\n"), LocalAudio,
            new SdpMediaNegotiationOptions { Video = new SdpVideoNegotiationOptions { Port = 41002 } });

        Assert.NotNull(parameters?.Video);
        Assert.Null(parameters!.Video!.TransportWideCcExtensionId);
    }

    // ── RTP session: stamp the transport-wide counter ────────────────────────────

    [Fact]
    public async Task Rtp_session_stamps_a_monotonic_transport_cc_sequence_when_configured()
    {
        const byte extId = 5;
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(FreeUdpPort(), peerPort, transportCcId: extId);
        await session.StartAsync();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var seqs = new List<ushort>();
        for (var i = 0; i < 3; i++)
        {
            await session.SendTimestampedAsync(new byte[] { 0x10, (byte)i }, marker: true, payloadType: 96, timestamp: 9000);
            var datagram = (await peer.ReceiveAsync(timeout.Token)).Buffer;
            seqs.Add(ReadTransportCcSeq(datagram, extId));
        }

        Assert.Equal(new ushort[] { 0, 1, 2 }, seqs.ToArray());
    }

    [Fact]
    public async Task Rtp_session_stamps_no_extension_without_the_option()
    {
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(FreeUdpPort(), peerPort, transportCcId: null);
        await session.StartAsync();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));

        await session.SendTimestampedAsync(new byte[] { 0x10, 0x00 }, marker: true, payloadType: 96, timestamp: 9000);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var datagram = (await peer.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Null(RtpCodec.Decode(datagram).HeaderExtension);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ushort ReadTransportCcSeq(byte[] datagram, byte id)
    {
        var extension = RtpCodec.Decode(datagram).HeaderExtension;
        Assert.NotNull(extension);
        var element = Assert.Single(OneByteRtpHeaderExtensions.Parse(extension!).Where(e => e.Id == id));
        return BinaryPrimitives.ReadUInt16BigEndian(element.Value.Span);
    }

    private static RtpSession CreateSession(int localPort, int remotePort, byte? transportCcId) =>
        new(new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
                PayloadType = 96,
                ClockRate = 90000,
                SamplesPerPacket = 3000,
                TransportWideCcExtensionId = transportCcId,
            },
            RtpCodec, NullLogger<RtpSession>.Instance);

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static string VideoOfferWithExtmap(string extmapLine) =>
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
        + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n"
        + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n" + extmapLine;
}
