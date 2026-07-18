namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Contract every WebRTC facade module implements to be discoverable through
/// <see cref="IWebRtcModuleRegistry"/> — the L3 plugin seam, mirroring the SIP <c>IVoipClientModule</c>.
/// Module packages define their own feature interfaces on top of this marker; the facade never references
/// concrete module types.
/// </summary>
public interface IWebRtcClientModule
{
    /// <summary>Stable module identifier for diagnostics and licensing.</summary>
    string ModuleId { get; }

    /// <summary>
    /// Called once when the module is registered on a client. Default is a no-op; override to capture the
    /// owning client and wire against its public API.
    /// </summary>
    void OnAttached(IWebRtcClient client)
    {
    }
}
