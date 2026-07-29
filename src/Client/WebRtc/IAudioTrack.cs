namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A handle to an outbound audio track added with <see cref="IPeerConnection.AddAudioTrack()"/> (its own
/// <c>m=audio</c> line on the shared BUNDLE transport, beyond the peer's primary audio anchor). Send encoded
/// audio payloads on this track with <see cref="SendFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>;
/// each track carries its own SSRC, so payloads sent on distinct handles never collide (RFC 3550 §8.1). The
/// SFU pattern of one audio stream per remote participant on a single peer connection.
/// </summary>
/// <remarks>
/// Adding a track supplements the peer's implicit primary audio track (the always-on transport anchor that
/// <see cref="IPeerConnection.SendAudioAsync"/> addresses); the frameless
/// <see cref="IPeerConnection.SendAudioAsync"/> keeps addressing that primary track. A track added before
/// <see cref="IPeerConnection.CreateOffer"/> is negotiated in the first offer; a track added mid-call is pending
/// until the next offer/answer cycle applies it to the live session (RFC 8829 renegotiation). Audio has no
/// simulcast, so there is no per-layer send overload. DTMF (RFC 4733) stays on the primary audio track and is
/// not surfaced per additional track — send it via <see cref="IPeerConnection.SendDtmfAsync"/>.
/// </remarks>
public interface IAudioTrack
{
    /// <summary>The track's negotiated MID (<c>a=mid</c>, RFC 5888) — a numeric token on the multi-track transport.</summary>
    string Mid { get; }

    /// <summary>The direction this track was added with (RFC 3264).</summary>
    TrackDirection Direction { get; }

    /// <summary>
    /// Sends one already-encoded audio RTP payload on this track, with <paramref name="rtpTimestamp"/> set on the
    /// outbound RTP packets (RFC 3550 §5.1). The app owns the codec (transport-only). The timestamp is stamped on
    /// the wire — not derived from an internal cursor — so an SFU forwarding this stream preserves the source's
    /// timestamp for A/V-sync against forwarded video; the app supplies the source's own RTP timestamp per payload.
    /// Suppressed until the DTLS handshake keys the transport; a no-op when the negotiated directions do not carry
    /// outbound audio on this track (a send-only/inactive remote answer, or a recv-only/inactive local side, RFC 3264).
    /// </summary>
    /// <param name="encodedAudioFrame">The already-encoded audio RTP payload.</param>
    /// <param name="rtpTimestamp">The payload's RTP timestamp on the track's negotiated audio clock; stamped on the outbound packets.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendFrameAsync(ReadOnlyMemory<byte> encodedAudioFrame, uint rtpTimestamp, CancellationToken cancellationToken = default);
}
