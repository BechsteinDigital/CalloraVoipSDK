using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDES-keyed video media end to end (RFC 4568): the video m-line derives its own SRTP/SRTCP
/// contexts from per-stream inline key material, so two real <see cref="RtpCallMediaSession"/>
/// instances exchange encrypted video over UDP loopback. A key mismatch delivers nothing,
/// proving the media is genuinely SRTP-protected rather than passed through in the clear.
/// </summary>
public sealed class VideoSdesMediaE2eTests
{
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";

    [Fact]
    public async Task Sdes_keyed_video_frames_round_trip_encrypted()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        // Cross-matched keys: A's outbound key is B's inbound key and vice versa.
        await using var sender = CreateSession(VideoParameters(portA, portB, localSeed: 110, remoteSeed: 120));
        await using var receiver = CreateSession(VideoParameters(portB, portA, localSeed: 120, remoteSeed: 110));

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Video!.FrameReceived += (frame, _, _) => received.TrySetResult(frame);

        await receiver.StartAsync();
        await sender.StartAsync();

        var frame = EncodedVp8Frame(2000);
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        uint ts = 90000;
        while (!received.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await sender.Video!.SendFrameAsync(frame, ts, overall.Token);
            ts += 3000;
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(frame, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Sdes_video_with_a_key_mismatch_delivers_no_frame()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        await using var sender = CreateSession(VideoParameters(portA, portB, localSeed: 110, remoteSeed: 120));
        // The receiver expects the wrong inbound key (199 ≠ 110) — SRTP auth must reject the video.
        await using var receiver = CreateSession(VideoParameters(portB, portA, localSeed: 120, remoteSeed: 199));

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Video!.FrameReceived += (frame, _, _) => received.TrySetResult(frame);

        await receiver.StartAsync();
        await sender.StartAsync();

        var frame = EncodedVp8Frame(2000);
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            uint ts = 90000;
            while (!overall.IsCancellationRequested)
            {
                await sender.Video!.SendFrameAsync(frame, ts, overall.Token);
                ts += 3000;
                await Task.Delay(20, overall.Token);
            }
        }
        catch (OperationCanceledException) { /* expected: we keep sending until the window closes */ }

        Assert.False(received.Task.IsCompleted, "video authenticated under the wrong key — media was not SRTP-protected");
    }

    private static CallMediaParameters VideoParameters(int localVideoPort, int remoteVideoPort, byte localSeed, byte remoteSeed) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        // Marks the leg secure so the video sub-stream is fail-closed; the video contexts come
        // from the video m-line's own inline key material below.
        IsSrtpNegotiated = true,
        Video = new CallVideoParameters
        {
            PayloadType = 96,
            CodecName = "VP8",
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localVideoPort),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remoteVideoPort),
            SrtpSuite = Suite,
            SrtpLocalKeyParams = InlineKey(localSeed),
            SrtpRemoteKeyParams = InlineKey(remoteSeed),
        },
    };

    private static string InlineKey(byte seed)
    {
        var material = new byte[30]; // AES_CM_128_HMAC_SHA1_80: 16-byte key + 14-byte salt
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static byte[] EncodedVp8Frame(int length)
    {
        var vp8 = new byte[length];
        for (var i = 0; i < vp8.Length; i++)
            vp8[i] = (byte)(i * 7);
        return vp8;
    }

    private static RtpCallMediaSession CreateSession(CallMediaParameters parameters) =>
        new(parameters, NullLoggerFactory.Instance);

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
