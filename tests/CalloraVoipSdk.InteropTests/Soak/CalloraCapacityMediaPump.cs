using System.Collections.Concurrent;
using System.Diagnostics;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed class CalloraCapacityMediaPump : IAsyncDisposable
{
    private static readonly byte[] PcmuSilence = Enumerable.Repeat((byte)0xff, 160).ToArray();

    private readonly object _gate = new();
    private readonly int _workerCount;
    private readonly CalloraCapacityCallTracker[] _callTrackers;
    private readonly ConcurrentQueue<string> _errors;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;

    private CalloraCapacityMediaTarget[] _targets = [];

    public CalloraCapacityMediaPump(
        int workerCount,
        CalloraCapacityCallTracker[] callTrackers,
        ConcurrentQueue<string> errors)
    {
        _workerCount = workerCount;
        _callTrackers = callTrackers;
        _errors = errors;
        _workers = Enumerable.Range(0, workerCount)
            .Select(RunWorkerAsync)
            .ToArray();
    }

    public void AddRange(IEnumerable<(int Index, ICall Call, IMediaSender Sender)> additions)
    {
        var targets = additions
            .Select(item => new CalloraCapacityMediaTarget(
                item.Index,
                item.Sender,
                _callTrackers[item.Index].Outbound,
                new MediaFrame(
                    PcmuSilence,
                    item.Call.MediaParameters?.PayloadType ?? 0,
                    160)))
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var current = Volatile.Read(ref _targets);
            var expanded = new CalloraCapacityMediaTarget[current.Length + targets.Length];
            current.CopyTo(expanded, 0);
            targets.CopyTo(expanded, current.Length);
            Volatile.Write(ref _targets, expanded);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task RunWorkerAsync(int worker)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                var targets = Volatile.Read(ref _targets);
                for (var index = worker; index < targets.Length; index += _workerCount)
                {
                    var target = targets[index];
                    await target.Sender.SendAsync(target.Frame, _cts.Token).ConfigureAwait(false);
                    target.Tracker.Observe(Stopwatch.GetTimestamp());
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected benchmark teardown.
        }
        catch (Exception ex)
        {
            _errors.Enqueue(
                $"Media worker #{worker}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal sealed record CalloraCapacityMediaTarget(
    int Index,
    IMediaSender Sender,
    CalloraCapacityDirectionTracker Tracker,
    MediaFrame Frame);
