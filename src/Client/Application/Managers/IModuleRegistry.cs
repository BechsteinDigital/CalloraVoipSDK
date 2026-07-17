using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk;

/// <summary>
/// Thread-safe registry resolving optional SDK modules by their feature contract.
/// </summary>
public interface IModuleRegistry
{
    /// <summary>
    /// Registers one module instance. The <see cref="IVoipClientModule.OnAttached"/> hook runs
    /// first; the module only becomes resolvable after the hook completed, so consumers never
    /// observe a partially initialized module. When multiple registered modules satisfy the same
    /// contract, resolution returns the first registered match.
    /// </summary>
    void Register(IVoipClientModule module);

    /// <summary>
    /// Resolves the first registered module implementing <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="ModuleFeatureUnavailableException">No registered module implements <typeparamref name="T"/>.</exception>
    T Get<T>() where T : class;

    /// <summary>
    /// Attempts to resolve the first registered module implementing <typeparamref name="T"/>.
    /// </summary>
    bool TryGet<T>(out T module) where T : class;
}
