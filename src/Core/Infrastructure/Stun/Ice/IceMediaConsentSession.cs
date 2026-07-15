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
    private readonly IPEndPoint _remoteEndPoint;
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
        _remoteEndPoint = remoteEndPoint;
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

    private Task<bool> SendConsentCheckAsync(CancellationToken ct) => SendCheckAsync(_remoteEndPoint, ct);

    /// <summary>
    /// Sends one connectivity check to <paramref name="target"/> and returns <see langword="true"/>
    /// when a matching response arrives within the check timeout. Shared by consent checks (to the
    /// nominated remote) and triggered checks (back to the source of an inbound check,
    /// RFC 8445 §7.3.1.4). Registers the transaction before sending so a fast response is not missed.
    /// </summary>
    /// <param name="target">The address to send the check to.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<bool> SendCheckAsync(IPEndPoint target, CancellationToken ct)
    {
        var (datagram, transactionId) = IceConsentCheckBuilder.Build(
            _codec, _localUfrag, _remoteUfrag, _remotePassword, _priority, _controlling, _tieBreaker);

        var pending = _registry.AwaitResponseAsync(transactionId, _checkTimeout, ct);
        try
        {
            await _sendRaw(datagram, target, ct).ConfigureAwait(false);
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
