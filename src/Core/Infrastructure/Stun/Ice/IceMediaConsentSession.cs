using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Runs RFC 7675 consent freshness for a nominated ICE pair on the media socket: it drives an
/// <see cref="IceConsentMonitor"/> whose consent checks are built by <see cref="IceConsentCheckBuilder"/>,
/// sent through the shared media send delegate, and matched to their responses via an
/// <see cref="IceStunTransactionRegistry"/>. The media receive loop feeds inbound STUN responses to
/// <see cref="OnStunResponse"/>; when no check is answered within the consent lifetime the injected
/// consent-lost callback fires. Timing dependencies are injectable so the loop is deterministically
/// testable.
/// </summary>
internal sealed class IceMediaConsentSession : IAsyncDisposable
{
    private readonly IStunMessageCodec _codec;
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _sendRaw;
    // The nominated pair: the remote endpoint and its send path (null = direct media socket, or a TURN-framed
    // relay path when a relay pair was nominated). Mutable — ICE nomination (RFC 8445 §8) can redirect consent
    // freshness onto a newly selected pair, and a relay nomination swaps in the relay send path so freshness
    // keeps the relayed pair alive (RFC 7675 over the allocation). One immutable value swapped under Volatile
    // (written from the nomination path, read from the consent loop) so the remote and its send path are always
    // observed as a consistent snapshot, never torn across two fields.
    private IceNominatedTarget _nominated;
    private readonly string _localUfrag;
    private readonly string _remoteUfrag;
    private readonly string _remotePassword;
    private readonly uint _priority;
    private readonly bool _controlling;
    private readonly ulong _tieBreaker;
    private readonly TimeSpan _checkTimeout;
    private readonly IceStunTransactionRegistry _registry = new();
    private readonly IceConsentMonitor _monitor;
    private readonly ILogger<IceMediaConsentSession> _logger;

    /// <summary>
    /// Creates a consent session for the nominated pair. Optional timing parameters are injected for
    /// deterministic tests; production leaves them at their runtime defaults.
    /// </summary>
    public IceMediaConsentSession(
        IStunMessageCodec codec,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        IPEndPoint remoteEndPoint,
        string localUfrag,
        string remoteUfrag,
        string remotePassword,
        uint priority,
        bool controlling,
        ulong tieBreaker,
        Action onConsentLost,
        ILoggerFactory loggerFactory,
        IceConsentFreshnessPolicy? policy = null,
        TimeSpan? checkTimeout = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? nextRandom = null,
        Action? onConnectivityDegraded = null,
        Action? onConnectivityRecovered = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(localUfrag);
        ArgumentNullException.ThrowIfNull(remoteUfrag);
        ArgumentNullException.ThrowIfNull(remotePassword);
        ArgumentNullException.ThrowIfNull(onConsentLost);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _codec = codec;
        _sendRaw = sendRaw;
        _nominated = new IceNominatedTarget(remoteEndPoint, Send: null);
        _localUfrag = localUfrag;
        _remoteUfrag = remoteUfrag;
        _remotePassword = remotePassword;
        _priority = priority;
        _controlling = controlling;
        _tieBreaker = tieBreaker;
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(2);
        _logger = loggerFactory.CreateLogger<IceMediaConsentSession>();
        _monitor = new IceConsentMonitor(
            policy ?? new IceConsentFreshnessPolicy(),
            SendConsentCheckAsync,
            onConsentLost,
            loggerFactory,
            utcNow,
            delay,
            nextRandom,
            onConnectivityDegraded,
            onConnectivityRecovered);
    }

    /// <summary>Starts the consent loop. Idempotent (see <see cref="IceConsentMonitor.Start"/>).</summary>
    public void Start() => _monitor.Start();

    /// <summary>
    /// Redirects consent freshness onto a newly nominated ICE pair (RFC 8445 §8): subsequent consent
    /// checks are sent to <paramref name="remoteEndPoint"/> over the direct media socket instead of the
    /// pair the session started on. Thread-safe.
    /// </summary>
    /// <param name="remoteEndPoint">The nominated remote endpoint to run consent against.</param>
    public void Nominate(IPEndPoint remoteEndPoint) => Nominate(remoteEndPoint, sendVia: null);

