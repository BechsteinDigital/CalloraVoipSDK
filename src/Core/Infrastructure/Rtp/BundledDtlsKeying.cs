using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Keys a bundled transport with DTLS-SRTP (ADR-011 B3-2, RFC 5763/5764). A BUNDLE group (RFC 8843)
/// runs one shared DTLS association over its single 5-tuple, so a single handshake derives the SRTP
/// keys for every m-line. This wires the reusable <see cref="DtlsMediaAttachment"/> to the bundle's
/// data path: inbound DTLS records demultiplexed by the <see cref="BundledInboundPipeline"/> feed the
/// handshake, its records go out through the transport, and on completion the four derived contexts are
/// installed into both pipelines — the shared inbound SRTP/SRTCP into the receive path and the shared
/// outbound SRTP into the send path — at which point their fail-closed guards open and media flows.
/// </summary>
internal sealed class BundledDtlsKeying : IAsyncDisposable
{
    private readonly BundledInboundPipeline _inbound;
    private readonly DtlsMediaAttachment _attachment;
    private int _disposed;

    public BundledDtlsKeying(
        bool isClient,
        IPEndPoint remoteEndPoint,
        DtlsFingerprint expectedRemoteFingerprint,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        BundledInboundPipeline inbound,
        BundledOutboundPipeline outbound,
        IBundledDatagramSender sender,
        Action onHandshakeFailed,
        ILoggerFactory loggerFactory,
        Action? onKeysInstalled = null)
    {
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(sender);

        _attachment = DtlsMediaAttachment.Create(
            isClient,
            remoteEndPoint,
            expectedRemoteFingerprint,
            handshaker,
            certificate,
            // The bundle transport already targets the shared remote; the endpoint the attachment passes
            // for its own source filter is not needed to address the send.
            sendRaw: (datagram, _, cancellationToken) => sender.SendAsync(datagram, cancellationToken),
            onContextsReady: (outboundSrtp, inboundSrtp, outboundSrtcp, inboundSrtcp) =>
            {
                inbound.InstallInboundKeys(inboundSrtp, inboundSrtcp);
                outbound.InstallOutboundKey(outboundSrtp);
                onKeysInstalled?.Invoke(); // media can now flow (RFC 5763: keys derived from the handshake)
            },
            onHandshakeFailed: onHandshakeFailed,
            loggerFactory);

        _inbound.DtlsPacketReceived += _attachment.OnDtlsPacketReceived;
    }

    /// <summary>Starts the shared DTLS handshake in the negotiated role.</summary>
    public void Start(CancellationToken cancellationToken = default) => _attachment.Start(cancellationToken);

    /// <summary>
    /// Points the shared DTLS association at a newly nominated remote (RFC 8445 §8), so its inbound source
    /// filter accepts the connectivity-checked candidate pair instead of the initial SDP endpoint.
    /// </summary>
    /// <param name="remoteEndPoint">The nominated remote endpoint.</param>
    public void SetRemoteEndPoint(IPEndPoint remoteEndPoint) => _attachment.UpdateRemoteEndPoint(remoteEndPoint);

    /// <summary>
    /// Detaches from the inbound DTLS feed and disposes the association (close_notify, key zeroing).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _inbound.DtlsPacketReceived -= _attachment.OnDtlsPacketReceived;
        await _attachment.DisposeAsync().ConfigureAwait(false);
    }
}
