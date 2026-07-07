namespace CalloraVoipSdk.Modules;

/// <summary>
/// Contract every SDK module implements to be discoverable through <see cref="ModuleRegistry"/>.
/// Module packages define their own feature interfaces on top of this marker; the SDK core
/// never references concrete module types.
/// </summary>
public interface IVoipClientModule
{
    /// <summary>Stable module identifier for diagnostics and licensing.</summary>
    string ModuleId { get; }

    /// <summary>
    /// Called once when the module is registered on a client. Default implementation is a no-op;
    /// override to capture the owning client and wire against its public API.
    /// </summary>
    void OnAttached(IVoipClient client)
    {
    }
}
