using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Thread-safe registry resolving optional WebRTC facade modules by their feature contract. A focused
/// parallel of the SIP <c>ModuleRegistry</c> (which is bound to <c>IVoipClient</c>), kept separate so the
/// two facades stay decoupled.
/// </summary>
internal sealed class WebRtcModuleRegistry : IWebRtcModuleRegistry
{
    private readonly object _sync = new();
    private readonly IWebRtcClient _owner;
    private readonly List<IWebRtcClientModule> _modules = [];
    // Guarded by _sync. Set by the owner when its teardown begins (#166 P3-13).
    private bool _ownerDisposed;

    internal WebRtcModuleRegistry(IWebRtcClient owner)
    {
        _owner = owner;
    }

    /// <inheritdoc />
    public void Register(IWebRtcClientModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ThrowIfOwnerDisposed();

        module.OnAttached(_owner);

        lock (_sync)
        {
            // Re-checked under the lock: the owner may have started disposing while the attach hook ran, and a
            // module added past that point would stay registered in a dead client (#166 P3-13).
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

    /// <inheritdoc />
    public T Get<T>() where T : class
        => TryGet<T>(out var module) ? module : throw new ModuleFeatureUnavailableException(typeof(T).Name);

    /// <inheritdoc />
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
