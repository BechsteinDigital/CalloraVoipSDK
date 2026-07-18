namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// One SDP <c>a=simulcast</c> attribute (RFC 8853 §5.1): the simulcast streams offered/answered on a media
/// section, e.g. <c>a=simulcast:send hi;mid;lo</c>. Each entry references a <see cref="SdpRid"/> id declared
/// by an <c>a=rid</c> line on the same m-line. <see cref="Send"/> lists the send-direction stream ids and
/// <see cref="Recv"/> the receive-direction ones (either may be empty). Comma alternatives within a single
/// simulcast stream (RFC 8853 §5.1) are preserved verbatim inside an entry.
/// </summary>
internal sealed record SdpSimulcast
{
    /// <summary>The send-direction simulcast stream ids, in declaration order.</summary>
    public IReadOnlyList<string> Send { get; init; } = [];

    /// <summary>The receive-direction simulcast stream ids, in declaration order.</summary>
    public IReadOnlyList<string> Recv { get; init; } = [];

    /// <summary>
    /// Parses the value after <c>a=simulcast:</c> (<c>[send &lt;list&gt;] [recv &lt;list&gt;]</c>, each list
    /// <c>;</c>-separated). Returns <see langword="null"/> when neither a send nor a recv list is present.
    /// </summary>
    public static SdpSimulcast? TryParse(string attrValue)
    {
        if (string.IsNullOrWhiteSpace(attrValue))
            return null;

        var tokens = attrValue.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<string> send = [];
        IReadOnlyList<string> recv = [];

        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            var list = tokens[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (tokens[i].Equals("send", StringComparison.Ordinal))
                send = list;
            else if (tokens[i].Equals("recv", StringComparison.Ordinal))
                recv = list;
        }

        return send.Count == 0 && recv.Count == 0 ? null : new SdpSimulcast { Send = send, Recv = recv };
    }

    /// <summary>Serializes to the value string (without the leading <c>a=simulcast:</c>).</summary>
    public string Serialize()
    {
        var parts = new List<string>(2);
        if (Send.Count > 0)
            parts.Add($"send {string.Join(';', Send)}");
        if (Recv.Count > 0)
            parts.Add($"recv {string.Join(';', Recv)}");
        return string.Join(' ', parts);
    }
}
