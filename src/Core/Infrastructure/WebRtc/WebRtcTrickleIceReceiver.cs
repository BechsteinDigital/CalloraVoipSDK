using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Receives remote ICE candidates that trickle in out-of-band (RFC 8838) for a <see cref="WebRtcPeerConnection"/>
/// and routes them to the connectivity-check list. Candidates that arrive before the media session exists are
/// buffered; once the peer attaches the built session (<see cref="AttachSession"/>) the buffer is drained and
/// later candidates go straight to the live check list. mDNS (<c>.local</c>) candidates are resolved in the
/// background, bound to the peer lifetime. Parsing and mDNS resolution live here so the peer stays under the
/// file-size limit; the buffering is guarded by an internal lock so a concurrent attach never loses a candidate.
/// </summary>
internal sealed class WebRtcTrickleIceReceiver
{
    private readonly IMdnsResolver _mdnsResolver;
    private readonly CancellationToken _mdnsLifetime;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    // Candidates buffered until the session (and its check list) exist, under _gate so a concurrent AttachSession
    // cannot lose one (RFC 8838). Cleared on attach; a live session routes straight through.
    private readonly List<(IPEndPoint Endpoint, long Priority)> _pending = [];
    private BundledMediaSession? _session;

    /// <summary>Creates a receiver resolving mDNS via <paramref name="mdnsResolver"/>, bound to <paramref name="mdnsLifetime"/>.</summary>
    public WebRtcTrickleIceReceiver(IMdnsResolver mdnsResolver, CancellationToken mdnsLifetime, ILogger logger)
    {
        _mdnsResolver = mdnsResolver ?? throw new ArgumentNullException(nameof(mdnsResolver));
        _mdnsLifetime = mdnsLifetime;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Attaches the built media session and drains the buffered candidates into its check list, atomically under
    /// the receiver's gate so a candidate that arrives concurrently is either drained here or routed live — never
    /// lost (RFC 8838). Idempotent per session; a later re-attach (renegotiation keeps the same session) is a no-op.
    /// </summary>
    public void AttachSession(BundledMediaSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        (IPEndPoint Endpoint, long Priority)[] pending;
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
                return;
            _session = session;
            pending = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var candidate in pending)
            session.AddRemoteCandidate(candidate.Endpoint, candidate.Priority);
    }

    /// <summary>
    /// Parses an RFC 8829 <c>candidate:</c> line and routes it: an IP candidate is enqueued immediately; an mDNS
    /// (<c>.local</c>) one is resolved in the background then enqueued; a malformed or unusable one is ignored.
    /// </summary>
    public void Add(string candidateLine)
    {
        if (WebRtcIceCandidateFactory.ParseTrickleCandidate(candidateLine) is not { } parsed)
        {
            _logger.LogDebug("Ignoring an unusable trickled ICE candidate.");
            return;
        }

        if (IPAddress.TryParse(parsed.Address, out var ip))
        {
            Enqueue(new IPEndPoint(ip, parsed.Port), parsed.Priority);
            return;
        }

        if (parsed.Address.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            // mDNS (.local) candidate: resolve in the background (RFC 8838 — candidates arrive asynchronously, so
            // the signalling path must not block on the resolver), bound to the peer lifetime. No throttle:
            // well-behaved peers send <20 candidates; each task lives at most the resolver timeout (3 s) and is
            // cancelled on dispose. Flood protection is the caller's concern (connection rate-limiting above).
            _ = ResolveAndEnqueueAsync(parsed.Address, parsed.Port, parsed.Priority);
            return;
        }

        _logger.LogDebug("Ignoring an unusable trickled ICE candidate.");
    }

    private async Task ResolveAndEnqueueAsync(string host, int port, long priority)
    {
        try
        {
            var ip = await _mdnsResolver.ResolveAsync(host, _mdnsLifetime).ConfigureAwait(false);
            if (ip is null)
            {
                _logger.LogDebug("An mDNS (.local) trickled ICE candidate could not be resolved; relying on the peer's other candidates.");
                return;
            }
            Enqueue(new IPEndPoint(ip, port), priority);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Peer disposed mid-resolution (token cancelled, or CTS read during the teardown race) — expected.
            _logger.LogTrace("mDNS (.local) ICE candidate resolution abandoned during peer teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolving an mDNS (.local) trickled ICE candidate failed.");
        }
    }

    // Routes one resolved candidate: to the live session's check list, or the buffer under _gate so a concurrent
    // AttachSession picks it up (RFC 8838 no-loss).
    private void Enqueue(IPEndPoint endpoint, long priority)
    {
        BundledMediaSession? session;
        lock (_gate)
        {
            session = _session;
            if (session is null)
            {
                _pending.Add((endpoint, priority));
                return;
            }
        }

        session.AddRemoteCandidate(endpoint, priority);
    }
}
