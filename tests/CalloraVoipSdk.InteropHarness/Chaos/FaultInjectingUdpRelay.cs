using System.Net;
using System.Net.Sockets;

namespace CalloraVoipSdk.InteropHarness.Chaos;

/// <summary>Which configured leg a relay operation targets.</summary>
public enum RelayLeg
{
    /// <summary>The first configured leg (leg A).</summary>
    A,

    /// <summary>The second configured leg (leg B).</summary>
    B,
}

/// <summary>
/// A man-in-the-middle UDP relay between exactly two peers (leg A ↔ leg B), used as the fault-injection
/// seam for the CORE-011 chaos gate. Both legs point their <c>RemoteEndPoint</c> at this relay; it forwards
/// by configured source address (A→B, B→A) and decides <b>per datagram</b> whether to forward, drop, corrupt,
/// or delay it — plus out-of-band <see cref="InjectAsync"/> of adversarial datagrams and a
/// <see cref="HardFault"/>/<see cref="Heal"/> total-loss toggle for mid-call transport loss.
/// <para>
/// No SDK change is needed: the relay sits on the wire between two real <c>RtpCallMediaSession</c> legs.
/// Peers are configured up front (not learned) because a one-directional media flow never reveals the
/// receiving leg's address by itself.
/// </para>
/// </summary>
public sealed class FaultInjectingUdpRelay : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _legA;
    private readonly IPEndPoint _legB;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    // The pump runs on a single task, so the RNG is only ever touched from one thread. Fixed seed → the
    // probabilistic faults (drop/corrupt rates) are reproducible across CI runs.
    private readonly Random _rng = new(0xC0FFEE);

    private volatile bool _hardFault;
    private int _dropPermille;    // 0..1000
    private int _corruptPermille; // 0..1000
    private int _delayMs;
    private long _forwarded;
    private long _dropped;
    private long _corrupted;
    private long _injected;

    private FaultInjectingUdpRelay(Socket socket, IPEndPoint legA, IPEndPoint legB)
    {
        _socket = socket;
        _legA = legA;
        _legB = legB;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>The loopback port both legs send to.</summary>
    public int Port { get; }

    /// <summary>Datagrams forwarded to their peer so far.</summary>
    public long Forwarded => Interlocked.Read(ref _forwarded);

    /// <summary>Datagrams dropped (hard fault or drop-rate) so far.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Datagrams whose bytes were corrupted before forwarding so far.</summary>
    public long Corrupted => Interlocked.Read(ref _corrupted);

    /// <summary>Adversarial datagrams injected via <see cref="InjectAsync"/> so far.</summary>
    public long Injected => Interlocked.Read(ref _injected);

    /// <summary>Binds a relay on a free loopback port for the two configured legs and starts pumping.</summary>
    public static FaultInjectingUdpRelay Start(IPEndPoint legA, IPEndPoint legB)
    {
        ArgumentNullException.ThrowIfNull(legA);
        ArgumentNullException.ThrowIfNull(legB);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return new FaultInjectingUdpRelay(socket, legA, legB);
    }

    /// <summary>Drops every datagram in both directions until <see cref="Heal"/> — a mid-call transport loss.</summary>
    public void HardFault() => _hardFault = true;

    /// <summary>Resumes forwarding after a <see cref="HardFault"/>.</summary>
    public void Heal() => _hardFault = false;

    /// <summary>Probabilistic per-datagram drop rate (0..1). 0 = forward all.</summary>
    public void SetDropRate(double rate) => Volatile.Write(ref _dropPermille, ToPermille(rate));

    /// <summary>Probabilistic per-datagram byte-corruption rate (0..1). 0 = never corrupt.</summary>
    public void SetCorruptRate(double rate) => Volatile.Write(ref _corruptPermille, ToPermille(rate));

    /// <summary>Per-datagram forwarding delay in milliseconds. 0 = forward immediately.</summary>
    public void SetDelay(int milliseconds) =>
        Volatile.Write(ref _delayMs, Math.Max(0, milliseconds));

    /// <summary>Injects an adversarial datagram straight to one leg, as if it came from the peer.</summary>
    public async Task InjectAsync(byte[] datagram, RelayLeg leg, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        var dest = leg == RelayLeg.A ? _legA : _legB;
        await _socket.SendToAsync(datagram, SocketFlags.None, dest, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _injected);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 0), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            var from = (IPEndPoint)received.RemoteEndPoint;
            var dest = Peer(from);
            if (dest is null)
                continue; // Traffic from an address that is neither configured leg — ignore.

            // Snapshot the payload; the shared receive buffer is reused on the next iteration.
            var datagram = buffer.AsSpan(0, received.ReceivedBytes).ToArray();

            if (_hardFault || DrawPermille(Volatile.Read(ref _dropPermille)))
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }

            if (datagram.Length > 0 && DrawPermille(Volatile.Read(ref _corruptPermille)))
            {
                datagram[_rng.Next(datagram.Length)] ^= 0xFF;
                Interlocked.Increment(ref _corrupted);
            }

            var delay = Volatile.Read(ref _delayMs);
            if (delay > 0)
                _ = ForwardDelayedAsync(datagram, dest, delay, ct);
            else
                await ForwardAsync(datagram, dest, ct).ConfigureAwait(false);
        }
    }

    private async Task ForwardDelayedAsync(byte[] datagram, IPEndPoint dest, int delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            await ForwardAsync(datagram, dest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ForwardAsync(byte[] datagram, IPEndPoint dest, CancellationToken ct)
    {
        try
        {
            await _socket.SendToAsync(datagram, SocketFlags.None, dest, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _forwarded);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    // The peer to forward to for a datagram from <paramref name="from"/>: A→B, B→A, else null.
    private IPEndPoint? Peer(IPEndPoint from)
    {
        if (from.Equals(_legA)) return _legB;
        if (from.Equals(_legB)) return _legA;
        return null;
    }

    private bool DrawPermille(int permille) => permille > 0 && _rng.Next(1000) < permille;

    private static int ToPermille(double rate) => (int)Math.Round(Math.Clamp(rate, 0, 1) * 1000);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _socket.Dispose();
        _cts.Dispose();
    }
}
