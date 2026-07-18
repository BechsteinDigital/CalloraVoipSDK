using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Bundles the ICE responsibilities layered on a media leg's shared socket: answering inbound
/// connectivity checks (RFC 8445 §7.3, <see cref="IceInboundStunHandler"/>) and running consent
/// freshness (RFC 7675, <see cref="IceMediaConsentSession"/>). A media session builds one of these
/// from the negotiated parameters, feeds it the STUN datagrams demuxed off the receive loop via
/// <see cref="OnStunPacketReceived"/>, calls <see cref="Start"/>, and disposes it — keeping the ICE
/// wiring out of the media session itself.
/// </summary>
internal sealed class IceMediaAttachment : IAsyncDisposable
{
    private readonly IceInboundStunHandler? _inbound;
    private readonly IceMediaConsentSession? _consent;
    private readonly IceNominationDriver? _nominationDriver;
    private IPEndPoint _nominatedRemote;
    private readonly ConcurrentDictionary<IPEndPoint, byte> _triggeredSources = new();
    private readonly Action? _onConsentLost;
    private readonly Action? _onConnectivityDegraded;
    private readonly Action? _onConnectivityRecovered;
    private readonly Action<IPEndPoint>? _onPairNominated;
    private int _nominated;
    private readonly ILogger<IceMediaAttachment> _logger;

    /// <summary>
    /// Builds the attachment from the ICE view of the media 5-tuple and the media socket's raw-send
    /// delegate. Both the inbound handler and the consent session are optional — absent when ICE or
    /// the required credentials are not present. When this agent is controlling and the parameters carry
    /// remote candidates, a nomination driver runs connectivity checks and nominates a pair (RFC 8445 §7/§8);
    /// a controlled agent adopts the pair the peer nominates via its USE-CANDIDATE check.
    /// </summary>
    /// <param name="onPairNominated">
    /// Invoked once with the nominated remote endpoint so the caller can redirect the media send target to
    /// the checked pair (typically the transport's <c>SetRemoteEndPoint</c>). Consent freshness is redirected
    /// internally.
    /// </param>
    public IceMediaAttachment(
        IceMediaParameters parameters,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        ILoggerFactory loggerFactory,
        Action? onConsentLost = null,
        Action? onConnectivityDegraded = null,
        Action? onConnectivityRecovered = null,
        Action<IPEndPoint>? onPairNominated = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<IceMediaAttachment>();
        _onConsentLost = onConsentLost;
        _onConnectivityDegraded = onConnectivityDegraded;
        _onConnectivityRecovered = onConnectivityRecovered;
        _onPairNominated = onPairNominated;
        _nominatedRemote = parameters.RemoteEndPoint;
        _inbound = parameters.IceEnabled
            ? IceInboundStunHandlerFactory.Create(
                parameters.LocalIceUfrag, parameters.LocalIcePwd, parameters.IceControlling, sendRaw, loggerFactory)
            : null;
        _consent = IceMediaConsentSessionFactory.TryCreate(
            parameters, sendRaw, OnConsentLost, loggerFactory, _onConnectivityDegraded, _onConnectivityRecovered);

        // The controlling agent drives candidate-pair checks and regular nomination (RFC 8445 §7.2.2/§8.1.1);
        // it needs the consent session's shared-socket check primitive and at least one remote candidate.
        if (_consent is not null && parameters.IceControlling && parameters.RemoteCandidates.Count > 0)
        {
            _nominationDriver = new IceNominationDriver(
                parameters.RemoteCandidates,
                _consent.SendCheckAsync,
                Nominate,
                loggerFactory);
        }

        if (_inbound is not null)
        {
            _inbound.CheckAccepted += OnInboundCheckAccepted;
            // The controlled agent adopts the pair the controlling peer nominates (RFC 8445 §7.3.1.5).
            _inbound.PairNominated += Nominate;
        }
    }

    // RFC 8445 §7.3.1.4: a valid inbound check from a source other than the nominated remote reveals
    // a peer-reflexive path; trigger one confirming connectivity check back to it (learn-once per
    // source). The nominated remote is already validated continuously by consent freshness.
    private void OnInboundCheckAccepted(IPEndPoint source)
    {
        if (_consent is null || source.Equals(Volatile.Read(ref _nominatedRemote)))
            return;
        if (!_triggeredSources.TryAdd(source, 0))
            return;

        _logger.LogDebug("ICE triggered check to peer-reflexive source {Source} (RFC 8445 §7.3.1.4).", source);
        _ = SendTriggeredCheckAsync(source);
    }

    private async Task SendTriggeredCheckAsync(IPEndPoint source)
    {
        try
        {
            var confirmed = await _consent!.SendCheckAsync(source, useCandidate: false, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("ICE triggered check to {Source} {Result}.", source, confirmed ? "confirmed" : "unanswered");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICE triggered check to {Source} failed.", source);
        }
    }

    /// <summary>True when either ICE responsibility is active and the attachment should receive STUN.</summary>
    public bool IsActive => _inbound is not null || _consent is not null;

    /// <summary>
    /// Starts the consent-freshness loop and, on a controlling agent with candidates, the connectivity-check
    /// nomination driver (no-op when the respective responsibility is inactive). Call after the transport's
    /// receive loop is running so the driver's checks are answered and matched over the shared socket.
    /// </summary>
    public void Start()
    {
        _consent?.Start();
        _nominationDriver?.Start();
    }

    // Nominates a checked/adopted remote pair (RFC 8445 §8), exactly once: redirects consent freshness onto
    // it and reports it so the transport send target follows. Funnelled from both the controlling driver and
    // the controlled agent's inbound USE-CANDIDATE, so it is idempotent by design.
    private void Nominate(IPEndPoint remoteEndPoint)
    {
        if (Interlocked.Exchange(ref _nominated, 1) != 0)
            return;

        Volatile.Write(ref _nominatedRemote, remoteEndPoint);
        _consent?.Nominate(remoteEndPoint);
        try
        {
            _onPairNominated?.Invoke(remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ICE pair-nominated handler.");
        }
    }

    /// <summary>
    /// Routes a STUN datagram demuxed off the media socket by class (RFC 5389 §6): Success/Error
    /// responses answer our consent checks (RFC 7675); everything else is an inbound
    /// connectivity-check request (RFC 8445 §7.3). Matches the transport's
    /// <c>StunPacketReceived(byte[], IPEndPoint)</c> hook signature.
    /// </summary>
    public void OnStunPacketReceived(byte[] datagram, IPEndPoint source)
    {
        if (_consent is not null
            && datagram.Length >= 2
            && (BinaryPrimitives.ReadUInt16BigEndian(datagram) & 0x0110) is 0x0100 or 0x0110)
        {
            _consent.OnStunResponse(datagram);
            return;
        }

        _inbound?.OnStunPacketReceived(datagram, source);
    }

    private void OnConsentLost()
    {
        _logger.LogWarning("ICE consent lost (RFC 7675): no consent check answered within the consent lifetime.");
        try { _onConsentLost?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "Unhandled exception in ICE consent-lost handler."); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_nominationDriver is not null)
            await _nominationDriver.DisposeAsync().ConfigureAwait(false);
        if (_consent is not null)
            await _consent.DisposeAsync().ConfigureAwait(false);
    }
}
