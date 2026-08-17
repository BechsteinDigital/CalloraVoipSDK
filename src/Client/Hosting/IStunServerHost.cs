using System.Net;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// A hostable STUN server (RFC 5389) for server-reflexive address discovery — the pure-STUN counterpart to
/// <see cref="ITurnServerHost"/>. It binds its socket on construction (so <see cref="LocalEndPoint"/> is known
/// immediately, including after an ephemeral bind), answers Binding requests from <see cref="Start"/>, and
/// releases everything on disposal.
/// </summary>
public interface IStunServerHost : IAsyncDisposable
{
    /// <summary>The endpoint the server is bound to (the actual port after an ephemeral <c>:0</c> bind).</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Starts answering STUN Binding requests. Idempotent — a second call on a started server is a no-op. A
    /// start that fails is not committed, so the host stays startable and a retry runs it again; starting a
    /// disposed host throws rather than pretending to serve (#166 P3-12).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    void Start();
}
