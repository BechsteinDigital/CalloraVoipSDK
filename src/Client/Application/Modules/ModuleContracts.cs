namespace CalloraVoipSdk.Modules;

/// <summary>
/// Thrown by <see cref="ModuleRegistry.Get{T}"/> when no registered module implements
/// the requested feature contract.
/// </summary>
public sealed class ModuleFeatureUnavailableException : InvalidOperationException
{
    /// <summary>
    /// Creates the exception for the requested module or feature contract.
    /// </summary>
    public ModuleFeatureUnavailableException(string moduleId)
        : base($"Module '{moduleId}' is not available in this SDK runtime. Register the module package before use.")
    {
    }
}
