using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// Endpoint comparison for the relay source filter. Relayed traffic (and TURN control responses) must come
/// from the relay server's exact transport address; a dual-stack socket may surface that address in
/// IPv4-mapped-IPv6 form (<c>::ffff:a.b.c.d</c>), so both addresses are canonicalised before comparison —
/// the filter neither drops genuine relayed traffic on a dual-stack socket nor lets a different host
/// through (the mapping is a lossless, host-preserving transform). Shared by the channel's own source
/// filter and the media transport's control-plane source filter so the two never diverge.
/// </summary>
internal static class RelayEndPoint
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="a"/> and <paramref name="b"/> denote the same
    /// host and port, treating an IPv4-mapped-IPv6 address as equal to its plain IPv4 form.
    /// </summary>
    public static bool SameEndPoint(IPEndPoint a, IPEndPoint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Port == b.Port && Canonical(a.Address).Equals(Canonical(b.Address));
    }

    private static IPAddress Canonical(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
