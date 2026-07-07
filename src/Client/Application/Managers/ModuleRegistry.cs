using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk;

/// <summary>
/// Thread-safe registry resolving optional SDK modules by their feature contract.
/// Modules are contributed by separate packages either through dependency injection
/// (register <see cref="IVoipClientModule"/> services before <c>AddCallora</c>) or
/// programmatically via <see cref="Register"/>.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly object _sync = new();
    private readonly IVoipClient _owner;
    private readonly List<IVoipClientModule> _modules = [];

    internal ModuleRegistry(IVoipClient owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Registers one module instance. The <see cref="IVoipClientModule.OnAttached"/> hook runs
    /// first; the module only becomes resolvable after the hook completed, so consumers never
    /// observe a partially initialized module. When multiple registered modules satisfy the same
    /// contract, resolution returns the first registered match.
    /// </summary>
    public void Register(IVoipClientModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        module.OnAttached(_owner);

        lock (_sync)
        {
            _modules.Add(module);
        }
    }

    /// <summary>
    /// Resolves the first registered module implementing <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="ModuleFeatureUnavailableException">No registered module implements <typeparamref name="T"/>.</exception>
    public T Get<T>() where T : class
    {
        return TryGet<T>(out var module)
            ? module
            : throw new ModuleFeatureUnavailableException(typeof(T).Name);
    }

    /// <summary>
    /// Attempts to resolve the first registered module implementing <typeparamref name="T"/>.
    /// </summary>
    public bool TryGet<T>(out T module) where T : class
    {
        lock (_sync)
        {
            foreach (var candidate in _modules)
            {
                if (candidate is T match)
                {
                    module = match;
                    return true;
                }
            }
        }

        module = null!;
        return false;
    }
}
