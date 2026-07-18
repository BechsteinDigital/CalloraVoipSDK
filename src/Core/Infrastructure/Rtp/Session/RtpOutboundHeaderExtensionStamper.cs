using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// Builds the RFC 8285 one-byte header extension stamped on each outgoing RTP packet from the negotiated
/// per-stream extensions: the transport-wide sequence number (transport-cc / RFC 8888); on a BUNDLE
/// transport, the MID SDES token (RFC 9143) so the peer can associate this stream's SSRC with its m-line;
/// and, on a simulcast encoding, the RID SDES token (RFC 8852) so the peer can associate the SSRC with its
/// <c>a=rid</c> layer.
///
/// The MID and RID are constant for the stream, so their elements (and the constant-only extension) are
/// built once here; only the transport-cc counter changes per packet. When neither MID nor RID is
/// configured — every non-BUNDLE call today — the output is byte-identical to stamping transport-cc alone,
/// so the existing send path is unchanged. Extracted as a small, socket-free unit so the wire result is
/// testable directly (ADR-010 B2c).
/// </summary>
internal sealed class RtpOutboundHeaderExtensionStamper
{
    private readonly byte? _transportCcId;
    private readonly RtpHeaderExtensionElement[] _constantElements; // MID then RID, in wire order
    private readonly RtpExtension? _constantOnlyExtension;

    /// <summary>
    /// Creates the stamper from the negotiated extension ids. MID is stamped only when both
    /// <paramref name="midExtensionId"/> and a non-empty <paramref name="mid"/> are supplied; RID likewise
    /// from <paramref name="ridExtensionId"/> and <paramref name="rid"/>. Each token is validated once here
    /// (id range and the 16-byte one-byte-form limit).
    /// </summary>
    public RtpOutboundHeaderExtensionStamper(
        byte? transportWideCcExtensionId,
        byte? midExtensionId,
        string? mid,
        byte? ridExtensionId = null,
        string? rid = null)
    {
        _transportCcId = transportWideCcExtensionId;

        var constants = new List<RtpHeaderExtensionElement>(2);
        if (midExtensionId is { } midId && !string.IsNullOrEmpty(mid))
            constants.Add(RtpMidHeaderExtension.Element(midId, mid)); // validates id range + length once
        if (ridExtensionId is { } ridId && !string.IsNullOrEmpty(rid))
            constants.Add(RtpRidHeaderExtension.Element(ridId, rid));

        _constantElements = [.. constants];
        _constantOnlyExtension = constants.Count > 0 ? OneByteRtpHeaderExtensions.Encode(constants) : null;
    }

    /// <summary>Whether this stamper adds any header extension at all (transport-cc and/or MID/RID negotiated).</summary>
    public bool StampsAnything => _transportCcId is not null || _constantElements.Length > 0;

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

        if (_constantElements.Length > 0)
        {
            // BUNDLE / simulcast path: the constant MID (and RID) elements always, plus transport-cc when
            // present. The constants re-use their pre-built elements; the combined form is rebuilt per
            // packet because the counter changes.
            if (transportCc is not { } tc)
                return _constantOnlyExtension;

            var combined = new RtpHeaderExtensionElement[_constantElements.Length + 1];
            Array.Copy(_constantElements, combined, _constantElements.Length);
            combined[^1] = tc;
            return OneByteRtpHeaderExtensions.Encode(combined);
        }

        // Non-BUNDLE path (all current calls): transport-cc alone or nothing — byte-identical to before.
        return _transportCcId is { } id && transportCcSequence is { } seq
            ? OneByteRtpHeaderExtensions.EncodeTransportSequenceNumber(id, seq)
            : null;
    }
}
