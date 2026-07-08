using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Pure decision logic for whether an inbound INVITE belongs to a registered line.
/// <para>
/// A strict "To-user == registration username" match only fits 1:1 user accounts. SIP
/// trunks (e.g. sipgate SIPconnect) deliver every called number (DID) to one registration,
/// so the INVITE's To-user is the dialed number, not the trunk credential. Following how
/// established stacks route trunk inbound (PJSIP best-match, FreeSWITCH gateway/ACL), the
/// default accepts a call that is either for our exact user, delivered by the registrar we
/// registered to (peer match), or addressed to our registered domain. An explicit DID
/// whitelist tightens this for multi-line disambiguation.
/// </para>
/// </summary>
internal static class TrunkInboundMatcher
{
    /// <summary>
    /// Decides whether the session's inbound INVITE belongs to this line.
    /// </summary>
    /// <param name="localUri">The INVITE local (To/Request) URI.</param>
    /// <param name="accountUsername">Registration username of the line.</param>
    /// <param name="accountDomain">Registrar domain (<c>SipServer</c>) of the line.</param>
    /// <param name="sourceAddress">Source address the INVITE arrived from, when known.</param>
    /// <param name="trustedRegistrarAddresses">
    /// Resolved addresses of the registrar/proxy this line registered to (peer trust).
    /// </param>
    /// <param name="inboundNumbers">
    /// Optional DID whitelist. When non-empty, only these numbers (on the account domain)
    /// are accepted — everything else is rejected, regardless of peer/domain.
    /// </param>
    /// <param name="acceptTrunkInbound">
    /// When <see langword="false"/> only the exact username is accepted (strict 1:1 account);
    /// peer- and domain-based trunk acceptance are disabled. Ignored when a whitelist is set.
    /// </param>
    public static bool IsForThisLine(
        string localUri,
        string accountUsername,
        string accountDomain,
        IPAddress? sourceAddress,
        IReadOnlyCollection<IPAddress> trustedRegistrarAddresses,
        IReadOnlyCollection<string>? inboundNumbers,
        bool acceptTrunkInbound)
    {
        if (!SipProtocol.TryParseSipUri(localUri, out var localUser, out var localHost, out _))
            return false;

        var domainMatches = string.Equals(localHost, accountDomain, StringComparison.OrdinalIgnoreCase);

        // Restrictive mode: only the whitelisted DIDs on our domain (multi-line safe).
        if (inboundNumbers is { Count: > 0 })
            return domainMatches && inboundNumbers.Contains(localUser, StringComparer.OrdinalIgnoreCase);

        // 1:1 user account — exact user match (always accepted).
        if (string.Equals(localUser, accountUsername, StringComparison.OrdinalIgnoreCase))
            return true;

        // Strict 1:1 account: no peer/domain broadening (opt-out of trunk acceptance).
        if (!acceptTrunkInbound)
            return false;

        // Trunk: delivered by the registrar/proxy we registered to (peer trust).
        if (sourceAddress is not null && trustedRegistrarAddresses.Contains(sourceAddress))
            return true;

        // Trunk: addressed to our registered domain. Providers deliver from IPs that need
        // not appear in the registrar's DNS records, so this domain match is the reliable
        // fallback that peer trust alone cannot cover. Callers that need strict source
        // control set AcceptTrunkInbound=false or an InboundNumbers whitelist.
        return domainMatches;
    }
}
