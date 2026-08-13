using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// Represents one SDP <c>a=candidate</c> attribute (RFC 8839 §5.1).
/// </summary>
internal sealed class SdpIceCandidate
{
    /// <summary>Foundation identifier (RFC 8839 §5.1).</summary>
    public required string Foundation { get; init; }

    /// <summary>Component identifier (1 = RTP, 2 = RTCP).</summary>
    public required int Component { get; init; }

    /// <summary>Transport protocol token, e.g. <c>UDP</c> or <c>TCP</c>.</summary>
    public required string Transport { get; init; }

    /// <summary>Candidate priority (RFC 8445 §5.1.2).</summary>
    public required long Priority { get; init; }

    /// <summary>IP address of the candidate.</summary>
    public required string Address { get; init; }

    /// <summary>Port of the candidate.</summary>
    public required int Port { get; init; }

    /// <summary>Candidate type: <c>host</c>, <c>srflx</c>, <c>prflx</c>, or <c>relay</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Server-reflexive / relay related address (<c>raddr</c>), if present.</summary>
    public string? RelatedAddress { get; init; }

    /// <summary>Server-reflexive / relay related port (<c>rport</c>), if present.</summary>
    public int? RelatedPort { get; init; }

    /// <summary>ICE restart generation number, if present.</summary>
    public int? Generation { get; init; }

    /// <summary>Per-candidate ufrag extension, if present.</summary>
    public string? Ufrag { get; init; }

    /// <summary>Network identifier extension, if present.</summary>
    public int? NetworkId { get; init; }

    /// <summary>
    /// Tries to parse an <c>a=candidate</c> value string (everything after <c>a=candidate:</c>).
    /// Returns <see langword="null"/> on malformed input.
    /// </summary>
    public static SdpIceCandidate? TryParse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Minimum required: foundation component transport priority address port typ type (8 tokens)
        if (parts.Length < 8)
            return null;

        // #160 P2-14: every field is validated against the grammar, not merely parsed. These values go
        // straight into the ICE agent's pairing and priority ordering — a component of 0, a priority of
        // 0, or a type nobody can prioritise is not a large value, it is one the agent cannot act on.
        if (!int.TryParse(parts[1], out var component) || !SdpIceGrammar.IsValidComponent(component))
            return null;

        if (!long.TryParse(parts[3], out var priority) || !SdpIceGrammar.IsValidPriority(priority))
            return null;

        if (!int.TryParse(parts[5], out var port) || !SdpIceGrammar.IsValidPort(port))
            return null;

        if (!parts[6].Equals("typ", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!SdpIceGrammar.IsValidFoundation(parts[0]) || !SdpIceGrammar.IsKnownCandidateType(parts[7]))
            return null;

        string? relatedAddress = null;
        int? relatedPort = null;
        int? generation = null;
        string? ufrag = null;
        int? networkId = null;

        for (var i = 8; i + 1 < parts.Length; i += 2)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "raddr":
                    relatedAddress = parts[i + 1];
                    break;
                case "rport" when int.TryParse(parts[i + 1], out var rport) && SdpIceGrammar.IsValidPort(rport):
                    relatedPort = rport;
                    break;
                case "generation" when int.TryParse(parts[i + 1], out var gen):
                    generation = gen;
                    break;
                case "ufrag":
                    ufrag = parts[i + 1];
                    break;
                case "network-id" when int.TryParse(parts[i + 1], out var nid):
                    networkId = nid;
                    break;
            }
        }

        return new SdpIceCandidate
        {
            Foundation = parts[0],
            Component = component,
            Transport = parts[2],
            Priority = priority,
            Address = parts[4],
            Port = port,
            Type = parts[7],
            RelatedAddress = relatedAddress,
            RelatedPort = relatedPort,
            Generation = generation,
            Ufrag = ufrag,
            NetworkId = networkId
        };
    }

    /// <summary>
    /// Serializes the candidate to its wire string value (without the leading <c>a=candidate:</c>).
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append($"{Foundation} {Component} {Transport} {Priority} {Address} {Port} typ {Type}");
        if (RelatedAddress is not null)
            sb.Append($" raddr {RelatedAddress}");
        if (RelatedPort.HasValue)
            sb.Append($" rport {RelatedPort.Value}");
        if (Generation.HasValue)
            sb.Append($" generation {Generation.Value}");
        if (Ufrag is not null)
            sb.Append($" ufrag {Ufrag}");
        if (NetworkId.HasValue)
            sb.Append($" network-id {NetworkId.Value}");
        return sb.ToString();
    }
}
