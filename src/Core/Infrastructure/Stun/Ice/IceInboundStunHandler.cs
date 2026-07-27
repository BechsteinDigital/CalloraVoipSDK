using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Ice;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Drives inbound ICE connectivity checks on a media leg (RFC 8445 §7.3): it subscribes to the
/// STUN datagrams the media transport demuxes off its receive loop, runs each through
/// <see cref="IceInboundCheckProcessor"/>, and sends the resulting Success / 487 response back to
/// the sender on the same socket via the supplied raw-send delegate. Role changes from role-conflict
/// resolution (§7.3.1.1) are tracked in <see cref="Role"/>; a controlled agent's nomination
/// (§7.3.1.5) is surfaced through <see cref="PairNominated"/>.
/// <para>
/// Transport-agnostic by design: the media transport (e.g. an RtpSession) wires its
/// <c>StunPacketReceived</c> hook to <see cref="OnStunPacketReceived"/> and passes its raw-send
/// method as the delegate. The handler owns no socket and no ICE gathering; peer-reflexive learning
/// and outbound triggered checks are layered on separately.
/// </para>
/// </summary>
internal sealed class IceInboundStunHandler
{
    private readonly IceInboundCheckProcessor _processor;
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _sendRaw;
    private readonly string _localUfrag;
    private readonly string _localPassword;
    private readonly ulong _tieBreaker;
    private readonly ILogger<IceInboundStunHandler> _logger;

    // IceRole stored as its int value for lock-free cross-thread access: it is written only on the
    // single transport receive-loop thread (via OnStunPacketReceived) and may be read elsewhere.
    private int _role;

    /// <summary>
    /// Initialises the handler.
    /// </summary>
    /// <param name="processor">The inbound-check processor (decode + auth + ICE decision + response).</param>
    /// <param name="sendRaw">Sends a raw datagram to a destination on the media socket.</param>
    /// <param name="localUfrag">This agent's local ICE username fragment.</param>
    /// <param name="localPassword">This agent's local ICE password (clear text).</param>
    /// <param name="tieBreaker">This agent's 64-bit tie-breaker (RFC 8445 §5.2).</param>
    /// <param name="initialRole">The role this agent starts in.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public IceInboundStunHandler(
        IceInboundCheckProcessor processor,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        string localUfrag,
        string localPassword,
        ulong tieBreaker,
        IceRole initialRole,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(localUfrag);
        ArgumentNullException.ThrowIfNull(localPassword);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _processor = processor;
        _sendRaw = sendRaw;
        _localUfrag = localUfrag;
        _localPassword = localPassword;
        _tieBreaker = tieBreaker;
        _role = (int)initialRole;
        _logger = loggerFactory.CreateLogger<IceInboundStunHandler>();
    }

    /// <summary>This agent's current ICE role, updated on role-conflict resolution (RFC 8445 §7.3.1.1).</summary>
    public IceRole Role => (IceRole)Volatile.Read(ref _role);

    /// <summary>
    /// Raised with the sender's address when an inbound check with USE-CANDIDATE nominates the pair and
    /// this agent is the controlled one (RFC 8445 §7.3.1.5) — the controlled agent adopts that source as
    /// the nominated remote. Fires on the transport receive-loop thread.
    /// </summary>
    public event Action<IPEndPoint>? PairNominated;

    /// <summary>
    /// Raised with the sender's address when an inbound check is authenticated and accepted, so the
    /// transport can trigger a connectivity check back to confirm the pair bidirectionally
    /// (RFC 8445 §7.3.1.4). Fires on the transport receive-loop thread.
    /// </summary>
    public event Action<IPEndPoint>? CheckAccepted;

    /// <summary>
    /// Handles one STUN datagram demuxed off the media transport. Matches the transport's
    /// <c>StunPacketReceived(byte[], IPEndPoint)</c> hook signature. Runs synchronously (does not
    /// block the receive loop); the response is sent fire-and-forget on the same socket.
    /// </summary>
    /// <param name="datagram">The received STUN datagram.</param>
    /// <param name="source">The transport address it arrived from.</param>
    /// <param name="replyVia">
    /// The path the response must take back to <paramref name="source"/> — supplied by the transport when the
    /// check arrived relayed (a TURN Data indication), so the response is framed as a Send indication back
    /// through the same relay (RFC 8445 role-agnostic response routing; the response takes the path the request
    /// came on). <see langword="null"/> for a direct (host/srflx) check — the response uses the raw socket send.
    /// </param>
    public void OnStunPacketReceived(
        byte[] datagram,
        IPEndPoint source,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? replyVia = null)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        ArgumentNullException.ThrowIfNull(source);

        var result = _processor.Process(
            datagram, source, _localUfrag, _localPassword, Role, _tieBreaker);

        if (result.RoleAfter != Role)
        {
            _logger.LogDebug("ICE role changed to {Role} after inbound role conflict from {Source}.", result.RoleAfter, source);
            Volatile.Write(ref _role, (int)result.RoleAfter);
        }

        if (result.NominatePair)
        {
            try { PairNominated?.Invoke(source); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled exception in ICE PairNominated handler."); }
        }

        if (result.Accepted)
        {
            try { CheckAccepted?.Invoke(source); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled exception in ICE CheckAccepted handler."); }
        }

        if (result.ResponseBytes is { } response)
        {
            // Role-agnostic response routing (RFC 8445, mirroring libwebrtc/pjnath/SIPSorcery): the response
            // takes the path the request arrived on — relay-framed via replyVia when the check came through a
            // TURN relay, else the direct socket send. Do not await on the receive loop; send fire-and-forget.
            _ = SendResponseAsync(response, source, replyVia ?? _sendRaw);
        }
    }

    private async Task SendResponseAsync(
        byte[] response,
        IPEndPoint destination,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send)
    {
        try
        {
            await send(response, destination, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ICE connectivity-check response to {Destination}.", destination);
        }
    }
}
