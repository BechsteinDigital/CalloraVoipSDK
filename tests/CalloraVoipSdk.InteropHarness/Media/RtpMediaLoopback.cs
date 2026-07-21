using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.InteropHarness.Media;

/// <summary>
/// L2-Fixture: zwei <see cref="RtpCallMediaSession"/> über echten UDP-Loopback (plain RTP, PCMU).
/// Kapselt den internen <c>CallAudioFrame</c> und bietet den Test-Projekten eine öffentliche API.
/// </summary>
public sealed class RtpMediaLoopback : IAsyncDisposable
{
    private const int PcmuPayloadType = 0;
    private const int ClockRate = 8000;
    private const int SamplesPerPacket = 160;

    private readonly RtpCallMediaSession _a;
    private readonly RtpCallMediaSession _b;

    private RtpMediaLoopback(RtpCallMediaSession a, RtpCallMediaSession b)
    {
        _a = a;
        _b = b;
    }

    /// <summary>Bindet beide Legs auf Loopback und startet ihre Medienpfade.</summary>
    public static async Task<RtpMediaLoopback> StartAsync()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        var a = new RtpCallMediaSession(Parameters(portA, portB), NullLoggerFactory.Instance);
        try
        {
            var b = new RtpCallMediaSession(Parameters(portB, portA), NullLoggerFactory.Instance);
            try
            {
                await b.StartAsync();
                await a.StartAsync();
                return new RtpMediaLoopback(a, b);
            }
            catch
            {
                await b.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await a.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Sendet <paramref name="payload"/> von Leg A und gibt das erste bei Leg B empfangene
    /// RTP-Payload zurück. Sendet wiederholt (20 ms) bis zum Empfang oder <paramref name="timeout"/>,
    /// um den Playout-Anlauf zu überbrücken.
    /// </summary>
    public async Task<byte[]> RoundTripAsync(byte[] payload, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(CallAudioFrame f) => tcs.TrySetResult((byte[])f.Payload.Clone());
        _b.FrameReceived += OnFrame;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var frame = new CallAudioFrame(payload, PcmuPayloadType, (uint)SamplesPerPacket);
            while (!tcs.Task.IsCompleted)
            {
                cts.Token.ThrowIfCancellationRequested();
                await _a.SendFrameAsync(frame, cts.Token);
                await Task.Delay(20, cts.Token);
            }
            return await tcs.Task;
        }
        finally
        {
            _b.FrameReceived -= OnFrame;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _a.DisposeAsync(); }
        finally { await _b.DisposeAsync(); }
    }

    private static CallMediaParameters Parameters(int localPort, int remotePort) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        PayloadType = PcmuPayloadType,
        ClockRate = ClockRate,
        SamplesPerPacket = SamplesPerPacket,
    };

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
