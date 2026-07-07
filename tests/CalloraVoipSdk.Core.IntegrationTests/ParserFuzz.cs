using System.Diagnostics;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Shared utilities for the network-parser robustness (fuzz) tests.
/// <para>
/// Every parser exercised here consumes <b>untrusted</b> wire input. The invariant under test is:
/// for any byte input a parser must terminate in bounded time and memory and must either return a
/// clean result, an empty/<c>false</c>/<c>null</c> result, or throw at most one expected exception
/// type that its caller handles — never an unexpected exception that could escape a receive loop
/// and never a non-terminating loop (hang).
/// </para>
/// </summary>
internal static class ParserFuzz
{
    /// <summary>
    /// Fixed seeds so the random-input coverage is deterministic and reproducible.
    /// </summary>
    public static readonly int[] Seeds = [1, 7, 42, 1337, 90210];

    /// <summary>
    /// Runs a full fuzz batch on a worker thread and fails deterministically if it does not
    /// finish within <paramref name="timeoutMs"/> — a non-terminating parse surfaces as a test
    /// failure instead of freezing the run. Exceptions thrown inside the body are re-thrown
    /// unwrapped so assertion failures propagate normally.
    /// </summary>
    public static void CompletesWithin(int timeoutMs, Action fuzzBody)
    {
        var task = Task.Run(fuzzBody);
        var finished = task.Wait(timeoutMs);
        Assert.True(
            finished,
            $"Fuzz batch did not complete within {timeoutMs} ms — possible non-terminating parse (hang).");
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Invokes <paramref name="parse"/> and asserts it either returns normally or throws only a
    /// type assignable to one of <paramref name="allowed"/>. Any other exception is a robustness
    /// defect and fails the test. When <paramref name="allowed"/> is empty the parser must never
    /// throw for any input.
    /// </summary>
    public static void Guard(Action parse, params Type[] allowed)
    {
        try
        {
            parse();
        }
        catch (Exception ex)
        {
            var accepted = false;
            foreach (var t in allowed)
            {
                if (t.IsInstanceOfType(ex))
                {
                    accepted = true;
                    break;
                }
            }

            Assert.True(accepted, $"Parser threw unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Produces a deterministic random byte array of the requested length.</summary>
    public static byte[] RandomBytes(Random rng, int length)
    {
        var buffer = new byte[length];
        rng.NextBytes(buffer);
        return buffer;
    }

    /// <summary>Asserts a single parse call stays well under a one-second wall-clock budget.</summary>
    public static void WithinCallBudget(Action parse)
    {
        var sw = Stopwatch.StartNew();
        parse();
        sw.Stop();
        Assert.True(
            sw.ElapsedMilliseconds < 1_000,
            $"Single parse took {sw.ElapsedMilliseconds} ms (>= 1000 ms budget).");
    }
}
