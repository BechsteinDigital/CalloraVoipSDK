using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// Builds the RFC 8285 one-byte header extension stamped on each outgoing RTP packet from the negotiated
/// per-stream extensions: the transport-wide sequence number (transport-cc / RFC 8888) and, on a BUNDLE
/// transport, the MID SDES token (RFC 9143) so the peer can associate this stream's SSRC with its m-line.
///
/// The MID is constant for the session, so its element (and the MID-only extension) are built once here;
/// only the transport-cc counter changes per packet. When MID is not configured — every non-BUNDLE call
/// today — the output is byte-identical to stamping transport-cc alone, so the existing send path is
/// unchanged. Extracted as a small, socket-free unit so the wire result is testable directly (ADR-010 B2c).
/// </summary>
internal sealed class RtpOutboundHeaderExtensionStamper
{
    private readonly byte? _transportCcId;
    private readonly RtpHeaderExtensionElement? _midElement;
    private readonly RtpExtension? _midOnlyExtension;

    /// <summary>
    /// Creates the stamper from the negotiated extension ids. MID is stamped only when both
    /// <paramref name="midExtensionId"/> and a non-empty <paramref name="mid"/> are supplied; the MID is
    /// validated once here (id range and the 16-byte one-byte-form limit).
    /// </summary>
    public RtpOutboundHeaderExtensionStamper(byte? transportWideCcExtensionId, byte? midExtensionId, string? mid)
    {
        _transportCcId = transportWideCcExtensionId;
        if (midExtensionId is { } midId && !string.IsNullOrEmpty(mid))
        {
            _midElement = RtpMidHeaderExtension.Element(midId, mid); // validates id range + length once
            _midOnlyExtension = RtpMidHeaderExtension.Encode(midId, mid);
        }
    }

    /// <summary>Whether this stamper adds any header extension at all (transport-cc and/or MID negotiated).</summary>
    public bool StampsAnything => _transportCcId is not null || _midElement is not null;

    /// <summary>
    /// Builds the header extension for one outgoing packet. <paramref name="transportCcSequence"/> is the
    /// transport-wide counter to stamp, or <see langword="null"/> when transport-cc is not stamped on this
    /// packet. Returns <see langword="null"/> when there is nothing to stamp.
    /// </summary>
    public RtpExtension? Build(ushort? transportCcSequence)
    {
        var transportCc = _transportCcId is { } tcId && transportCcSequence is { } ccSeq
            ? OneByteRtpHeaderExtensions.TransportSequenceNumber(tcId, ccSeq)
            : (RtpHeaderExtensionElement?)null;

        if (_midElement is { } midElement)
        {
            // BUNDLE path: MID always, plus transport-cc when present. The MID re-uses its pre-built
            // element; the combined form is rebuilt per packet because the counter changes.
            return transportCc is { } tc
                ? OneByteRtpHeaderExtensions.Encode([midElement, tc])
                : _midOnlyExtension;
        }

        // Non-BUNDLE path (all current calls): transport-cc alone or nothing — byte-identical to before.
        return _transportCcId is { } id && transportCcSequence is { } seq
            ? OneByteRtpHeaderExtensions.EncodeTransportSequenceNumber(id, seq)
            : null;
    }
}
