using System.Diagnostics.Tracing;

namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// EventSource for the audio pipeline's out-of-band diagnostics (issue #18). A single internal sealed
/// singleton with an <see cref="EventSource.IsEnabled()"/> guard — the .NET-idiomatic channel for library
/// telemetry from static, logger-less hotpaths, mirroring the Azure SDK and the .NET runtime's own
/// EventSources. It is retrievable out of process via <c>dotnet-trace</c> / ETW under the provider name
/// <c>Callora-Voip-Audio</c> without any app-side logging configuration, and — unlike <c>Trace</c> — it
/// carries structured payloads and is not tied to the process-global <c>Trace.Listeners</c> collection.
/// </summary>
[EventSource(Name = "Callora-Voip-Audio")]
internal sealed class AudioEventSource : EventSource
{
    /// <summary>The singleton provider instance (Azure/runtime convention).</summary>
    public static readonly AudioEventSource Log = new();

    private AudioEventSource()
    {
    }

    /// <summary>
    /// A fire-and-forget audio send faulted; the exception has been observed (preventing finalizer
    /// escalation) and is reported here for diagnostics.
    /// </summary>
    /// <param name="context">Short label identifying the send path.</param>
    /// <param name="error">The observed exception, formatted as a string (EventSource payloads are structured, not object graphs).</param>
    [Event(1, Level = EventLevel.Error, Message = "Audio send failed on {0}: {1}")]
    public void SendFailed(string context, string error)
    {
        if (IsEnabled())
            WriteEvent(1, context, error);
    }
}
