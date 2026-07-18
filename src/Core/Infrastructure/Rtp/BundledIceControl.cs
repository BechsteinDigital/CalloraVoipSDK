using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Layers ICE (RFC 8445) on a bundled transport's shared 5-tuple (ADR-011 B3-3): a BUNDLE group runs
/// one ICE agent over its single socket, so one consent-freshness loop (RFC 7675) keeps the whole group
/// alive and one inbound handler answers the peer's connectivity checks for every m-line. It wires the
/// reusable <see cref="IceMediaAttachment"/> to the bundle's data path — STUN datagrams demuxed by the
/// <see cref="BundledInboundPipeline"/> feed the attachment, and its checks and responses go out through
/// the transport's targeted send (a STUN response goes to the source of the check, not the default
/// remote). Consent loss/degraded/recovered surface through the supplied callbacks.
/// </summary>
internal sealed class BundledIceControl : IAsyncDisposable
{
    private readonly BundledInboundPipeline _inbound;
    private readonly IceMediaAttachment _attachment;
    private int _disposed;

    /// <param name="parameters">The ICE view of the shared 5-tuple (credentials, role, nominated remote).</param>
    /// <param name="inbound">The bundle inbound pipeline whose STUN datagrams feed the ICE agent.</param>
    /// <param name="sendRaw">
    /// Targeted raw send over the shared socket — a STUN packet goes to the endpoint the attachment
    /// supplies (typically <see cref="BundledMediaTransport.SendToAsync"/>), not the default remote.
    /// </param>
    /// <param name="onConsentLost">Invoked when consent freshness expires (RFC 7675).</param>
    /// <param name="onConnectivityDegraded">Invoked on a transient consent miss.</param>
    /// <param name="onConnectivityRecovered">Invoked when consent recovers after a degrade.</param>
    /// <param name="onPairNominated">
    /// Invoked with the nominated remote endpoint once ICE connectivity checks select a pair (RFC 8445 §8),
    /// so the transport points its send target at the checked pair (typically
    /// <see cref="BundledMediaTransport.SetRemoteEndPoint"/>).
    /// </param>
    public BundledIceControl(
        IceMediaParameters parameters,
        BundledInboundPipeline inbound,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        ILoggerFactory loggerFactory,
        Action? onConsentLost = null,
        Action? onConnectivityDegraded = null,
        Action? onConnectivityRecovered = null,
        Action<IPEndPoint>? onPairNominated = null)
    {
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _attachment = new IceMediaAttachment(
            parameters, sendRaw, loggerFactory, onConsentLost, onConnectivityDegraded, onConnectivityRecovered,
            onPairNominated);

        _inbound.StunPacketReceived += _attachment.OnStunPacketReceived;
    }

    /// <summary>True when ICE is active on this transport (inbound checks and/or consent freshness).</summary>
    public bool IsActive => _attachment.IsActive;

    /// <summary>Starts the consent-freshness loop (no-op when consent is inactive).</summary>
    public void Start() => _attachment.Start();

    /// <summary>Detaches from the inbound STUN feed and disposes the consent session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _inbound.StunPacketReceived -= _attachment.OnStunPacketReceived;
        await _attachment.DisposeAsync().ConfigureAwait(false);
    }
}
