using System.Diagnostics.Tracing;
using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// The capture-path fault observer (issue #18, A5 / K3). A faulted fire-and-forget send must have
/// its exception observed — not silently swallowed — so it does not escalate to the finalizer as an
/// unobserved task fault, and the failure must be made visible via the <c>Callora-Voip-Audio</c>
/// EventSource. Verified with an in-memory <see cref="CapturingEventListener"/> (atomic events, no
/// process-global Trace state), asserting on the specific context so a parallel test cannot cross-talk.
/// </summary>
public sealed class AudioTaskFaultObserverTests
{
    [Fact]
    public async Task Reports_the_context_and_error_when_the_send_faults()
    {
        using var listener = new CapturingEventListener();

        var faulted = Task.Run(() => throw new InvalidOperationException("send failed"));
        AudioTaskFaultObserver.Observe(faulted, "unit-context");
        await Assert.ThrowsAnyAsync<Exception>(() => faulted);

        // The OnlyOnFaulted continuation runs once the antecedent faults; each WriteEvent yields exactly
        // one atomic event, so polling for it is deterministic (no header/message fragment split).
        var evt = await WaitForEventAsync(listener, "unit-context");
        Assert.Equal(1, evt.EventId);
        Assert.Contains("unit-context", (string)evt.Payload![0]!, StringComparison.Ordinal);
        Assert.Contains("send failed", (string)evt.Payload![1]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_nothing_when_the_send_completes_successfully()
    {
        using var listener = new CapturingEventListener();

        AudioTaskFaultObserver.Observe(Task.CompletedTask, "ok-context");
        await Task.Delay(50);

        Assert.DoesNotContain(listener.Events, e => (string)e.Payload![0]! == "ok-context");
    }

    [Fact]
    public void Null_task_is_ignored()
    {
        AudioTaskFaultObserver.Observe(null, "unit");
    }

    private static async Task<EventWrittenEventArgs> WaitForEventAsync(CapturingEventListener listener, string context)
    {
        for (var i = 0; i < 50; i++)
        {
            var match = listener.Events.FirstOrDefault(e => (string)e.Payload![0]! == context);
            if (match is not null)
                return match;
            await Task.Delay(10);
        }

        Assert.Fail($"No '{context}' event was captured within the timeout.");
        return null!; // unreachable
    }
}
