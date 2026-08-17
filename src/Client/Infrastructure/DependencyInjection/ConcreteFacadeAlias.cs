using Microsoft.Extensions.DependencyInjection;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Resolves the convenience registration that lets a host ask for a facade's concrete type
/// (<c>VoipClient</c>, <c>WebRtcClient</c>) and get the very same instance the interface registration
/// produced. The alias is only meaningful while the SDK owns the interface registration; a host that brings
/// its own implementation must resolve the interface (#166 P2-8).
/// </summary>
internal static class ConcreteFacadeAlias
{
    /// <summary>
    /// Returns the resolved <typeparamref name="TService"/> as <typeparamref name="TConcrete"/>, or reports
    /// an actionable error. A hard cast here produced an <see cref="InvalidCastException"/> from deep inside
    /// the container whenever a host registered its own implementation after the SDK's registration ran —
    /// unhelpful enough that the facade's documented mockability was effectively unusable.
    /// </summary>
    internal static TConcrete Resolve<TService, TConcrete>(IServiceProvider provider)
        where TService : notnull
        where TConcrete : class, TService
    {
        var resolved = provider.GetRequiredService<TService>();

        return resolved as TConcrete ?? throw new InvalidOperationException(
            $"The registered {typeof(TService).Name} is a '{resolved.GetType().Name}', not the SDK's " +
            $"{typeof(TConcrete).Name}. Resolve {typeof(TService).Name} instead — the concrete " +
            $"{typeof(TConcrete).Name} is only resolvable while the SDK owns that registration.");
    }
}
