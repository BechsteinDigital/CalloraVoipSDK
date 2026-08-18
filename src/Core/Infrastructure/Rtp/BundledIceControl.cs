using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Layers ICE (RFC 8445) on a bundled transport's shared 5-tuple (ADR-011 B3-3): a BUNDLE group runs
/// one ICE agent over its single socket, so one consent-freshness loop (RFC 7675) keeps the whole group
/// alive and one inbound handler answers the peer's connectivity checks for every m-line. It wires the
/// reusable <see cref="IceMediaAttachment"/> to the bundle's data path — STUN datagrams demuxed by the
/// <see cref="BundledInboundPipeline"/> feed the attachment, and its checks and responses go out through
/// the transport's targeted send (a STUN response goes to the source of the check, not the default
/// remote). Consent loss/degraded/recovered surface through the supplied callbacks.
/// </summary>
internal sealed class BundledIceControl : IAsyncDisposable
{
    private readonly BundledInboundPipeline _inbound;
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _sendRaw;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action? _onConsentLost;
    private readonly Action? _onConnectivityDegraded;
    private readonly Action? _onConnectivityRecovered;
    private readonly Action<IPEndPoint>? _onPairNominated;
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? _relaySend;
    private readonly Action<IPEndPoint>? _onRelayPairNominated;
    // Plain object rather than System.Threading.Lock: this assembly also targets net8.0, where that type
    // does not exist.
    private readonly object _restartGate = new();

    // The live agent. Mutable because an ICE restart (RFC 8445 §9) replaces it wholesale — new credentials mean
    // a new check list, a new inbound validator and a new consent session, and none of those can be re-keyed in
    // place without leaving half-updated state on the media hot path. Volatile because the receive loop reads it
    // per STUN datagram while a restart publishes a new one.
    private volatile IceMediaAttachment _attachment;

    // A relay local candidate added after construction (the answerer's TURN path). Retained so a restart can
    // re-apply it to the fresh agent — dropping it would silently downgrade a relayed session to direct-only.
    private (Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> Send,
             Func<IPAddress, CancellationToken, Task>? EnsurePermission)? _lateRelay;

    private int _disposed;

    /// <param name="parameters">The ICE view of the shared 5-tuple (credentials, role, nominated remote).</param>
    /// <param name="inbound">The bundle inbound pipeline whose STUN datagrams feed the ICE agent.</param>
    /// <param name="sendRaw">
    /// Targeted raw send over the shared socket — a STUN packet goes to the endpoint the attachment
    /// supplies (typically <see cref="BundledMediaTransport.SendToAsync"/>), not the default remote.
    /// </param>
    /// <param name="onConsentLost">Invoked when consent freshness expires (RFC 7675).</param>
    /// <param name="onConnectivityDegraded">Invoked on a transient consent miss.</param>
    /// <param name="onConnectivityRecovered">Invoked when consent recovers after a degrade.</param>
    /// <param name="onPairNominated">
    /// Invoked with the nominated remote endpoint once ICE connectivity checks select a pair (RFC 8445 §8),
    /// so the transport points its send target at the checked pair (typically
    /// <see cref="BundledMediaTransport.SetRemoteEndPoint"/>).
    /// </param>
    /// <param name="relaySend">
    /// The TURN-framed send path of a relay ICE local candidate (RFC 8656 §10), or <see langword="null"/> when
    /// no relay allocation was gathered. When supplied, a controlling agent adds a relay local candidate so a
    /// relayed pair is checked and, if no direct pair works, nominated. Forwarded to the ICE attachment.
    /// </param>
    /// <param name="onRelayPairNominated">
    /// Invoked (in addition to <paramref name="onPairNominated"/>) when a relay pair is nominated, so the caller
    /// can switch the transport onto the relay data path (RFC 8656 ChannelBind). Forwarded to the ICE attachment.
    /// </param>
    public BundledIceControl(
        IceMediaParameters parameters,
        BundledInboundPipeline inbound,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        ILoggerFactory loggerFactory,
        Action? onConsentLost = null,
        Action? onConnectivityDegraded = null,
        Action? onConnectivityRecovered = null,
        Action<IPEndPoint>? onPairNominated = null,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? relaySend = null,
        Action<IPEndPoint>? onRelayPairNominated = null)
    {
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _sendRaw = sendRaw ?? throw new ArgumentNullException(nameof(sendRaw));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _onConsentLost = onConsentLost;
        _onConnectivityDegraded = onConnectivityDegraded;
        _onConnectivityRecovered = onConnectivityRecovered;
        _onPairNominated = onPairNominated;
        _relaySend = relaySend;
        _onRelayPairNominated = onRelayPairNominated;

        _attachment = Attach(parameters);
    }

