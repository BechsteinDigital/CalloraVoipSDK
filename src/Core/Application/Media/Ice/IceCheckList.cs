using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// Forms and orders the ICE check list for one media stream (RFC 8445 §6.1.2): pairs the local
/// and remote candidates, computes pair priorities for the agent's role, prunes redundant
/// pairs, orders by priority, and assigns the initial Frozen/Waiting states via foundation
/// freezing. Pure computation — connectivity checks and state advancement are layered on top.
/// </summary>
internal static class IceCheckList
{
    /// <summary>Upper bound on the number of candidate pairs kept (RFC 8445 §6.1.2.5).</summary>
    public const int MaxPairs = 100;

    /// <summary>
    /// Builds the ordered, initially-stated check list for the given local and remote
    /// candidates and role.
    /// </summary>
    public static IReadOnlyList<IceCandidatePair> Create(
        IReadOnlyList<CallIceCandidate> locals,
        IReadOnlyList<CallIceCandidate> remotes,
        IceRole role)
    {
        ArgumentNullException.ThrowIfNull(locals);
        ArgumentNullException.ThrowIfNull(remotes);

        var pairs = new List<IceCandidatePair>();
        foreach (var local in locals)
        {
            foreach (var remote in remotes)
            {
                if (!CanPair(local, remote))
                    continue;

                pairs.Add(new IceCandidatePair
                {
                    Local = local,
                    Remote = remote,
                    Priority = IceCandidatePair.ComputePriority(role, local.Priority, remote.Priority),
                    Foundation = $"{local.Foundation}|{remote.Foundation}",
                });
            }
        }

        var ordered = Prune(pairs)
            .OrderByDescending(p => p.Priority)
            .Take(MaxPairs)
            .ToList();

        AssignInitialStates(ordered);
        return ordered;
    }

    // RFC 8445 §6.1.2.2: pair a local and remote candidate only when they share the same
    // component, transport, and IP address family.
    private static bool CanPair(CallIceCandidate local, CallIceCandidate remote)
    {
        if (local.Component != remote.Component)
            return false;
        if (!string.Equals(local.Transport, remote.Transport, StringComparison.OrdinalIgnoreCase))
            return false;
        return AddressFamilyOf(local.Address) == AddressFamilyOf(remote.Address);
    }

    private static AddressFamily? AddressFamilyOf(string address) =>
        IPAddress.TryParse(address, out var ip) ? ip.AddressFamily : null;

    // RFC 8445 §6.1.2.4: a server-reflexive local candidate is redundant with its base for
    // pairing, so pairs are keyed on the local base (host) address. Keep the highest-priority
    // pair for each (local base, remote, component) key.
    private static List<IceCandidatePair> Prune(List<IceCandidatePair> pairs)
    {
        var kept = new Dictionary<string, IceCandidatePair>(StringComparer.Ordinal);
        foreach (var pair in pairs.OrderByDescending(p => p.Priority))
        {
            var key = $"{LocalBaseKey(pair.Local)}=>{pair.Remote.Address}:{pair.Remote.Port}#{pair.ComponentId}";
            if (!kept.ContainsKey(key))
                kept[key] = pair;
        }

        return kept.Values.ToList();
    }

    private static string LocalBaseKey(CallIceCandidate local) =>
        string.Equals(local.Type, "srflx", StringComparison.OrdinalIgnoreCase)
            && local.RelatedAddress is not null
            ? $"{local.RelatedAddress}:{local.RelatedPort}"
            : $"{local.Address}:{local.Port}";

    // RFC 8445 §6.1.2.6: for each foundation, the pair with the lowest component id (tie:
    // highest priority) starts in Waiting; all other pairs start Frozen.
    private static void AssignInitialStates(List<IceCandidatePair> ordered)
    {
        foreach (var group in ordered.GroupBy(p => p.Foundation, StringComparer.Ordinal))
        {
            var waiting = group
                .OrderBy(p => p.ComponentId)
                .ThenByDescending(p => p.Priority)
                .First();
            waiting.State = IceCandidatePairState.Waiting;
        }
    }
}
