using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Bundles the ICE responsibilities layered on a media leg's shared socket: answering inbound
/// connectivity checks (RFC 8445 §7.3, <see cref="IceInboundStunHandler"/>) and running consent
/// freshness (RFC 7675, <see cref="IceMediaConsentSession"/>). A media session builds one of these
/// from the negotiated parameters, feeds it the STUN datagrams demuxed off the receive loop via
/// <see cref="OnStunPacketReceived"/>, calls <see cref="Start"/>, and disposes it — keeping the ICE
/// wiring out of the media session itself.
/// </summary>
internal sealed class IceMediaAttachment : IAsyncDisposable
{
    private readonly IceInboundStunHandler? _inbound;
    private readonly IceMediaConsentSession? _consent;
    private readonly IceNominationDriver? _nominationDriver;
    private IPEndPoint _nominatedRemote;
    private readonly ConcurrentDictionary<IPEndPoint, byte> _triggeredSources = new();
    private readonly Action? _onConsentLost;
    private readonly Action? _onConnectivityDegraded;
    private readonly Action? _onConnectivityRecovered;
    private readonly Action<IPEndPoint>? _onPairNominated;
    // The last pair actually nominated (null until the first nomination), used to skip redundant
    // re-nominations. Keyed on the remote endpoint AND its send path, so a switch between the direct and the
    // relay send path to the same remote still redirects consent rather than being skipped as a no-op.
    // Distinct from _nominatedRemote, which starts at the initial resolved remote for triggered-check
    // deduplication — so the first nomination fires even when it lands on that same remote.
    private IceNominatedTarget? _lastNominated;
    // The relay local candidate's send path (TURN-framed), or null when no TURN allocation was gathered.
    // Held so a relay nomination can redirect consent freshness through it (RFC 7675 over the allocation).
    // Set at construction (offerer, whose allocation is gathered before the session) or post-construction via
    // AddRelayLocalCandidate (answerer late adoption). Written on the gather thread, read on the driver loop —
    // accessed under Volatile so the read sees the store the relay candidate's Check closure was built with.
    private Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? _relaySend;
    private readonly ILogger<IceMediaAttachment> _logger;

    /// <summary>
    /// Builds the attachment from the ICE view of the media 5-tuple and the media socket's raw-send
    /// delegate. Both the inbound handler and the consent session are optional — absent when ICE or
    /// the required credentials are not present. When this agent is controlling and the parameters carry
    /// remote candidates, a nomination driver runs connectivity checks and nominates a pair (RFC 8445 §7/§8);
    /// a controlled agent adopts the pair the peer nominates via its USE-CANDIDATE check.
    /// </summary>
    /// <param name="onPairNominated">
    /// Invoked once with the nominated remote endpoint so the caller can redirect the media send target to
    /// the checked pair (typically the transport's <c>SetRemoteEndPoint</c>). Consent freshness is redirected
    /// internally.
    /// </param>
    /// <param name="relaySend">
    /// The TURN-framed send path of a relay local candidate — <c>(datagram, remoteTarget, ct)</c>, which frames
    /// the datagram to the remote through the TURN allocation (Send indication, RFC 8656 §10). When supplied,
    /// a controlling agent adds a relay local candidate (type preference 0, below host/srflx) so a relayed pair
    /// is checked and, if no direct pair works, nominated — at which point consent freshness runs over this
    /// path. Absent (<see langword="null"/>) when no TURN allocation was gathered — leaving behaviour identical
    /// to the direct-only path.
    /// </param>
    public IceMediaAttachment(
        IceMediaParameters parameters,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        ILoggerFactory loggerFactory,
        Action? onConsentLost = null,
        Action? onConnectivityDegraded = null,
        Action? onConnectivityRecovered = null,
        Action<IPEndPoint>? onPairNominated = null,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? relaySend = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<IceMediaAttachment>();
        _onConsentLost = onConsentLost;
        _onConnectivityDegraded = onConnectivityDegraded;
        _onConnectivityRecovered = onConnectivityRecovered;
        _onPairNominated = onPairNominated;
        _relaySend = relaySend;
        _nominatedRemote = parameters.RemoteEndPoint;
        _inbound = parameters.IceEnabled
            ? IceInboundStunHandlerFactory.Create(
                parameters.LocalIceUfrag, parameters.LocalIcePwd, parameters.IceControlling, sendRaw, loggerFactory)
            : null;
        _consent = IceMediaConsentSessionFactory.TryCreate(
            parameters, sendRaw, OnConsentLost, loggerFactory, _onConnectivityDegraded, _onConnectivityRecovered);

        // The controlling agent drives candidate-pair checks and regular nomination (RFC 8445 §7.2.2/§8.1.1);
        // it needs the consent session's shared-socket check primitive. Built even with an empty seed so
        // candidates that only arrive via trickle (RFC 8838, AddRemoteCandidate) are still checked.
        if (_consent is not null && parameters.IceControlling)
        {
            // The direct local candidate: host and server-reflexive share the media socket's direct send path
            // (srflx is only the mapped view of the same socket).
            var localCandidates = new List<IceLocalCandidate>
            {
                new()
                {
                    Type = HostCandidateType,
                    Priority = HostLocalCandidatePriority,
                    Check = _consent.SendCheckAsync,
                },
            };

            // The relay local candidate, when a TURN allocation was gathered: its checks are framed through the
            // TURN server via the injected relay send path. The driver pairs every local candidate against every
            // remote and orders by pair priority (RFC 8445 §6.1.2.3); with type preference 0 the relay pairs sit
            // below host/srflx, so a relayed pair is only nominated when no direct pair works — direct-preferred
            // selection falls out for free.
            if (relaySend is not null)
            {
                localCandidates.Add(new IceLocalCandidate
                {
                    Type = RelayCandidateType,
                    Priority = RelayLocalCandidatePriority,
                    Check = (remote, useCandidate, ct) => _consent.SendCheckVia(relaySend, remote, useCandidate, ct),
                });
            }

            _nominationDriver = new IceNominationDriver(
                localCandidates,
                parameters.RemoteCandidates,
                OnDriverNominated,
                loggerFactory);
        }

        if (_inbound is not null)
        {
            _inbound.CheckAccepted += OnInboundCheckAccepted;
            // The controlled agent adopts the pair the controlling peer nominates (RFC 8445 §7.3.1.5).
            _inbound.PairNominated += Nominate;
        }
    }

