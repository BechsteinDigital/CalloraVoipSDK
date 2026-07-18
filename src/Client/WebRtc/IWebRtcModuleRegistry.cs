using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk.WebRtc;

/// <summary>Thread-safe registry resolving optional WebRTC facade modules by their feature contract.</summary>
public interface IWebRtcModuleRegistry
{
    /// <summary>
    /// Registers one module instance. The <see cref="IWebRtcClientModule.OnAttached"/> hook runs first, so
    /// the module only becomes resolvable after it completed. When multiple registered modules satisfy the
    /// same contract, resolution returns the first registered match.
    /// </summary>
    void Register(IWebRtcClientModule module);

    /// <summary>Resolves the first registered module implementing <typeparamref name="T"/>.</summary>
    /// <exception cref="ModuleFeatureUnavailableException">No registered module implements <typeparamref name="T"/>.</exception>
    T Get<T>() where T : class;

    /// <summary>Attempts to resolve the first registered module implementing <typeparamref name="T"/>.</summary>
    bool TryGet<T>(out T module) where T : class;
}
