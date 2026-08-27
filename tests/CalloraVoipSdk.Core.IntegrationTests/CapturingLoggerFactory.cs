using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// An <see cref="ILoggerFactory"/> that hands every category the same <see cref="CapturingLogger"/>, so a
/// test can construct a component under test and then assert on what it logged.
/// </summary>
internal sealed class CapturingLoggerFactory(CapturingLogger logger) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => logger;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}
