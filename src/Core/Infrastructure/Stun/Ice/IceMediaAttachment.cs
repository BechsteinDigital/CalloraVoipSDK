using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;

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
    private readonly IPEndPoint _nominatedRemote;
    private readonly ConcurrentDictionary<IPEndPoint, byte> _triggeredSources = new();
    private readonly ILogger<IceMediaAttachment> _logger;

    /// <summary>
    /// Builds the attachment from the negotiated media parameters and the media socket's raw-send
    /// delegate. Both the inbound handler and the consent session are optional — absent when ICE or
    /// the required credentials are not present.
    /// </summary>
    public IceMediaAttachment(
        CallMediaParameters parameters,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<IceMediaAttachment>();
        _nominatedRemote = parameters.RemoteEndPoint;
        _inbound = parameters.IceEnabled
            ? IceInboundStunHandlerFactory.Create(
                parameters.LocalIceUfrag, parameters.LocalIcePwd, parameters.IceControlling, sendRaw, loggerFactory)
            : null;
        _consent = IceMediaConsentSessionFactory.TryCreate(parameters, sendRaw, OnConsentLost, loggerFactory);

        if (_inbound is not null)
            _inbound.CheckAccepted += OnInboundCheckAccepted;
    }

    // RFC 8445 §7.3.1.4: a valid inbound check from a source other than the nominated remote reveals
    // a peer-reflexive path; trigger one confirming connectivity check back to it (learn-once per
    // source). The nominated remote is already validated continuously by consent freshness.
    private void OnInboundCheckAccepted(IPEndPoint source)
    {
        if (_consent is null || source.Equals(_nominatedRemote))
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
            var confirmed = await _consent!.SendCheckAsync(source, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("ICE triggered check to {Source} {Result}.", source, confirmed ? "confirmed" : "unanswered");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICE triggered check to {Source} failed.", source);
        }
    }

    /// <summary>True when either ICE responsibility is active and the attachment should receive STUN.</summary>
    public bool IsActive => _inbound is not null || _consent is not null;

    /// <summary>Starts the consent-freshness loop (no-op when consent is inactive).</summary>
    public void Start() => _consent?.Start();

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
        => _logger.LogWarning("ICE consent lost (RFC 7675): no consent check answered within the consent lifetime.");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
        => _consent?.DisposeAsync() ?? ValueTask.CompletedTask;
}
