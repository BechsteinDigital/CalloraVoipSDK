using System.Diagnostics;

namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Observes fire-and-forget send tasks started from the audio capture hotpath so their exceptions
/// are surfaced rather than silently swallowed by the finalizer as unobserved-task faults
/// (issue #18, A5; threading contract K3: "fire-and-forget only with fault observation"). A faulted
/// send is written to <see cref="Trace"/> — the audio devices carry no injected logger, and the
/// capture callback must never block, so a non-blocking faulted continuation is the correct seam.
/// Backpressure on a slow sender is a separate, larger concern and is intentionally out of scope
/// here; this type only guarantees the exception is observed and made visible.
/// </summary>
public static class AudioTaskFaultObserver
{
    /// <summary>
    /// Attaches a non-blocking faulted continuation to <paramref name="task"/> that observes and
    /// traces any exception. Safe to call with a completed task; a null task is ignored.
    /// </summary>
    /// <param name="task">The fire-and-forget send task to observe.</param>
    /// <param name="context">A short label identifying the send path, used in the trace message.</param>
    public static void Observe(Task? task, string context)
    {
        if (task is null)
            return;

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                // Reading Exception marks the fault observed, preventing TaskScheduler escalation.
                var error = completed.Exception;
                if (error is not null)
                    Trace.TraceError("Callora audio send failed on {0}: {1}", state, error.GetBaseException());
            },
            context,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
