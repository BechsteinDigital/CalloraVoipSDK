using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk;

/// <summary>
/// Thread-safe registry resolving optional SDK modules by their feature contract.
/// Modules are contributed by separate packages either through dependency injection
/// (register <see cref="IVoipClientModule"/> services before <c>AddCalloraVoip</c>) or
/// programmatically via <see cref="Register"/>.
/// </summary>
public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly object _sync = new();
    private readonly IVoipClient _owner;
    private readonly List<IVoipClientModule> _modules = [];
    // Guarded by _sync. Set by the owner when its teardown begins (#166 P3-13).
    private bool _ownerDisposed;

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
    /// <remarks>
    /// Registration closes when the owning client is disposed. The attach hook hands the module the client, and
    /// a module attaching to a disposed one would wire itself to torn-down transport, lines and media and then
    /// stay registered in a dead owner for its whole lifetime — so a late registration is refused rather than
    /// half-performed (#166 P3-13). Resolution keeps working, so a teardown path can still reach its modules.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The owning client has been disposed.</exception>
    public void Register(IVoipClientModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ThrowIfOwnerDisposed();

        module.OnAttached(_owner);

        lock (_sync)
        {
            // Re-checked under the lock: the owner may have started disposing while the attach hook ran, and a
            // module added past that point would never be released.
            ObjectDisposedException.ThrowIf(_ownerDisposed, _owner);
            _modules.Add(module);
        }
    }

    /// <summary>
    /// Closes registration. Called by the owning client when its teardown begins; already registered modules
    /// stay resolvable for the remainder of that teardown.
    /// </summary>
    internal void MarkOwnerDisposed()
    {
        lock (_sync)
        {
            _ownerDisposed = true;
        }
    }

    private void ThrowIfOwnerDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_ownerDisposed, _owner);
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
