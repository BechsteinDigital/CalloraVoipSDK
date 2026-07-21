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
/// L2-Fixture: zwei <see cref="RtpCallMediaSession"/> über echten UDP-Loopback. Konfigurierbar über
/// Codec (PCMU/Opus, transport-only opake Payload) und Sicherheit (Plain RTP / SRTP via SDES).
/// Kapselt den internen <c>CallAudioFrame</c> und bietet den Test-Projekten eine öffentliche API.
/// </summary>
public sealed class RtpMediaLoopback : IAsyncDisposable
{
    // SDES-Keying für den SRTP-Modus (RFC 4568). Zwei feste Master-Keys, richtungsweise getauscht:
    // was Leg A lokal sendet, entschlüsselt Leg B als Remote — und umgekehrt.
    private const string SrtpSuite = "AES_CM_128_HMAC_SHA1_80";
    private const byte KeySeedA = 70;
    private const byte KeySeedB = 90;

    private readonly RtpCallMediaSession _a;
    private readonly RtpCallMediaSession _b;
    private readonly int _portA;
    private readonly int _portB;
    private readonly int _payloadType;
    private readonly int _samplesPerPacket;

    private RtpMediaLoopback(
        RtpCallMediaSession a, RtpCallMediaSession b,
        int portA, int portB, int payloadType, int samplesPerPacket)
    {
        _a = a;
        _b = b;
        _portA = portA;
        _portB = portB;
        _payloadType = payloadType;
        _samplesPerPacket = samplesPerPacket;
    }

    /// <summary>Das gebundene Loopback-Portpaar (Leg A ↔ Leg B) — für Soak-Fehlerdiagnose.</summary>
    public (int LegA, int LegB) PortPair => (_portA, _portB);

    /// <summary>
    /// Bindet beide Legs auf freie Loopback-Ports und startet ihre Medienpfade. Wiederholt bei
    /// Port-Bind-Kollision (<see cref="System.Net.Sockets.SocketError.AddressAlreadyInUse"/>) mit
    /// frischen Ports bis zu <paramref name="maxAttempts"/> mal. <paramref name="metricsPublishInterval"/>
    /// steuert das Laufzeit-Metrik-Publish-Intervall beider Legs (<see langword="null"/> = SDK-Default 1 s).
    /// <paramref name="codec"/>/<paramref name="security"/> wählen Codec-Profil und Transport-Sicherheit.
    /// </summary>
    public static async Task<RtpMediaLoopback> StartAsync(
        int maxAttempts = 5,
        TimeSpan? metricsPublishInterval = null,
        LoopbackCodec codec = LoopbackCodec.Pcmu,
        LoopbackSecurity security = LoopbackSecurity.Plain)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryStartOnceAsync(metricsPublishInterval, codec, security);
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < maxAttempts)
            {
                // Port zwischen Probe und Bind belegt — mit frischen Ports erneut versuchen.
            }
        }
    }

    private static async Task<RtpMediaLoopback> TryStartOnceAsync(
        TimeSpan? metricsPublishInterval, LoopbackCodec codec, LoopbackSecurity security)
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var (payloadType, clockRate, samples) = CodecSpec(codec);

        // Bei SRTP teilen beide Legs dieselben Master-Keys, richtungsweise getauscht.
        var (localA, remoteA, localB, remoteB) = security == LoopbackSecurity.Srtp
            ? (InlineKey(KeySeedA), InlineKey(KeySeedB), InlineKey(KeySeedB), InlineKey(KeySeedA))
            : (null, null, null, null);

        var a = CreateSession(portA, portB, payloadType, clockRate, samples, localA, remoteA, metricsPublishInterval);
        try
        {
            var b = CreateSession(portB, portA, payloadType, clockRate, samples, localB, remoteB, metricsPublishInterval);
            try
            {
                await b.StartAsync();
                await a.StartAsync();
                return new RtpMediaLoopback(a, b, portA, portB, payloadType, samples);
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
        int localPort, int remotePort, int payloadType, int clockRate, int samples,
        string? srtpLocalKey, string? srtpRemoteKey, TimeSpan? metricsPublishInterval) =>
        new(Parameters(localPort, remotePort, payloadType, clockRate, samples, srtpLocalKey, srtpRemoteKey),
            NullLoggerFactory.Instance,
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
            var frame = new CallAudioFrame(payload, _payloadType, (uint)_samplesPerPacket);
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
            var frame = new CallAudioFrame(new byte[160], _payloadType, (uint)_samplesPerPacket);
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

    private static (int payloadType, int clockRate, int samplesPerPacket) CodecSpec(LoopbackCodec codec) => codec switch
    {
        LoopbackCodec.Pcmu => (0, 8000, 160),
        LoopbackCodec.Opus => (111, 48000, 960),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unbekannter Loopback-Codec."),
    };

    private static CallMediaParameters Parameters(
        int localPort, int remotePort, int payloadType, int clockRate, int samples,
        string? srtpLocalKey, string? srtpRemoteKey) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        PayloadType = payloadType,
        ClockRate = clockRate,
        SamplesPerPacket = samples,
        MediaProfile = srtpLocalKey is null ? "RTP/AVP" : "RTP/SAVP",
        IsSrtpNegotiated = srtpLocalKey is not null,
        SrtpSuite = srtpLocalKey is null ? null : SrtpSuite,
        SrtpLocalKeyParams = srtpLocalKey,
        SrtpRemoteKeyParams = srtpRemoteKey,
    };

    /// <summary>Baut ein "inline:base64"-SDES-Key-Param aus 30 Bytes Testmaterial (16 Key + 14 Salt).</summary>
    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
