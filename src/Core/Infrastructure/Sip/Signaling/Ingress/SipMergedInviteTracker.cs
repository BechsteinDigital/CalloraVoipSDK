using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Tracks out-of-dialog INVITE identity tuples to distinguish merged requests
/// (RFC 3261 §8.2.2.2) from plain retransmissions. A second INVITE that shares the
/// Call-ID, From tag and CSeq of a recent one is only a merged request when its topmost
/// Via branch <em>differs</em> — the §17.2.3 transaction-matching rule keys on that branch.
/// An identical branch is a retransmission and must not be rejected with 482 Loop Detected.
/// </summary>
internal sealed class SipMergedInviteTracker
{
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(2);

    // Identity tuple (Call-ID|From-tag|CSeq|INVITE) -> topmost Via branch first seen for it,
    // plus when. The branch separates a merge (different branch) from a retransmission
    // (same branch).
    private readonly ConcurrentDictionary<string, (string? Branch, DateTimeOffset SeenAt)> _seen =
        new(StringComparer.Ordinal);
    private int _registrations;

    /// <summary>
    /// Registers one candidate INVITE. Returns true only when the request is a merged
    /// duplicate: same identity tuple as a recent INVITE but a different topmost Via branch.
    /// A repeat carrying the same branch (a retransmission) returns false.
    /// </summary>
    public bool IsMergedInvite(SipRequest request)
    {
        if (!string.Equals(request.Method, "INVITE", StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrWhiteSpace(SipProtocol.ExtractTag(request.Header("To"))))
            return false;

        if (!TryBuildMergeKey(request, out var key))
            return false;

        var branch = SipProtocol.ExtractViaBranch(request.Header("Via"));
        var now = DateTimeOffset.UtcNow;

        if (_seen.TryGetValue(key, out var existing) && now - existing.SeenAt <= EntryTtl)
        {
            // Same identity tuple within the window. A different topmost Via branch means the
            // request forked and merged back (RFC 3261 §8.2.2.2) → merged. An identical branch
            // — or a branch we cannot parse on either side — is a retransmission → not merged.
            if (!string.IsNullOrWhiteSpace(branch)
                && !string.IsNullOrWhiteSpace(existing.Branch)
                && !string.Equals(branch, existing.Branch, StringComparison.Ordinal))
            {
                return true;
            }

            // Retransmission: keep the original branch, refresh the window.
            _seen[key] = (existing.Branch, now);
            return false;
        }

        _seen[key] = (branch, now);
        var registrationCount = Interlocked.Increment(ref _registrations);
        if (registrationCount % 64 == 0)
            PruneExpired(now);

        return false;
    }

    /// <summary>
    /// Prunes expired merge keys to keep bounded memory.
    /// </summary>
    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _seen)
        {
            if (now - pair.Value.SeenAt <= EntryTtl)
                continue;
            _seen.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>
    /// Builds one merge identifier from Call-ID, From tag and CSeq number.
    /// </summary>
    private static bool TryBuildMergeKey(SipRequest request, out string key)
    {
        key = string.Empty;
        var callId = request.Header("Call-ID");
        var fromTag = SipProtocol.ExtractTag(request.Header("From"));
        var cseqNumber = SipProtocol.ExtractCSeqNumber(request.Header("CSeq"));

        if (string.IsNullOrWhiteSpace(callId)
            || string.IsNullOrWhiteSpace(fromTag)
            || cseqNumber <= 0)
        {
            return false;
        }

        key = $"{callId}|{fromTag}|{cseqNumber}|INVITE";
        return true;
    }
}
