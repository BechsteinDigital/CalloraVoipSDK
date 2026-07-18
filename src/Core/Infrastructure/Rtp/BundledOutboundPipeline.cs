using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The outbound datagram path of the bundled transport (ADR-011 B2c-in-4, RFC 8843): it hosts the
/// per-m-line <see cref="BundledOutboundTrack"/>s, encrypts each built packet with the transport's
/// shared outbound SRTP context (per-SSRC state, ADR-011 B2c-in-1), and sends it over the shared
/// 5-tuple via <see cref="IBundledDatagramSender"/>. It is the send-side mirror of
/// <see cref="BundledInboundPipeline"/> and, like it, owns no socket — the shared UDP send is provided
/// by the sender seam wired in B3.
///
/// BUNDLE media is DTLS-SRTP only, so the pipeline fails closed: a send is suppressed and counted until
/// <see cref="InstallOutboundKey"/> supplies the negotiated SRTP context, and never leaves as plaintext.
/// Thread-safe: the track registry is a <see cref="ConcurrentDictionary{TKey,TValue}"/>, the SRTP
/// context is read through <see cref="Volatile"/> and is internally synchronised, and each track
/// advances its own RTP cursors under its own lock.
/// </summary>
internal sealed class BundledOutboundPipeline
{
    private readonly ConcurrentDictionary<string, BundledOutboundTrack> _tracksByMid =
        new(StringComparer.Ordinal);
    private readonly IRtpPacketCodec _codec;
    private readonly IBundledDatagramSender _sender;
    private readonly ILogger<BundledOutboundPipeline> _logger;

    private ISrtpContext? _outboundSrtp;
    private long _suppressedSends;
    private long _packetsSent;
    private long _bytesSent;

    /// <summary>Raised after a packet has actually been sent, so an RTX buffer (RFC 4588) can retain it.</summary>
    public event Action<RtpPacket>? PacketSent;

    public BundledOutboundPipeline(
        IRtpPacketCodec codec,
        IBundledDatagramSender sender,
        ILogger<BundledOutboundPipeline> logger)
    {
        _codec  = codec  ?? throw new ArgumentNullException(nameof(codec));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Sends suppressed because no outbound SRTP context was installed yet (fail-closed).</summary>
    public long SuppressedSends => Interlocked.Read(ref _suppressedSends);

    /// <summary>Total RTP packets actually sent (after SRTP protection) across all tracks.</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>Total bytes of protected RTP datagrams sent across all tracks.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>
    /// Registers the outbound track for one m-line's MID.
    /// </summary>
    /// <exception cref="InvalidOperationException">A track is already registered for <paramref name="mid"/>.</exception>
    public void RegisterTrack(string mid, BundledOutboundTrack track)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(track);
        if (!_tracksByMid.TryAdd(mid, track))
            throw new InvalidOperationException($"An outbound track is already registered for MID '{mid}'.");
    }

    /// <summary>Removes the track for a MID. Returns <see langword="false"/> when none was registered.</summary>
    public bool UnregisterTrack(string mid) => _tracksByMid.TryRemove(mid, out _);

    /// <summary>
    /// Installs the shared outbound SRTP context once the DTLS-SRTP handshake has derived the key. Until
    /// then every send fails closed. The one context serves every track's SSRC under the shared key.
    /// </summary>
    public void InstallOutboundKey(ISrtpContext srtp)
    {
        ArgumentNullException.ThrowIfNull(srtp);
        Volatile.Write(ref _outboundSrtp, srtp);
    }

    /// <summary>
    /// Sends one media packet on the given MID's track, advancing that track's timestamp cursor.
    /// </summary>
    /// <exception cref="InvalidOperationException">No track is registered for <paramref name="mid"/>.</exception>
    public ValueTask SendAsync(
        string mid,
        ReadOnlyMemory<byte> payload,
        bool marker = false,
        byte? payloadTypeOverride = null,
        CancellationToken cancellationToken = default)
    {
        var track = ResolveTrack(mid);
        var payloadType = payloadTypeOverride ?? track.DefaultPayloadType;
        return SendCoreAsync(mid, track, payload, marker, payloadType, timestampOverride: null, advanceTimestamp: true, cancellationToken);
    }

    /// <summary>
    /// Sends one packet on the given MID's track with an explicit timestamp and without advancing the
    /// track's timestamp cursor — for video frames whose packets share one frame-level timestamp.
    /// </summary>
    /// <exception cref="InvalidOperationException">No track is registered for <paramref name="mid"/>.</exception>
    public ValueTask SendTimestampedAsync(
        string mid,
        ReadOnlyMemory<byte> payload,
        bool marker,
        byte payloadType,
        uint timestamp,
        CancellationToken cancellationToken = default)
    {
        var track = ResolveTrack(mid);
        return SendCoreAsync(mid, track, payload, marker, payloadType, timestampOverride: timestamp, advanceTimestamp: false, cancellationToken);
    }

    private BundledOutboundTrack ResolveTrack(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        if (!_tracksByMid.TryGetValue(mid, out var track))
            throw new InvalidOperationException($"No outbound track is registered for MID '{mid}'.");
        return track;
    }

    private async ValueTask SendCoreAsync(
        string mid,
        BundledOutboundTrack track,
        ReadOnlyMemory<byte> payload,
        bool marker,
        byte payloadType,
        uint? timestampOverride,
        bool advanceTimestamp,
        CancellationToken cancellationToken)
    {
        // Fail closed before building anything: a BUNDLE transport is DTLS-SRTP only and must never
        // emit plaintext RTP. Checked first so the track's sequence cursor is not consumed on a drop.
        if (Volatile.Read(ref _outboundSrtp) is not { } outboundSrtp)
        {
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogDebug("Suppressing outbound RTP on MID {Mid}: no SRTP context installed yet.", mid);
            return;
        }

        var packet = track.BuildPacket(payload, marker, payloadType, timestampOverride, advanceTimestamp);
        var datagram = _codec.Encode(packet);

        try
        {
            datagram = outboundSrtp.Protect(datagram);
        }
        catch (ObjectDisposedException)
        {
            // A send racing transport teardown after the context owner zeroed the keys — suppress the
            // packet; never fall through to an unprotected send.
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogDebug("Suppressing outbound RTP: SRTP context disposed during teardown.");
            return;
        }

        await _sender.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _packetsSent);
        Interlocked.Add(ref _bytesSent, datagram.Length);

        try
        {
            PacketSent?.Invoke(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled RTP PacketSent handler.");
        }
    }
}
