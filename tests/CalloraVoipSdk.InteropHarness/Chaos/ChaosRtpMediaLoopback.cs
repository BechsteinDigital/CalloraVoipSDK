using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.InteropHarness.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.InteropHarness.Chaos;

/// <summary>
/// Like <see cref="RtpMediaLoopback"/>, but both <see cref="RtpCallMediaSession"/> legs route their media
/// through a <see cref="FaultInjectingUdpRelay"/> instead of sending to each other directly. The relay is the
/// CORE-011 fault-injection seam: the test drives faults via <see cref="Relay"/> and observes how the SDK
/// media path degrades and recovers. No SDK change — the fault lives on the wire between two real sessions.
/// </summary>
public sealed class ChaosRtpMediaLoopback : IAsyncDisposable
{
    private const string SrtpSuite = "AES_CM_128_HMAC_SHA1_80";
    private const byte KeySeedA = 70;
    private const byte KeySeedB = 90;

    private readonly RtpCallMediaSession _a;
    private readonly RtpCallMediaSession _b;
    private readonly FaultInjectingUdpRelay _relay;
    private readonly int _payloadType;
    private readonly int _samplesPerPacket;

    private ChaosRtpMediaLoopback(
        RtpCallMediaSession a, RtpCallMediaSession b, FaultInjectingUdpRelay relay,
        int payloadType, int samplesPerPacket)
    {
        _a = a;
        _b = b;
        _relay = relay;
        _payloadType = payloadType;
        _samplesPerPacket = samplesPerPacket;
    }

    /// <summary>The fault-injection seam on the wire between leg A and leg B.</summary>
    public FaultInjectingUdpRelay Relay => _relay;

    /// <summary>
    /// Binds both legs on free loopback ports, starts a relay between them, and starts their media paths.
    /// Retries on a port-bind collision with fresh ports.
    /// </summary>
    public static async Task<ChaosRtpMediaLoopback> StartAsync(
        int maxAttempts = 5,
        LoopbackCodec codec = LoopbackCodec.Pcmu,
        LoopbackSecurity security = LoopbackSecurity.Plain)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryStartOnceAsync(codec, security);
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < maxAttempts)
            {
                // Port taken between probe and bind — retry with fresh ports.
            }
        }
    }

    private static async Task<ChaosRtpMediaLoopback> TryStartOnceAsync(LoopbackCodec codec, LoopbackSecurity security)
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var (payloadType, clockRate, samples) = CodecSpec(codec);

        var (localA, remoteA, localB, remoteB) = security == LoopbackSecurity.Srtp
            ? (InlineKey(KeySeedA), InlineKey(KeySeedB), InlineKey(KeySeedB), InlineKey(KeySeedA))
            : (null, null, null, null);

        // The relay learns nothing — it is told both leg addresses and forwards A↔B by source.
        var relay = FaultInjectingUdpRelay.Start(
            new IPEndPoint(IPAddress.Loopback, portA), new IPEndPoint(IPAddress.Loopback, portB));
        try
        {
            // Both legs point their RemoteEndPoint at the relay port (not at each other).
            var a = CreateSession(portA, relay.Port, payloadType, clockRate, samples, localA, remoteA);
            try
            {
                var b = CreateSession(portB, relay.Port, payloadType, clockRate, samples, localB, remoteB);
                try
                {
                    await b.StartAsync();
                    await a.StartAsync();
                    return new ChaosRtpMediaLoopback(a, b, relay, payloadType, samples);
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
        catch
        {
            await relay.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Sends <paramref name="payload"/> from leg A (repeating every 20 ms) and returns <see langword="true"/>
    /// as soon as leg B receives a frame, or <see langword="false"/> if none arrives within
    /// <paramref name="timeout"/>. Unlike <see cref="RtpMediaLoopback.RoundTripAsync"/> it does not throw on
    /// timeout — a timeout is the expected observation while a transport loss is active.
    /// </summary>
    public async Task<bool> TryRoundTripAsync(byte[] payload, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(CallAudioFrame f) => tcs.TrySetResult(true);
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
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _b.FrameReceived -= OnFrame;
        }
    }

    /// <summary>
    /// Sends <paramref name="payload"/> from leg A every 20 ms for <paramref name="duration"/>, ignoring what
    /// leg B receives. Used to keep media on the wire while a fault is active — the sender must tolerate the
    /// fault without throwing (its socket is up; the relay decides the datagram's fate).
    /// </summary>
    public async Task SendForAsync(byte[] payload, TimeSpan duration)
    {
        using var cts = new CancellationTokenSource(duration);
        var frame = new CallAudioFrame(payload, _payloadType, (uint)_samplesPerPacket);
        try
        {
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                await _a.SendFrameAsync(frame, cts.Token);
                await Task.Delay(20, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected end of the send window.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _a.DisposeAsync(); }
        finally
        {
            try { await _b.DisposeAsync(); }
            finally { await _relay.DisposeAsync(); }
        }
    }

    private static RtpCallMediaSession CreateSession(
        int localPort, int remotePort, int payloadType, int clockRate, int samples,
        string? srtpLocalKey, string? srtpRemoteKey) =>
        new(Parameters(localPort, remotePort, payloadType, clockRate, samples, srtpLocalKey, srtpRemoteKey),
            NullLoggerFactory.Instance,
            jitterBufferOptions: null, playoutInterval: null, metricsPublishInterval: null);

    private static (int payloadType, int clockRate, int samplesPerPacket) CodecSpec(LoopbackCodec codec) => codec switch
    {
        LoopbackCodec.Pcmu => (0, 8000, 160),
        LoopbackCodec.Opus => (111, 48000, 960),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown loopback codec."),
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
