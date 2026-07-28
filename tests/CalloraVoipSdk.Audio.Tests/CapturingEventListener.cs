using System.Diagnostics.Tracing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// In-memory <see cref="EventListener"/> that captures events from the <c>Callora-Voip-Audio</c>
/// EventSource so tests can assert on diagnostics deterministically: one <c>WriteEvent</c> yields exactly
/// one <see cref="OnEventWritten"/> call (no header/message fragment split like <c>TraceListener</c>), and
/// the listener is filtered per source (no process-global <c>Trace.Listeners</c> cross-talk).
/// </summary>
public sealed class CapturingEventListener : EventListener
{
    // A const (not a constructor field) because the base EventListener constructor calls
    // OnEventSourceCreated for already-created sources BEFORE any instance field is assigned.
    private const string SourceName = "Callora-Voip-Audio";

    private readonly object _sync = new();
    private readonly List<EventWrittenEventArgs> _events = new();

    /// <summary>A snapshot of the events captured so far.</summary>
    public IReadOnlyList<EventWrittenEventArgs> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == SourceName)
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        lock (_sync)
        {
            _events.Add(eventData);
        }
    }
}
