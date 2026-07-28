using System.Diagnostics;
using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// The capture-path fault observer (issue #18, A5 / K3). A faulted fire-and-forget send must have
/// its exception observed — not silently swallowed — so it does not escalate to the finalizer as an
/// unobserved task fault, and the failure must be made visible rather than lost.
/// </summary>
public sealed class AudioTaskFaultObserverTests
{
    [Fact]
    public async Task Traces_the_context_and_error_when_the_send_faults()
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var faulted = Task.Run(() => throw new InvalidOperationException("send failed"));
            AudioTaskFaultObserver.Observe(faulted, "unit-context");

            // Wait for the antecedent to fault; the OnlyOnFaulted continuation then traces it.
            await Assert.ThrowsAnyAsync<Exception>(() => faulted);
            for (var i = 0; i < 10 && listener.Output.Count == 0; i++)
                await Task.Delay(20);

            var message = string.Concat(listener.Output);
            Assert.Contains("unit-context", message, StringComparison.Ordinal);
            Assert.Contains("send failed", message, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task Does_not_trace_when_the_send_completes_successfully()
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var completed = Task.CompletedTask;
            AudioTaskFaultObserver.Observe(completed, "ok");
            await completed;
            await Task.Delay(40);

            Assert.Empty(listener.Output);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void Null_task_is_ignored()
    {
        AudioTaskFaultObserver.Observe(null, "unit");
    }
}
