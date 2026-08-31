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
/// outbound SRTP/SRTCP into the send path — at which point their fail-closed guards open and media (and,
/// via the outbound SRTCP context, periodic Sender Reports, RFC 3550 §6.4) can flow.
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
        Action? onKeysInstalled = null,
        Action? onPeerClosed = null)
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
                outbound.InstallOutboundRtcpKey(outboundSrtcp); // enables periodic Sender Reports (RFC 3550 §6.4)
                onKeysInstalled?.Invoke(); // media can now flow (RFC 5763: keys derived from the handshake)
            },
            onHandshakeFailed: onHandshakeFailed,
            loggerFactory,
            onPeerClosed: onPeerClosed);

        _inbound.DtlsPacketReceived += _attachment.OnDtlsPacketReceived;
    }

    private int _nominated;

    /// <summary>Starts the shared DTLS handshake in the negotiated role.</summary>
    public void Start(CancellationToken cancellationToken = default) => _attachment.Start(cancellationToken);

    /// <summary>
    /// Points the shared DTLS association at a newly nominated remote (RFC 8445 §8), so its inbound source
    /// filter accepts the connectivity-checked candidate pair instead of the initial SDP endpoint.
    /// </summary>
    /// <param name="remoteEndPoint">The nominated remote endpoint.</param>
    public void SetRemoteEndPoint(IPEndPoint remoteEndPoint)
    {
        Volatile.Write(ref _nominated, 1);
        _attachment.UpdateRemoteEndPoint(remoteEndPoint);
    }

    /// <summary>
    /// Points the association at a source ICE has authenticated but not yet nominated, so the handshake can
    /// start against it instead of being dropped until nomination completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second of two seconds.</b> A browser starts its DTLS handshake as soon as it has a usable
    /// candidate pair, which is well before the controlling agent sets USE-CANDIDATE. Until nomination
    /// reached us the association still pointed at the SDP endpoint — <c>0.0.0.0:9</c>, the "no address"
    /// placeholder — so every record was dropped, the browser never got a reply, and it retransmitted on
    /// a doubling timer. Measured in a real call: drops at +406 ms and +813 ms, and a handshake that took
    /// two seconds for what needs two round trips.
    /// </para>
    /// <para>
    /// <b>Why this is safe.</b> The filter exists so an off-path sender cannot feed the handshake, and a
    /// source that reaches here is not off-path: it produced a STUN check whose MESSAGE-INTEGRITY verified
    /// against our local ICE password (<c>IceInboundCheckProcessor</c> discards a failed one rather than
    /// answering it), so it holds the credential from our SDP. The fingerprint remains the authentication
    /// boundary either way (RFC 5763 §6.7.1).
    /// </para>
    /// <para>
    /// A nomination always wins and is never undone: once <see cref="SetRemoteEndPoint"/> has run, a later
    /// validated source is ignored, so a candidate that is merely authenticated cannot pull the filter off
    /// the pair ICE actually chose.
    /// </para>
    /// </remarks>
    /// <param name="remoteEndPoint">The ICE-authenticated source.</param>
    public void AdoptValidatedSource(IPEndPoint remoteEndPoint)
    {
        if (Volatile.Read(ref _nominated) != 0)
            return;

        _attachment.UpdateRemoteEndPoint(remoteEndPoint);
    }

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
