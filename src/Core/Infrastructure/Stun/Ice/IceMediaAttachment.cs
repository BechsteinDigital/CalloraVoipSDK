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
/// <see cref="OnStunPacketReceived(byte[], System.Net.IPEndPoint)"/>, calls <see cref="Start"/>, and disposes it — keeping the ICE
/// wiring out of the media session itself.
/// </summary>
internal sealed class IceMediaAttachment : IAsyncDisposable
{
    private readonly IceInboundStunHandler? _inbound;
    private readonly IceMediaConsentSession? _consent;
    private readonly IceNominationDriver? _nominationDriver;
    private readonly ConcurrentDictionary<IPEndPoint, byte> _triggeredSources = new();
    private readonly Action? _onConsentLost;
    private readonly Action? _onConnectivityDegraded;
    private readonly Action? _onConnectivityRecovered;
    private readonly Action<IPEndPoint>? _onPairNominated;
    private readonly Action<IPEndPoint>? _onSourceValidated;
    // Fires additionally to _onPairNominated when the nominated pair is a relay pair (its send path is relay-
    // framed), so the caller can switch the transport onto the relay data path (RFC 8656 ChannelBind). Direct
    // pairs never fire it.
    private readonly Action<IPEndPoint>? _onRelayPairNominated;
    // First nomination wins for this ICE generation (RFC 8445 §8.1.1). A later path change requires restart.
    private IceNominatedTarget? _lastNominated;
    // The signalled remote endpoint. It already has its own checklist pair, so an inbound check from it is the
    // expected bidirectional confirmation, not a peer-reflexive discovery (RFC 8445 §7.3.1.3) — never triggered.
    private readonly IPEndPoint _initialRemote;
    private int _controlling;
    // The relay local candidate's send path (TURN-framed), or null when no TURN allocation was gathered.
    // Held so a relay nomination can redirect consent freshness through it (RFC 7675 over the allocation).
    // Set at construction (offerer, whose allocation is gathered before the session) or post-construction via
    // AddRelayLocalCandidate (answerer late adoption). Written on the gather thread, read on the driver loop —
    // accessed under Volatile so the read sees the store the relay candidate's Check closure was built with.
    private Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? _relaySend;
    // Installs a TURN permission (RFC 8656 §9) for a peer IP over this agent's relay allocation. Set together
    // with _relaySend on a controlled (answerer) agent that adopts a relay candidate, so the agent proactively
    // permissions every offerer remote-candidate IP BEFORE the offerer's inbound relay check arrives — without
    // it the TURN server silently drops the inbound check (§9). Null on the controlling path (the send path
    // installs the permission itself when it sends a relayed check) and when no relay allocation was gathered.
    // Volatile: written on the adoption thread, read on the trickle thread (AddRemoteCandidate).
    private Func<IPAddress, CancellationToken, Task>? _ensurePermission;
    // The IPs of remote candidates seen so far, so that when a relay candidate is adopted late (answerer path,
    // where the offer's SDP remote candidates and early trickle arrive BEFORE the relay's permission installer
    // exists) the already-known peer IPs are proactively permissioned at adoption time — otherwise their inbound
    // relay checks would be dropped. Deduplicated per IP; entries are cheap (a handful of offerer candidates).
    private readonly ConcurrentDictionary<IPAddress, byte> _seenRemoteAddresses = new();
    private readonly ILogger<IceMediaAttachment> _logger;

