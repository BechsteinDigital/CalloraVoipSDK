namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// A relay allocation keepalive owned by a media session: started once the shared transport is up, and
/// disposed — running its teardown — before that transport is torn down. Kept protocol-agnostic in
/// <c>Infrastructure/Common</c> so the media transport (<c>Infrastructure/Rtp</c>) can drive an allocation's
/// liveness through this seam without depending on the TURN module (the TURN refresh loop implements it).
/// </summary>
internal interface IRelayKeepAlive : IAsyncDisposable
{
    /// <summary>Starts the keepalive loop. Idempotent; a second call, or a call after disposal, is a no-op.</summary>
    void Start();
}