    /// <summary>
    /// Redirects consent freshness onto a newly nominated ICE pair (RFC 8445 §8), running subsequent consent
    /// checks to <paramref name="remoteEndPoint"/> over <paramref name="sendVia"/> — a TURN-framed relay send
    /// path when a relay pair was nominated, or <see langword="null"/> to use the direct media socket.
    /// Thread-safe.
    /// </summary>
    /// <param name="remoteEndPoint">The nominated remote endpoint to run consent against.</param>
    /// <param name="sendVia">
    /// The send path consent checks use for the nominated pair, or <see langword="null"/> for the direct
    /// media socket. Same shape as the raw-send delegate: <c>(datagram, remoteTarget, ct)</c>.
    /// </param>
    public void Nominate(
        IPEndPoint remoteEndPoint,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? sendVia)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        // A single atomic reference swap: the consent loop always reads the remote and its send path together.
        Volatile.Write(ref _nominated, new IceNominatedTarget(remoteEndPoint, sendVia));
    }

    /// <summary>
    /// Feeds an inbound STUN response (demuxed off the media socket) to the transaction matcher,
    /// confirming the consent check it answers. Non-matching datagrams are ignored.
    /// </summary>
    /// <param name="datagram">The received STUN response datagram.</param>
    public void OnStunResponse(ReadOnlySpan<byte> datagram)
    {
        // The 12-byte transaction ID is at offset 8 of the STUN header (RFC 5389 §6).
        if (datagram.Length < 20)
            return;
        _registry.TryComplete(datagram.Slice(8, 12));
    }

    private Task<bool> SendConsentCheckAsync(CancellationToken ct)
    {
        // One atomic read of the nominated pair — remote and send path are always a consistent snapshot.
        var nominated = Volatile.Read(ref _nominated);
        return SendCheckVia(nominated.Send ?? _sendRaw, nominated.Remote, useCandidate: false, ct);
    }

    /// <summary>
    /// Sends one connectivity check to <paramref name="target"/> over the direct media socket and returns
    /// <see langword="true"/> when a matching response arrives within the check timeout. Shared by consent
    /// checks (to the nominated remote), triggered checks (back to the source of an inbound check,
    /// RFC 8445 §7.3.1.4), and direct nomination checks (candidate-pair checking, RFC 8445 §7.2.2).
    /// </summary>
    /// <param name="target">The address to send the check to.</param>
    /// <param name="useCandidate">Whether to carry USE-CANDIDATE — a controlling agent's nominating check (RFC 8445 §8.1.1).</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task<bool> SendCheckAsync(IPEndPoint target, bool useCandidate, CancellationToken ct)
        => SendCheckVia(_sendRaw, target, useCandidate, ct);

    /// <summary>
    /// Sends one connectivity check to <paramref name="target"/> over <paramref name="send"/> and returns
    /// <see langword="true"/> when a matching response arrives within the check timeout. The send path is
    /// injected so a relay local candidate can frame the check through its TURN server (Send indication)
    /// while direct candidates use the media socket — both correlate their response by the same transaction
    /// id via <see cref="OnStunResponse"/>, so the matcher is send-path agnostic. Registers the transaction
    /// before sending so a fast response is not missed.
    /// </summary>
    /// <param name="send">The send path — <c>(datagram, remoteTarget, ct)</c>; the direct socket or a relay frame.</param>
    /// <param name="target">The remote address the check is addressed to.</param>
    /// <param name="useCandidate">Whether to carry USE-CANDIDATE — a controlling agent's nominating check (RFC 8445 §8.1.1).</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<bool> SendCheckVia(
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send,
        IPEndPoint target,
        bool useCandidate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);

        var (datagram, transactionId) = IceConsentCheckBuilder.Build(
            _codec, _localUfrag, _remoteUfrag, _remotePassword, _priority, _controlling, _tieBreaker, useCandidate);

        var pending = _registry.AwaitResponseAsync(transactionId, _checkTimeout, ct);
        try
        {
            await send(datagram, target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Failed to send ICE check to {Target}.", target);
            return false;
        }

        return await pending.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _monitor.DisposeAsync();
}
