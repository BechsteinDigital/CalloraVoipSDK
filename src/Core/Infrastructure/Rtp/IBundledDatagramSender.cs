namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Sends an already-protected datagram over the bundled transport's shared 5-tuple (RFC 8843).
/// The outbound pipeline builds and encrypts a track's RTP packet, then hands the bytes to this seam;
/// the shared UDP socket that implements it — with the latched remote endpoint — is assembled in a
/// later slice (B3). Keeping the send behind an interface lets the outbound path be driven and tested
/// without a socket.
/// </summary>
internal interface IBundledDatagramSender
{
    /// <summary>Sends one datagram to the transport's remote endpoint.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);
}
