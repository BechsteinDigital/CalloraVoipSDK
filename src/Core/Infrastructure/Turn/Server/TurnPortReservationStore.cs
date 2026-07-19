using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Holds relay sockets reserved by EVEN-PORT (reserve) allocations (RFC 8656 §7): the odd port next to an even
/// relayed port is bound eagerly and stashed under a random token, so a follow-up Allocate carrying that
/// RESERVATION-TOKEN can claim the exact port. Unclaimed reservations are released after their lifetime by a
/// background sweep. Thread-safe.
/// </summary>
internal sealed class TurnPortReservationStore : IAsyncDisposable
{
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, TurnPortReservation> _reservations = new();
    private readonly Timer _sweepTimer;
    private int _disposed;

    /// <summary>Creates the store with the reservation lifetime and a background sweep at that interval.</summary>
    /// <param name="lifetime">How long an unclaimed reservation is held; non-positive falls back to 30 s.</param>
    /// <param name="logger">Logger for released reservations.</param>
    /// <param name="utcNow">Clock; injectable for deterministic tests.</param>
    public TurnPortReservationStore(TimeSpan lifetime, ILogger logger, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _lifetime = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromSeconds(30);
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _sweepTimer = new Timer(_ => Sweep(), null, _lifetime, _lifetime);
    }

    /// <summary>Stores a pre-bound reserved relay socket under a fresh random token; returns the token.</summary>
    public ulong Reserve(UdpClient reservedSocket)
    {
        ArgumentNullException.ThrowIfNull(reservedSocket);
        var reservation = new TurnPortReservation(reservedSocket, _utcNow() + _lifetime);
        ulong token;
        do
        {
            token = NextToken();
        }
        while (!_reservations.TryAdd(token, reservation));
        return token;
    }

    /// <summary>
    /// Removes and returns the reserved socket for <paramref name="token"/>, or <see langword="null"/> when the
    /// token is unknown or already expired (the expired socket is disposed). The caller owns the returned socket.
    /// </summary>
    public UdpClient? Claim(ulong token)
    {
        if (!_reservations.TryRemove(token, out var reservation))
            return null;
        if (reservation.ExpiresAtUtc <= _utcNow())
        {
            reservation.Socket.Dispose();
            return null;
        }
        return reservation.Socket;
    }

    private void Sweep()
    {
        var now = _utcNow();
        foreach (var (token, reservation) in _reservations)
        {
            if (reservation.ExpiresAtUtc <= now && _reservations.TryRemove(token, out var removed))
            {
                removed.Socket.Dispose();
                _logger.LogDebug("Released an unclaimed TURN port reservation.");
            }
        }
    }

    private static ulong NextToken()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _sweepTimer.DisposeAsync().ConfigureAwait(false);
        foreach (var (token, _) in _reservations)
            if (_reservations.TryRemove(token, out var removed))
                removed.Socket.Dispose();
    }
}
