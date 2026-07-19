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
    /// Starts answering STUN Binding requests. Idempotent — a second call, or a call after disposal, is a no-op.
    /// </summary>
    void Start();
}
