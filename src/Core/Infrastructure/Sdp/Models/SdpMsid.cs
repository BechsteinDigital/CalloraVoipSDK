namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// One SDP <c>a=msid</c> attribute (RFC 8830): the WebRTC MediaStream and track identity for a media
/// section, e.g. <c>a=msid:stream-id track-id</c>. <see cref="StreamId"/> is the MediaStream id (or
/// <c>-</c> for no associated stream); <see cref="TrackId"/> is the optional per-track appdata that
/// Unified-Plan browsers always include. Used to associate a received RTP track with its MediaStream.
/// </summary>
internal sealed record SdpMsid
{
    /// <summary>MediaStream identifier (<c>msid-id</c>), or <c>-</c> for no associated stream.</summary>
    public required string StreamId { get; init; }

    /// <summary>Optional track identifier (<c>msid-appdata</c>); <see langword="null"/> when absent.</summary>
    public string? TrackId { get; init; }

    /// <summary>
    /// Parses the value after <c>a=msid:</c> (<c>&lt;stream-id&gt; [track-id]</c>).
    /// Returns <see langword="null"/> when no stream id is present.
    /// </summary>
    public static SdpMsid? TryParse(string attrValue)
    {
        if (string.IsNullOrWhiteSpace(attrValue))
            return null;

        var parts = attrValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        return new SdpMsid
        {
            StreamId = parts[0],
            TrackId = parts.Length > 1 ? parts[1] : null,
        };
    }

    /// <summary>Serializes to the value string (without the leading <c>a=msid:</c>).</summary>
    public string Serialize() =>
        TrackId is null ? StreamId : $"{StreamId} {TrackId}";
}