    /// <summary>
    /// Replaces the ICE agent with one built from <paramref name="parameters"/> — an ICE restart (RFC 8445 §9,
    /// RFC 8839 §5.4): new credentials on both sides, a fresh check list, and connectivity checks run again over
    /// the same socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything above ICE survives: the transport, its socket, the DTLS association and the SRTP contexts are
    /// untouched, which is what a restart means for a peer — the path is re-selected, the session is not
    /// renegotiated. That is also what a browser does, and why a restart must not be answered by tearing the
    /// peer down.
    /// </para>
    /// <para>
    /// Order matters and is deliberate: the old agent is detached from the inbound STUN feed <em>before</em> the
    /// new one is attached, so no datagram is ever offered to both — a check answered with the old credentials
    /// after the restart would be a protocol error, and two live nomination drivers could redirect the transport
    /// against each other. The gap between detach and attach drops inbound checks for the length of a few
    /// statements; ICE retransmits them (RFC 8445 §14.1), which is the mechanism that makes this safe.
    /// </para>
    /// <para>
    /// A relay local candidate added after construction is re-applied to the new agent, so a relayed session
    /// stays relayed across the restart. Remote candidates are not carried over: they belong to the old check
    /// list, and the caller re-adds the ones the re-offer carries.
    /// </para>
    /// </remarks>
    /// <param name="parameters">The new ICE view of the shared 5-tuple (rotated credentials, role, remote).</param>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The control has been disposed.</exception>
    public async ValueTask RestartIceAsync(IceMediaParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        IceMediaAttachment previous;
        IceMediaAttachment next;
        lock (_restartGate)
        {
            previous = _attachment;
            next = Attach(parameters, detach: previous);
            _attachment = next;
        }

        next.Start();

        // Last: the old agent's consent loop and any in-flight checks stop only here, so the window in which
        // nothing is running is bounded by the statements above rather than by a disposal.
        await previous.DisposeAsync().ConfigureAwait(false);
    }

    // Builds an agent from the current wiring and swaps it onto the inbound STUN feed. Detaching the previous
    // one first is what keeps exactly one agent addressable at any moment.
    private IceMediaAttachment Attach(IceMediaParameters parameters, IceMediaAttachment? detach = null)
    {
        var attachment = new IceMediaAttachment(
            parameters, _sendRaw, _loggerFactory, _onConsentLost, _onConnectivityDegraded,
            _onConnectivityRecovered, _onPairNominated, _relaySend, _onRelayPairNominated);

        if (_lateRelay is { } relay)
            attachment.AddRelayLocalCandidate(relay.Send, relay.EnsurePermission);

        if (detach is not null)
            _inbound.StunPacketReceived -= detach.OnStunPacketReceived;
        _inbound.StunPacketReceived += attachment.OnStunPacketReceived;
        return attachment;
    }

    /// <summary>True when ICE is active on this transport (inbound checks and/or consent freshness).</summary>
    public bool IsActive => _attachment.IsActive;

    /// <summary>Starts the consent-freshness loop (no-op when consent is inactive).</summary>
    public void Start() => _attachment.Start();

    /// <summary>
    /// Adds a trickled remote candidate (RFC 8838) to the connectivity-check list, so the controlling agent
    /// checks and possibly nominates it. No-op on a controlled agent or when ICE is inactive.
    /// </summary>
    /// <param name="candidate">The trickled remote candidate.</param>
    public void AddRemoteCandidate(IceRemoteCandidate candidate) => _attachment.AddRemoteCandidate(candidate);

    /// <summary>
    /// Adds a relay ICE local candidate after construction (RFC 8445 §5.1.1.2) — the answerer path, whose TURN
    /// allocation only finished gathering once the session already existed, so the relay path could not be
    /// supplied to the constructor like the offerer's. A controlling agent then checks the relayed pair alongside
    /// the direct one and, if no direct pair works, nominates it. A controlled agent adds no driver candidate but
    /// records the send path (so an inbound relay-received nomination replies over the relay) and proactively
    /// permissions the offerer's remote-candidate IPs via <paramref name="ensurePermission"/> so their inbound
    /// relay checks are not dropped (RFC 8656 §9). No-op when ICE is inactive. Forwarded to the ICE attachment.
    /// </summary>
    /// <param name="relaySend">The relay local candidate's TURN-framed send path (RFC 8656 §10).</param>
    /// <param name="ensurePermission">
    /// Installs a TURN permission (RFC 8656 §9) for a peer IP over the allocation, used by a controlled agent to
    /// proactively permission offerer remote-candidate IPs. <see langword="null"/> leaves proactive permissioning
    /// off (a controlling agent installs permissions itself as it relays outbound checks).
    /// </param>
    public void AddRelayLocalCandidate(
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> relaySend,
        Func<IPAddress, CancellationToken, Task>? ensurePermission = null)
    {
        // Retained under the same gate that publishes an agent, so a concurrent restart either builds its agent
        // with this relay path or has it applied here — never neither.
        lock (_restartGate)
        {
            _lateRelay = (relaySend, ensurePermission);
            _attachment.AddRelayLocalCandidate(relaySend, ensurePermission);
        }
    }

    /// <summary>Detaches from the inbound STUN feed and disposes the consent session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        IceMediaAttachment live;
        lock (_restartGate)
            live = _attachment;

        _inbound.StunPacketReceived -= live.OnStunPacketReceived;
        await live.DisposeAsync().ConfigureAwait(false);
    }
}
