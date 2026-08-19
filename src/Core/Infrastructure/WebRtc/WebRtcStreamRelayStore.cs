using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Owns the peer's gathered stream relay candidate (ADR-073) — the stream analog of
/// <see cref="WebRtcRelayAllocationStore"/>. A stream relay owns its own TCP/TLS connection (not the media
/// socket), so unlike the UDP allocation it is adopted post-construction through
/// <see cref="BundledMediaSession.AdoptStreamRelay"/>: immediately when a session already exists (the answerer,
/// whose session was built before gathering) or on the next session build (the offerer, which gathers before
/// applying the answer, so its session does not exist yet).
/// <para>
/// First-wins: the first gathered candidate is retained and advertised; a later one is surplus and is disposed by
/// the caller. Ownership of the retained candidate transfers to the session on adoption (the session disposes it);
/// a candidate that is never adopted (an offerer whose session is never built) is disposed here.
/// </para>
/// </summary>
internal sealed class WebRtcStreamRelayStore : IAsyncDisposable
{
    private readonly ILogger<WebRtcStreamRelayStore> _logger;
    private readonly object _sync = new();
    private IStreamRelayAttachment? _retained;
    private bool _adoptedBySession;

    /// <summary>Creates the store.</summary>
    /// <param name="loggerFactory">Logger factory.</param>
    public WebRtcStreamRelayStore(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<WebRtcStreamRelayStore>();
    }

    /// <summary>
    /// Records a freshly gathered stream relay candidate, first-wins. Reads the caller's session snapshot inside
    /// the same lock that latches, so a concurrent gather and session build cannot interleave into a lost
    /// adoption. When a session already exists it is adopted now (the answerer); otherwise the candidate waits for
    /// <see cref="AdoptInto"/> (the offerer). The adopt itself runs outside the lock.
    /// </summary>
    /// <param name="candidate">The gathered stream relay candidate.</param>
    /// <param name="sessionSnapshot">Snapshots the peer's current media session (the adopt target), or null.</param>
    /// <returns>
    /// <see langword="true"/> when this candidate was retained (the caller advertises its relay candidate);
    /// <see langword="false"/> when one was already retained (first-wins — the caller disposes the surplus).
    /// </returns>
    public bool OnGathered(IStreamRelayAttachment candidate, Func<BundledMediaSession?> sessionSnapshot)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sessionSnapshot);

        BundledMediaSession? adoptInto;
        lock (_sync)
        {
            if (_retained is not null)
                return false;
            _retained = candidate;
            adoptInto = sessionSnapshot();
            if (adoptInto is not null)
                _adoptedBySession = true;
        }

        if (adoptInto is not null)
        {
            _logger.LogDebug("Adopting a gathered stream relay into the existing session (answerer path).");
            adoptInto.AdoptStreamRelay(candidate);
        }
        return true;
    }

    /// <summary>
    /// Adopts the retained stream relay into a freshly built session — the offerer path, whose session did not
    /// exist at gather time. Idempotent: a no-op when nothing was retained or it was already adopted (at gather,
    /// or by a prior build on a renegotiation).
    /// </summary>
    /// <param name="session">The session to adopt the retained stream relay into.</param>
    public void AdoptInto(BundledMediaSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        IStreamRelayAttachment? adopt = null;
        lock (_sync)
        {
            if (_retained is not null && !_adoptedBySession)
            {
                adopt = _retained;
                _adoptedBySession = true;
            }
        }

        if (adopt is not null)
        {
            _logger.LogDebug("Adopting the retained stream relay into the freshly built session (offerer path).");
            session.AdoptStreamRelay(adopt);
        }
    }

    /// <summary>The retained candidate's advertised relayed address, or <see langword="null"/> when none was gathered.</summary>
    public IPEndPoint? RelayedEndPoint
    {
        get { lock (_sync) { return _retained?.RelayedEndPoint; } }
    }

    /// <summary>
    /// Disposes a retained stream relay that no session ever took ownership of (an offerer whose session was never
    /// built). A candidate adopted by a session is disposed by that session, so it is not disposed here.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        IStreamRelayAttachment? orphan;
        lock (_sync)
        {
            orphan = _adoptedBySession ? null : _retained;
            _retained = null;
        }

        if (orphan is not null)
            await orphan.DisposeAsync().ConfigureAwait(false);
    }
}
