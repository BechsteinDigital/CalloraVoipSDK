using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// Symmetric-RTP (comedia) latch: remembers the real source of the peer's media so outbound RTP follows the
/// NAT-translated path the peer actually uses, instead of the SDP-advertised address. Hardened against the
/// media-hijack class of CVE-2017-14099, which re-pointed established media at an attacker's address: the
/// caller only offers a source that has already passed SSRC/sequence validation (a duplicate, sequence-jump, or
/// collision datagram never qualifies), and a <em>change</em> of source away from an established latch is
/// honoured only on a keyed (SRTP/DTLS-authenticated) call — where a new source can only be the peer behind a
/// NAT rebind. On a plaintext call there is no such proof, so the latch locks onto the first validated source
/// and refuses to move: an unauthenticated flood must not be able to redirect our outbound media.
/// <para>
/// <see cref="Consider"/> runs on the single RTP receive-loop thread; <see cref="Target"/> is read on the send
/// path. The latched endpoint is published via <see cref="Volatile"/> so the send path sees it without a lock.
/// </para>
/// </summary>
internal sealed class SymmetricRtpLatch
{
    private readonly ILogger _logger;

    // Published to the send thread via Volatile; the refused-source note is receive-loop-thread only.
    private IPEndPoint? _latched;
    private IPEndPoint? _lastRefused;

    /// <summary>Creates an unlatched latch logging to <paramref name="logger"/>.</summary>
    public SymmetricRtpLatch(ILogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The address outbound media should target: the latched source once one is established, otherwise
    /// <paramref name="fallback"/> (the SDP-advertised remote).
    /// </summary>
    public IPEndPoint Target(IPEndPoint fallback) => Volatile.Read(ref _latched) ?? fallback;

    /// <summary>
    /// Considers a validated inbound packet's <paramref name="source"/> for the latch. The caller MUST only pass
    /// a source that passed SSRC/sequence validation. The first source always latches; a subsequent change of
    /// source re-latches only when <paramref name="authenticated"/> (a keyed call), and is otherwise refused so
    /// an unauthenticated flood cannot hijack the outbound path.
    /// </summary>
    public void Consider(IPEndPoint source, bool authenticated)
    {
        var current = Volatile.Read(ref _latched);
        if (source.Equals(current))
            return;

        if (current is null || authenticated)
        {
            Volatile.Write(ref _latched, source);
            _logger.LogDebug("RTP symmetric latch: sending media to observed source {Source}.", source);
            return;
        }

        // Refused (plaintext lock). Log once per distinct new source so a flood does not spam the log.
        if (!source.Equals(_lastRefused))
        {
            _lastRefused = source;
            _logger.LogWarning(
                "Ignoring RTP from a new source {Source}: media stays latched to {Latched} (plaintext lock — " +
                "possible spoof or an un-renegotiated NAT rebind).", source, current);
        }
    }
}
