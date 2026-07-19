using System.Net;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// A hostable TURN relay server (RFC 8656) — the server-side counterpart to the client facades
/// (<c>VoipClient</c>, <c>WebRtcClient</c>). It binds its socket on construction (so <see cref="LocalEndPoint"/>
/// is known immediately, including after an ephemeral bind), starts serving on <see cref="Start"/>, and stops
/// and releases everything on disposal. It serves the TURN methods (Allocate / Refresh / CreatePermission /
/// ChannelBind / Send / Data, plus the TCP extension); it does <em>not</em> answer bare STUN Binding requests —
/// for server-reflexive discovery run an <see cref="IStunServerHost"/> alongside it.
/// </summary>
public interface ITurnServerHost : IAsyncDisposable
{
    /// <summary>The endpoint the server is bound to (the actual port after an ephemeral <c>:0</c> bind).</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Starts the server's listen loop. Idempotent — a second call, or a call after disposal, is a no-op.
    /// </summary>
    void Start();
}
