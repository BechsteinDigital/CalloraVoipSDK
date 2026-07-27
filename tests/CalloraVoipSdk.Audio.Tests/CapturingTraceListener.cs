using System.Diagnostics;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// A trace listener that records emitted messages so tests can assert on
/// <see cref="Trace"/> output. Thread-safe: the fault observer traces from a task continuation.
/// </summary>
public sealed class CapturingTraceListener : TraceListener
{
    private readonly object _sync = new();
    private readonly List<string> _output = new();

    /// <summary>A snapshot of the messages captured so far.</summary>
    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_sync)
            {
                return _output.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string? message)
    {
        if (message is null)
            return;

        lock (_sync)
        {
            _output.Add(message);
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? message) => Write(message);
}