    // RFC 8445 §7.3.1.4: a valid inbound check from a source other than the nominated remote reveals
    // a peer-reflexive path; trigger one confirming connectivity check back to it (learn-once per
    // source). The nominated remote is already validated continuously by consent freshness.
    private void OnInboundCheckAccepted(IPEndPoint source)
    {
        if (_consent is null || source.Equals(Volatile.Read(ref _nominatedRemote)))
            return;
        if (!_triggeredSources.TryAdd(source, 0))
            return;

        _logger.LogDebug("ICE triggered check to peer-reflexive source {Source} (RFC 8445 §7.3.1.4).", source);
        _ = SendTriggeredCheckAsync(source);
    }

    private async Task SendTriggeredCheckAsync(IPEndPoint source)
    {
        try
        {
            var confirmed = await _consent!.SendCheckAsync(source, useCandidate: false, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("ICE triggered check to {Source} {Result}.", source, confirmed ? "confirmed" : "unanswered");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICE triggered check to {Source} failed.", source);
        }
    }

    /// <summary>True when either ICE responsibility is active and the attachment should receive STUN.</summary>
    public bool IsActive => _inbound is not null || _consent is not null;

    /// <summary>
    /// Starts the consent-freshness loop and, on a controlling agent with candidates, the connectivity-check
    /// nomination driver (no-op when the respective responsibility is inactive). Call after the transport's
    /// receive loop is running so the driver's checks are answered and matched over the shared socket.
    /// </summary>
    public void Start()
    {
        _consent?.Start();
        _nominationDriver?.Start();
    }

    /// <summary>
    /// Adds a remote candidate discovered after negotiation (RFC 8838 trickle) to the connectivity-check
    /// list, so it is checked (and possibly nominated) rather than trusted by raw priority. No-op on a
    /// controlled agent — which has no driver and adopts the pair the controlling peer nominates — or when
    /// ICE is inactive.
    /// </summary>
    /// <param name="candidate">The trickled remote candidate.</param>
    public void AddRemoteCandidate(IceRemoteCandidate candidate) => _nominationDriver?.AddCandidate(candidate);

    /// <summary>
    /// Adds the relay ICE local candidate after construction (RFC 8445 §5.1.1.2) — the answerer path, whose
    /// TURN allocation only finishes gathering once this attachment already exists, so its relay path could not
    /// be seeded at construction the way the offerer's is. Stores the relay send path so a later relay
    /// nomination redirects consent freshness through it, then hands the nomination driver a relay local
    /// candidate (type preference 0, below host/srflx) paired against every remote — so a relayed pair is
    /// checked and, if no direct pair works, nominated. No-op on a controlled agent (no driver — it adopts the
    /// pair the controlling peer nominates) or when consent/ICE is inactive. Call at most once: a connection is
    /// offerer XOR answerer for a given allocation, and the offerer already seeded its relay candidate.
    /// </summary>
    /// <param name="relaySend">
    /// The relay local candidate's TURN-framed send path — <c>(datagram, remoteTarget, ct)</c>, framing the
    /// datagram to the remote through the TURN allocation (Send indication, RFC 8656 §10).
    /// </param>
    public void AddRelayLocalCandidate(
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> relaySend)
    {
        ArgumentNullException.ThrowIfNull(relaySend);
        if (_nominationDriver is null || _consent is null)
            return;

        // Store before handing the candidate to the driver so a relay nomination that fires as soon as the new
        // pair is checked observes the send path for OnDriverNominated's consent redirect. Volatile pairs with
        // the read there; the driver's _gate additionally orders the pair's visibility.
        Volatile.Write(ref _relaySend, relaySend);
        _nominationDriver.AddLocalCandidate(new IceLocalCandidate
        {
            Type = RelayCandidateType,
            Priority = RelayLocalCandidatePriority,
            Check = (remote, useCandidate, ct) => _consent.SendCheckVia(relaySend, remote, useCandidate, ct),
        });
    }

    private const string HostCandidateType = "host";
    private const string RelayCandidateType = "relay";

    // RFC 8445 §5.1.2.1 priority of the direct (host) local candidate: type preference 126, full local
    // preference, RTP component.
    private const long HostLocalCandidatePriority = ((long)126 << 24) + (65535L << 8) + 255;

    // RFC 8445 §5.1.2.1 priority of the relay local candidate: type preference 0 (below host's 126), full
    // local preference, RTP component — so relay pairs sit below every direct pair in pair priority and are
    // only nominated when no host/srflx pair works.
    private const long RelayLocalCandidatePriority = ((long)0 << 24) + (65535L << 8) + 255;

    // The controlling driver reports the nominated pair's local candidate and remote endpoint. A relay
    // candidate additionally routes consent freshness through the relay send path; a direct (host/srflx)
    // candidate nominates over the shared media socket, exactly as before.
    private void OnDriverNominated(IceLocalCandidate local, IPEndPoint remoteEndPoint)
    {
        var sendVia = local.Type == RelayCandidateType ? Volatile.Read(ref _relaySend) : null;
        NominateInternal(remoteEndPoint, sendVia);
    }

    // Adopts the pair the controlling peer nominates via its inbound USE-CANDIDATE check (RFC 8445 §7.3.1.5).
    // A controlled agent sends back to the source of the check over the shared media socket, so its nomination
    // always uses the direct send path.
    private void Nominate(IPEndPoint remoteEndPoint) => NominateInternal(remoteEndPoint, sendVia: null);

    // Nominates a checked/adopted remote pair (RFC 8445 §8): redirects consent freshness onto it (over the
    // given send path — relay-framed or direct) and reports it so the transport send target and DTLS follow.
    // Funnelled from both the controlling driver (which can re-nominate onto a higher-priority working pair)
    // and the controlled agent's inbound USE-CANDIDATE. A no-op re-nomination to the pair already in effect is
    // skipped (redundant redirect / log spam).
    private void NominateInternal(
        IPEndPoint remoteEndPoint,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? sendVia)
    {
        var previous = Interlocked.Exchange(ref _lastNominated, new IceNominatedTarget(remoteEndPoint, sendVia));
        // Skip only a genuine no-op: same remote AND same send path. A relay↔direct switch to the same remote
        // (same endpoint, different path) must still redirect consent, so it is not treated as redundant.
        if (previous is not null
            && previous.Remote.Equals(remoteEndPoint)
            && ReferenceEquals(previous.Send, sendVia))
        {
            return;
        }

        Volatile.Write(ref _nominatedRemote, remoteEndPoint);
        _consent?.Nominate(remoteEndPoint, sendVia);
        try
        {
            _onPairNominated?.Invoke(remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ICE pair-nominated handler.");
        }
    }

    /// <summary>
    /// Routes a STUN datagram demuxed off the media socket by class (RFC 5389 §6): Success/Error
    /// responses answer our consent checks (RFC 7675); everything else is an inbound
    /// connectivity-check request (RFC 8445 §7.3). Matches the transport's
    /// <c>StunPacketReceived(byte[], IPEndPoint)</c> hook signature.
    /// </summary>
    public void OnStunPacketReceived(byte[] datagram, IPEndPoint source)
    {
        if (_consent is not null
            && datagram.Length >= 2
            && (BinaryPrimitives.ReadUInt16BigEndian(datagram) & 0x0110) is 0x0100 or 0x0110)
        {
            _consent.OnStunResponse(datagram);
            return;
        }

        _inbound?.OnStunPacketReceived(datagram, source);
    }

    private void OnConsentLost()
    {
        _logger.LogWarning("ICE consent lost (RFC 7675): no consent check answered within the consent lifetime.");
        try { _onConsentLost?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "Unhandled exception in ICE consent-lost handler."); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_nominationDriver is not null)
            await _nominationDriver.DisposeAsync().ConfigureAwait(false);
        if (_consent is not null)
            await _consent.DisposeAsync().ConfigureAwait(false);
    }
}
