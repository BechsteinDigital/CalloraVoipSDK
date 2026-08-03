namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Thrown when an authenticated packet introduces a new synchronisation source but the context's
/// per-SSRC state map is already at its hard cap (<c>MaxTrackedSsrcs</c>). Authentication proves the
/// peer holds the session key, but not that it is well-behaved: a keyed peer can spray arbitrarily
/// many SSRCs to exhaust memory (RFC 3711 §3.2.1 keeps per-SSRC rollover/index and replay state).
/// The cap fails closed by discarding the <b>new</b> source — it never evicts an already-admitted
/// SSRC's replay window, since that would let previously rejected replays back in (K4 wire-DoS cap).
/// </summary>
internal sealed class SrtpSourceLimitException : Exception
{
    public SrtpSourceLimitException(string message) : base(message) { }
}
