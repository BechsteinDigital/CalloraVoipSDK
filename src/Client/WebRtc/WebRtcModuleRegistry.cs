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

    internal WebRtcModuleRegistry(IWebRtcClient owner)
    {
        _owner = owner;
    }

    /// <inheritdoc />
    public void Register(IWebRtcClientModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        module.OnAttached(_owner);

        lock (_sync)
        {
            _modules.Add(module);
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