    /// <summary>
    /// Builds the attachment from the ICE view of the media 5-tuple and the media socket's raw-send
    /// delegate. Both the inbound handler and the consent session are optional — absent when ICE or
    /// the required credentials are not present. When this agent is controlling and the parameters carry
    /// remote candidates, a nomination driver runs connectivity checks and nominates a pair (RFC 8445 §7/§8);
    /// a controlled agent adopts the pair the peer nominates via its USE-CANDIDATE check.
    /// </summary>
    /// <param name="onSourceValidated">
    /// Invoked with a remote source whose inbound check verified against our ICE credential, before any pair
    /// is nominated and possibly several times. Lets a consumer stop discarding traffic from a peer that is
    /// authenticated but not yet chosen — the DTLS source filter is the case this exists for.
    /// </param>
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
    /// <param name="onRelayPairNominated">
    /// Invoked in addition to <paramref name="onPairNominated"/> only when the nominated pair is a relay pair, so
    /// the caller can switch the transport onto the relay data path (RFC 8656 ChannelBind). A direct nomination
    /// never fires it. <see langword="null"/> leaves the relay data path unswitched (checks/consent still run
    /// relay-framed, but media stays on whatever the transport last targeted).
    /// </param>
    public IceMediaAttachment(
        IceMediaParameters parameters,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        ILoggerFactory loggerFactory,
        Action? onConsentLost = null,
        Action? onConnectivityDegraded = null,
        Action? onConnectivityRecovered = null,
        Action<IPEndPoint>? onPairNominated = null,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? relaySend = null,
        Action<IPEndPoint>? onRelayPairNominated = null,
        Action<IPEndPoint>? onSourceValidated = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<IceMediaAttachment>();
        _onConsentLost = onConsentLost;
        _onConnectivityDegraded = onConnectivityDegraded;
        _onConnectivityRecovered = onConnectivityRecovered;
        _onPairNominated = onPairNominated;
        _onSourceValidated = onSourceValidated;
        _onRelayPairNominated = onRelayPairNominated;
        _relaySend = relaySend;
        _controlling = parameters.IceControlling ? 1 : 0;
        _initialRemote = parameters.RemoteEndPoint;
        _inbound = parameters.IceEnabled
            ? IceInboundStunHandlerFactory.Create(
                parameters.LocalIceUfrag, parameters.LocalIcePwd, parameters.IceControlling, sendRaw, loggerFactory)
            : null;
        _consent = IceMediaConsentSessionFactory.TryCreate(
            parameters, sendRaw, OnConsentLost, loggerFactory, _onConnectivityDegraded, _onConnectivityRecovered);

        // Both roles run connectivity checks; only the controlling role nominates (RFC 8445 §7.2/§8).
        if (_consent is not null)
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
                    SendVia = relaySend,
                });
            }

            var remotes = parameters.RemoteCandidates.Count > 0
                ? parameters.RemoteCandidates
                : [new IceRemoteCandidate(parameters.RemoteEndPoint, HostLocalCandidatePriority)];
            _nominationDriver = new IceNominationDriver(
                localCandidates,
                remotes,
                OnDriverNominated,
                loggerFactory,
                controlling: parameters.IceControlling);
        }

        if (_inbound is not null)
        {
            _inbound.CheckAccepted += OnInboundCheckAccepted;
            _inbound.RoleChanged += OnRoleChanged;
            // The controlled agent adopts the pair the controlling peer nominates (RFC 8445 §7.3.1.5).
            _inbound.PairNominated += Nominate;
        }
    }

    // RFC 8445 §7.3.1.4: learn the peer-reflexive source and queue its check ahead of ordinary work.
    private void OnInboundCheckAccepted(
        IPEndPoint source,
        uint priority,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? replyVia)
    {
        if (_consent is null
            || source.Equals(_initialRemote)
            || (_lastNominated is { } nominated && source.Equals(nominated.Remote)))
            return;
        if (_triggeredSources.Count >= MaxTriggeredSources)
        {
            _logger.LogWarning("ICE triggered-source cap {MaxSources} reached; ignoring {Source}.", MaxTriggeredSources, source);
            return;
        }
        if (!_triggeredSources.TryAdd(source, 0))
            return;

        _logger.LogDebug("ICE triggered check to peer-reflexive source {Source} (RFC 8445 §7.3.1.4).", source);

        // Reported before the pair is nominated, and deliberately: the source has already proved it holds
        // our ICE credential (the check would have been discarded otherwise), while nomination can still be
        // seconds away. A consumer that gates on nomination — the DTLS source filter does — spends that time
        // dropping a handshake the peer has already begun.
        try
        {
            _onSourceValidated?.Invoke(source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in the ICE validated-source handler.");
        }
        var queued = _nominationDriver?.EnqueueTriggered(
            source,
            priority,
            relayed: replyVia is not null,
            ct => replyVia is not null
                ? _consent.SendCheckVia(replyVia, source, useCandidate: false, ct)
                : _consent.SendCheckAsync(source, useCandidate: false, ct)) == true;
        if (!queued)
            _triggeredSources.TryRemove(source, out _);
    }

    private void OnRoleChanged(CalloraVoipSdk.Core.Application.Media.Ice.IceRole role)
    {
        var controlling = role == CalloraVoipSdk.Core.Application.Media.Ice.IceRole.Controlling;
        Volatile.Write(ref _controlling, controlling ? 1 : 0);
        _consent?.SetRole(controlling);
        _nominationDriver?.SetRole(controlling);
    }

    private const int MaxTriggeredSources = 256;
    private const int MaxSeenRemoteAddresses = 256;

    /// <summary>True when either ICE responsibility is active and the attachment should receive STUN.</summary>
    public bool IsActive => _inbound is not null || _consent is not null;

    /// <summary>
    /// Starts connectivity checking. Consent freshness starts only after a pair is nominated. Call after the transport's
    /// receive loop is running so the driver's checks are answered and matched over the shared socket.
    /// </summary>
    public void Start()
    {
        _nominationDriver?.Start();
    }

    /// <summary>
    /// Adds a remote candidate discovered after negotiation (RFC 8838 trickle) to the connectivity-check
    /// list, so it is checked (and possibly nominated) rather than trusted by raw priority. On a controlling
    /// agent the nomination driver picks it up. On a controlled agent — which has no driver and adopts the pair
    /// the controlling peer nominates — it has no driver effect, but when this agent has a relay candidate its
    /// peer IP is proactively permissioned on the relay (RFC 8656 §9) so the offerer's inbound relay check
    /// reaches it rather than being dropped by the TURN server. Its IP is recorded either way so a relay
    /// candidate adopted later can back-fill the permission. No-op on driver/permission when ICE is inactive.
    /// </summary>
    /// <param name="candidate">The trickled remote candidate.</param>
    public void AddRemoteCandidate(IceRemoteCandidate candidate)
    {
        var address = candidate.EndPoint.Address;
        // DoS cap on distinct remote-candidate IPs (RFC 8838 trickle): a peer can trickle unlimited unique IPs,
        // each otherwise growing _seenRemoteAddresses and driving a proactive CreatePermission on the relay — an
        // unbounded wire-boundary state (ENGINEERING_RULES.md §132-133). A known IP always passes; a new IP only
        // with room. Overflow IPs get no persistent entry, no driver pair, and no permission transaction.
        var admitted = _seenRemoteAddresses.ContainsKey(address)
            || (_seenRemoteAddresses.Count < MaxSeenRemoteAddresses && _seenRemoteAddresses.TryAdd(address, 0));
        if (!admitted)
        {
            _logger.LogWarning(
                "ICE seen-remote-address cap {Cap} reached; ignoring trickled candidate IP {Address}.", MaxSeenRemoteAddresses, address);
            return;
        }

        _nominationDriver?.AddCandidate(candidate);

        // Controlling agents install the permission when the send path relays a check, so this only matters for
        // the controlled (answerer) agent, which never sends a relay check itself and must open the inbound path
        // proactively. When the relay candidate is adopted after this candidate is seen, AddRelayLocalCandidate
        // back-fills the permission instead (it reads _seenRemoteAddresses).
        if (Volatile.Read(ref _controlling) == 0 && Volatile.Read(ref _ensurePermission) is { } ensure)
            _ = InstallRemotePermissionAsync(ensure, address);
    }

    // Installs the proactive TURN permission (RFC 8656 §9) for a remote candidate IP on a controlled agent's
    // relay. Fire-and-forget: a check retransmits, and the permission install dedups per IP, so a transient
    // failure is retried by the next candidate/adoption cycle rather than tearing anything down. Logs (never a
    // silent catch) so a persistent failure is diagnosable.
    private async Task InstallRemotePermissionAsync(
        Func<IPAddress, CancellationToken, Task> ensurePermission, IPAddress peerAddress)
    {
        try
        {
            await ensurePermission(peerAddress, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug(
                "Proactively installed a TURN relay permission for offerer candidate {Peer} (RFC 8656 §9).", peerAddress);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex, "Proactive TURN relay permission for offerer candidate {Peer} failed; the inbound relay check may be dropped.",
                peerAddress);
        }
    }

    /// <summary>
    /// Adds the relay ICE local candidate after construction (RFC 8445 §5.1.1.2) — the answerer path, whose
    /// TURN allocation only finishes gathering once this attachment already exists, so its relay path could not
    /// be seeded at construction the way the offerer's is. Stores the relay send path (so a later relay
    /// nomination redirects consent freshness through it) and the permission installer <b>before</b> the
    /// no-driver guard, so both the controlling and the controlled path see them.
    /// <para>
    /// On a controlling agent it then hands the nomination driver a relay local candidate (type preference 0,
    /// below host/srflx) paired against every remote — so a relayed pair is checked and, if no direct pair
    /// works, nominated. On a controlled agent (no driver — it adopts the pair the controlling peer nominates)
    /// there is no driver candidate to add, but the stored send path still lets an inbound relay-received
    /// nomination reply and run consent over the relay, and the permission installer opens the inbound path:
    /// every remote candidate IP already seen (the offer's SDP candidates and early trickle, which arrive before
    /// the answerer's allocation finishes gathering) is proactively permissioned now (RFC 8656 §9), and later
    /// candidates permission themselves through <see cref="AddRemoteCandidate"/>. No-op when consent/ICE is
    /// inactive. Call at most once: a connection is offerer XOR answerer for a given allocation.
    /// </para>
    /// </summary>
    /// <param name="relaySend">
    /// The relay local candidate's TURN-framed send path — <c>(datagram, remoteTarget, ct)</c>, framing the
    /// datagram to the remote through the TURN allocation (Send indication, RFC 8656 §10).
    /// </param>
    /// <param name="ensurePermission">
    /// Installs a TURN permission (RFC 8656 §9) for a peer IP over the relay allocation, deduplicated per IP.
    /// Used on a controlled agent to proactively permission offerer remote-candidate IPs so their inbound relay
    /// checks are not dropped by the TURN server. <see langword="null"/> leaves proactive permissioning off
    /// (the controlling path installs permissions as it sends relayed checks).
    /// </param>
    /// <param name="onNominated">
    /// Invoked with the nominated remote when THIS relay candidate wins the pair (RFC 8445 §8), for a relay that
    /// owns its own transport (a stream relay, ADR-073) to switch that transport onto the relay data path.
    /// <see langword="null"/> (the UDP relay) falls to the session-level relay-nominated callback instead.
    /// </param>
    public void AddRelayLocalCandidate(
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> relaySend,
        Func<IPAddress, CancellationToken, Task>? ensurePermission = null,
        Action<IPEndPoint>? onNominated = null)
    {
        ArgumentNullException.ThrowIfNull(relaySend);
        if (_consent is null)
            return;

        // Store the send path and the permission installer BEFORE the driver guard, so the controlled agent
        // (no driver) still records both: the send path lets an inbound relay-received nomination reply and run
        // consent over the relay, and the installer opens the inbound path for offerer IPs. Volatile pairs with
        // the reads on the driver loop (_relaySend) and the trickle thread (_ensurePermission).
        Volatile.Write(ref _relaySend, relaySend);
        Volatile.Write(ref _ensurePermission, ensurePermission);

        if (Volatile.Read(ref _controlling) == 0 && ensurePermission is not null)
        {
            foreach (var address in _seenRemoteAddresses.Keys)
                _ = InstallRemotePermissionAsync(ensurePermission, address);
        }

        _nominationDriver?.AddLocalCandidate(new IceLocalCandidate
        {
            Type = RelayCandidateType,
            Priority = RelayLocalCandidatePriority,
            Check = (remote, useCandidate, ct) => _consent.SendCheckVia(relaySend, remote, useCandidate, ct),
            SendVia = relaySend,
            OnNominated = onNominated,
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
        // Consent rides the winning candidate's own send path (a relay's Send indication, or null for direct),
        // carried on the candidate so coexisting relays are not conflated by a single shared field.
        if (!NominateInternal(remoteEndPoint, local.SendVia))
            return;

        // The winning candidate switches its OWN transport onto the relay data path when it owns one (a stream
        // relay carries an OnNominated). A UDP relay leaves it null and switches its shared socket in place via
        // the session-level relay-nominated callback; a direct candidate does neither.
        if (local.OnNominated is { } onNominated)
            onNominated(remoteEndPoint);
        else if (local.SendVia is not null)
            FireRelayPairNominated(remoteEndPoint);
    }

    // Adopts the pair the controlling peer nominates via its inbound USE-CANDIDATE check (RFC 8445 §7.3.1.5).
    // A controlled agent sends back over the path the check arrived on (RFC 8445 role-agnostic routing): the
    // direct socket for a host/srflx check, or relay-framed (replyVia) when the check came through this agent's
    // own TURN relay — so a controlled agent's relay candidate works without a nomination driver, and consent
    // freshness follows the same relay path.
    private void Nominate(
        IPEndPoint remoteEndPoint,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? replyVia)
    {
        if (!NominateInternal(remoteEndPoint, replyVia))
            return;

        // A controlled agent that adopted a relay-framed nomination (replyVia set) switches its shared socket in
        // place via the session-level callback. The controlled-agent stream-relay path (a candidate-owned
        // OnNominated) is the known controlled-agent relay gap and is not driven here.
        if (replyVia is not null)
            FireRelayPairNominated(remoteEndPoint);
    }

    // First nomination wins for this ICE generation. Returns true only for the call that won the latch (consent
    // starts only after this selection), so the caller runs relay-nomination side effects exactly once.
    private bool NominateInternal(
        IPEndPoint remoteEndPoint,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? sendVia)
    {
        var nominated = new IceNominatedTarget(remoteEndPoint, sendVia);
        if (Interlocked.CompareExchange(ref _lastNominated, nominated, null) is not null)
            return false;

        _nominationDriver?.AcceptRemoteNomination(remoteEndPoint);
        _consent?.Nominate(remoteEndPoint, sendVia);
        _consent?.Start();
        try
        {
            _onPairNominated?.Invoke(remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ICE pair-nominated handler.");
        }

        return true;
    }

    // Notifies the session that a relay pair was nominated so it can switch the transport onto the relay data
    // path (ChannelBind). Used for the UDP relay (shared-socket in-place switch) and the controlled-agent path;
    // a candidate that owns its transport (a stream relay) drives its own OnNominated instead.
    private void FireRelayPairNominated(IPEndPoint remoteEndPoint)
    {
        try
        {
            _onRelayPairNominated?.Invoke(remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ICE relay-pair-nominated handler.");
        }
    }

    /// <summary>
    /// Routes a STUN datagram demuxed off the media socket by class (RFC 5389 §6): Success/Error
    /// responses answer our consent checks (RFC 7675); everything else is an inbound
    /// connectivity-check request (RFC 8445 §7.3). Matches the transport's
    /// <c>StunPacketReceived(byte[], IPEndPoint)</c> hook signature.
    /// </summary>
    public void OnStunPacketReceived(byte[] datagram, IPEndPoint source)
        => OnStunPacketReceived(datagram, source, replyVia: null);

    /// <summary>
    /// As <see cref="OnStunPacketReceived(byte[], IPEndPoint)"/>, but with the transport-supplied reply path an
    /// inbound connectivity-check response must take (RFC 8445 role-agnostic routing): the bundle transport
    /// passes a relay-framing <paramref name="replyVia"/> when the check arrived through a TURN relay indication,
    /// so the response goes back through the same relay. A consent response (our own check being answered) never
    /// needs it. Direct checks pass <see langword="null"/> — byte-identical to the plain overload.
    /// </summary>
    public void OnStunPacketReceived(
        byte[] datagram,
        IPEndPoint source,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? replyVia)
    {
        if (_consent is not null
            && datagram.Length >= 2
            && (BinaryPrimitives.ReadUInt16BigEndian(datagram) & 0x0110) is 0x0100 or 0x0110)
        {
            _consent.OnStunResponse(datagram);
            return;
        }

        _inbound?.OnStunPacketReceived(datagram, source, replyVia);
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
