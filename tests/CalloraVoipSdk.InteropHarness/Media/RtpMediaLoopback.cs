using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.InteropHarness.Metrics;
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

    /// <summary>
    /// Bindet beide Legs auf freie Loopback-Ports und startet ihre Medienpfade. Wiederholt bei
    /// Port-Bind-Kollision (<see cref="System.Net.Sockets.SocketError.AddressAlreadyInUse"/>) mit
    /// frischen Ports bis zu <paramref name="maxAttempts"/> mal. <paramref name="metricsPublishInterval"/>
    /// steuert das Laufzeit-Metrik-Publish-Intervall beider Legs (<see langword="null"/> = SDK-Default 1 s).
    /// </summary>
    public static async Task<RtpMediaLoopback> StartAsync(
        int maxAttempts = 5, TimeSpan? metricsPublishInterval = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryStartOnceAsync(metricsPublishInterval);
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < maxAttempts)
            {
                // Port zwischen Probe und Bind belegt — mit frischen Ports erneut versuchen.
            }
        }
    }

    private static async Task<RtpMediaLoopback> TryStartOnceAsync(TimeSpan? metricsPublishInterval)
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        var a = CreateSession(portA, portB, metricsPublishInterval);
        try
        {
            var b = CreateSession(portB, portA, metricsPublishInterval);
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

    private static RtpCallMediaSession CreateSession(
        int localPort, int remotePort, TimeSpan? metricsPublishInterval) =>
        new(Parameters(localPort, remotePort), NullLoggerFactory.Instance,
            jitterBufferOptions: null, playoutInterval: null, metricsPublishInterval: metricsPublishInterval);

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

    /// <summary>
    /// Sendet <paramref name="duration"/> lang kontinuierlich Frames von Leg A (alle
    /// <paramref name="frameInterval"/>) und sammelt die bei Leg B gemeldeten Empfangs-Qualitäts-
    /// Snapshots. Für Qualitäts-Drift-Soaks: ein langer Call statt vieler kurzer.
    /// </summary>
    public async Task<IReadOnlyList<MediaQualitySnapshot>> RunAndCollectQualityAsync(
        TimeSpan duration, TimeSpan frameInterval)
    {
        var snapshots = new List<MediaQualitySnapshot>();
        var gate = new object();
        void OnMetrics(CallMediaRuntimeMetrics m)
        {
            lock (gate) snapshots.Add(MediaQualitySnapshot.From(m));
        }

        _b.RuntimeMetricsUpdated += OnMetrics;
        try
        {
            using var cts = new CancellationTokenSource(duration);
            var frame = new CallAudioFrame(new byte[160], PcmuPayloadType, (uint)SamplesPerPacket);
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await _a.SendFrameAsync(frame, cts.Token);
                    await Task.Delay(frameInterval, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Erwartetes Ende der Lauf-Dauer.
            }

            lock (gate) return snapshots.ToArray();
        }
        finally
        {
            _b.RuntimeMetricsUpdated -= OnMetrics;
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
