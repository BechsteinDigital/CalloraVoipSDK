using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Microsoft.Extensions.Logging;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed record CalloraCapacityMachine(
    string Os,
    string ProcessArchitecture,
    string Framework,
    int LogicalProcessors,
    bool ServerGc,
    long GcAvailableMemoryBytes);

internal sealed record CalloraCapacityTrial(
    int TargetCalls,
    int Repetition,
    bool Stable,
    int ConnectedCalls,
    int FullDuplexMediaCalls,
    int ApplicationQualityPassedCalls,
    int RtpEvidenceCompleteCalls,
    int QualityPassedCalls,
    int AsteriskChannelsDuringMedia,
    double SetupP50Milliseconds,
    double SetupP95Milliseconds,
    double SetupP99Milliseconds,
    double ObservedMediaWindowSeconds,
    double MeasurementWakeDelayMilliseconds,
    double MediaCpuPercentOfMachine,
    double AsteriskCpuPercentOfMachine,
    long AsteriskMemoryBytes,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long ManagedAllocatedBytesDuringMedia,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long OutboundApplicationFrames,
    long InboundApplicationFrames,
    long OutboundRtpPackets,
    long InboundRtpPackets,
    IReadOnlyList<CalloraCapacityCallQuality> CallQuality,
    IReadOnlyList<string> Errors);

internal sealed record CalloraCapacityReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    CalloraCapacityMachine Machine,
    CalloraCapacityProfile Profile,
    int LargestValidatedCallCount,
    int? FirstUnstableTarget,
    bool CeilingReached,
    bool RunCompleted,
    bool CleanTeardown,
    double CleanupSeconds,
    int AsteriskChannelsAfterCleanup,
    IReadOnlyList<CalloraCapacityTrial> Trials,
    IReadOnlyList<string> Errors)
{
    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

internal sealed class CalloraCapacityBenchmark
{
    private readonly AsteriskContainer _asterisk;
    private readonly CalloraCapacityProfile _profile;

    public CalloraCapacityBenchmark(
        AsteriskContainer asterisk,
        CalloraCapacityProfile profile)
    {
        _asterisk = asterisk;
        _profile = profile;
    }

    public async Task<CalloraCapacityReport> RunAsync()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var machine = CaptureMachine();
        var trials = new List<CalloraCapacityTrial>();
        var largestValidated = 0;
        int? firstUnstable = null;
        var sdkWarnings = new ConcurrentQueue<string>();
        using var loggerFactory = new CalloraCapacityWarningLoggerFactory(sdkWarnings);
        var client = NewClient(loggerFactory);
        using var process = Process.GetCurrentProcess();
        var calls = new List<ICall>(_profile.SdkCallLimit);
        var senders = new List<IMediaSender>(_profile.SdkCallLimit);
        var receivers = new List<IMediaReceiver>(_profile.SdkCallLimit);
        var callTrackers = Enumerable.Range(0, _profile.SdkCallLimit)
            .Select(_ => new CalloraCapacityCallTracker(TimeSpan.FromMilliseconds(20)))
            .ToArray();
        var benchmarkErrors = new ConcurrentQueue<string>();
        var mediaErrors = new ConcurrentQueue<string>();
        var cleanupErrors = new ConcurrentQueue<string>();
        var mediaPump = new CalloraCapacityMediaPump(
            _profile.MediaWorkers,
            callTrackers,
            mediaErrors);
        var channelsAfterCleanup = -1;
        var cleanupSeconds = -1d;

        try
        {
            var line = await RegisterAsync(client).ConfigureAwait(false);
            foreach (var level in _profile.Levels)
            {
                var setupLatencies = new ConcurrentBag<double>();
                var levelErrors = new ConcurrentQueue<string>();
                var connectedExistingCalls = calls.Count(call => call.State == CallState.Connected);
                if (connectedExistingCalls != calls.Count)
                {
                    levelErrors.Enqueue(
                        $"{calls.Count - connectedExistingCalls}/{calls.Count} calls from the prior " +
                        "stage were no longer connected; the cumulative ramp cannot continue.");
                    firstUnstable ??= level;
                    trials.Add(CreateSetupFailureTrial(
                        level,
                        connectedExistingCalls,
                        setupLatencies,
                        levelErrors));
                    CopyErrors(levelErrors, benchmarkErrors);
                    break;
                }

                await AddCallsAsync(
                        client,
                        line,
                        level,
                        calls,
                        senders,
                        receivers,
                        mediaPump,
                        callTrackers,
                        setupLatencies,
                        levelErrors)
                    .ConfigureAwait(false);

                if (calls.Count != level)
                {
                    firstUnstable ??= level;
                    trials.Add(CreateSetupFailureTrial(
                        level,
                        calls.Count,
                        setupLatencies,
                        levelErrors));
                    CopyErrors(levelErrors, benchmarkErrors);
                    await WriteReportAsync(CreateReport(
                            startedAt,
                            machine,
                            largestValidated,
                            firstUnstable,
                            runCompleted: false,
                            cleanTeardown: false,
                            cleanupSeconds: -1,
                            channelsAfterCleanup: -1,
                            trials,
                            benchmarkErrors))
                        .ConfigureAwait(false);
                    break;
                }

                var levelStable = true;
                for (var repetition = 1; repetition <= _profile.Repetitions; repetition++)
                {
                    var trial = await MeasureLevelAsync(
                            level,
                            repetition,
                            calls,
                            callTrackers,
                            setupLatencies,
                            process,
                            levelErrors,
                            mediaErrors,
                            sdkWarnings)
                        .ConfigureAwait(false);
                    trials.Add(trial);
                    if (!trial.Stable)
                    {
                        levelStable = false;
                        firstUnstable ??= level;
                        break;
                    }
                }

                if (!levelStable)
                {
                    CopyErrors(levelErrors, benchmarkErrors);
                    await WriteReportAsync(CreateReport(
                            startedAt,
                            machine,
                            largestValidated,
                            firstUnstable,
                            runCompleted: false,
                            cleanTeardown: false,
                            cleanupSeconds: -1,
                            channelsAfterCleanup: -1,
                            trials,
                            benchmarkErrors))
                        .ConfigureAwait(false);
                    if (!_profile.ContinueAfterQualityFailure)
                    {
                        break;
                    }

                    continue;
                }

                largestValidated = level;
                await WriteReportAsync(CreateReport(
                        startedAt,
                        machine,
                        largestValidated,
                        firstUnstable,
                        runCompleted: false,
                        cleanTeardown: false,
                        cleanupSeconds: -1,
                        channelsAfterCleanup: -1,
                        trials,
                        benchmarkErrors))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            benchmarkErrors.Enqueue($"Benchmark infrastructure: {ex.GetType().Name}: {ex.Message}");
            firstUnstable ??= _profile.Levels.FirstOrDefault();
        }
        finally
        {
            var cleanupStopwatch = Stopwatch.StartNew();
            await mediaPump.DisposeAsync().ConfigureAwait(false);
            foreach (var sender in senders)
            {
                sender.Dispose();
            }
            foreach (var receiver in receivers)
            {
                receiver.Dispose();
            }

            await HangupAllAsync(calls, cleanupErrors).ConfigureAwait(false);
            channelsAfterCleanup = await WaitForNoChannelsAsync(cleanupErrors).ConfigureAwait(false);
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors.Enqueue(
                    $"VoipClient cleanup: {ex.GetType().Name}: {ex.Message}");
            }
            cleanupStopwatch.Stop();
            cleanupSeconds = cleanupStopwatch.Elapsed.TotalSeconds;
        }

        DrainErrors(sdkWarnings, benchmarkErrors);
        CopyErrors(cleanupErrors, benchmarkErrors);
        var cleanTeardown = channelsAfterCleanup == 0 && cleanupErrors.IsEmpty;
        var report = CreateReport(
            startedAt,
            machine,
            largestValidated,
            firstUnstable,
            runCompleted: true,
            cleanTeardown,
            cleanupSeconds,
            channelsAfterCleanup,
            trials,
            benchmarkErrors);
        await WriteReportAsync(report).ConfigureAwait(false);
        return report;
    }

    private async Task AddCallsAsync(
        VoipClient client,
        IPhoneLine line,
        int targetCalls,
        List<ICall> calls,
        List<IMediaSender> senders,
        List<IMediaReceiver> receivers,
        CalloraCapacityMediaPump mediaPump,
        CalloraCapacityCallTracker[] callTrackers,
        ConcurrentBag<double> setupLatencies,
        ConcurrentQueue<string> errors)
    {
        var firstIndex = calls.Count;
        var remaining = targetCalls - firstIndex;
        foreach (var batch in Enumerable.Range(firstIndex, remaining).Chunk(_profile.SetupParallelism))
        {
            var results = await Task.WhenAll(
                    batch.Select(index => DialAsync(client, line, index)))
                .ConfigureAwait(false);
            var mediaAdditions = new List<(int Index, ICall Call, IMediaSender Sender)>();
            foreach (var result in results)
            {
                setupLatencies.Add(result.Elapsed.TotalMilliseconds);
                if (result.Call is null)
                {
                    errors.Enqueue($"Call #{result.Index}: {result.Error}");
                    continue;
                }

                calls.Add(result.Call);
                var sender = client.Media.CreateSender();
                sender.AttachToCall(result.Call);
                senders.Add(sender);
                mediaAdditions.Add((result.Index, result.Call, sender));
                var receiver = client.Media.CreateReceiver();
                var callIndex = result.Index;
                receiver.FrameReceived += (_, _) =>
                    callTrackers[callIndex].Inbound.Observe(Stopwatch.GetTimestamp());
                receiver.AttachToCall(result.Call);
                receivers.Add(receiver);
                result.Call.StateChanged += (_, args) =>
                    callTrackers[callIndex].ObserveState(
                        args.NewState,
                        Stopwatch.GetTimestamp());
            }
            mediaPump.AddRange(mediaAdditions);

            if (results.Any(result => result.Call is null))
            {
                break;
            }
        }
    }

    private async Task<CalloraCapacityTrial> MeasureLevelAsync(
        int targetCalls,
        int repetition,
        IReadOnlyList<ICall> calls,
        CalloraCapacityCallTracker[] callTrackers,
        ConcurrentBag<double> setupLatencies,
        Process process,
        ConcurrentQueue<string> errors,
        ConcurrentQueue<string> mediaErrors,
        ConcurrentQueue<string> sdkWarnings)
    {
        var errorCountBefore = errors.Count;
        await Task.Delay(_profile.SettleWindow).ConfigureAwait(false);
        DrainErrors(sdkWarnings, errors);
        var rtpBefore = await WaitForRtpSnapshotsAsync(calls, minimumCapturedAtUtc: null)
            .ConfigureAwait(false);
        if (rtpBefore.Count(snapshot => snapshot.HasValue) != targetCalls)
        {
            errors.Enqueue(
                $"{targetCalls - rtpBefore.Count(snapshot => snapshot.HasValue)}/{targetCalls} calls " +
                "had no RTP baseline after settling.");
        }

        var lead = TimeSpan.FromMilliseconds(500);
        var preparedAtTicks = Stopwatch.GetTimestamp();
        var windowStartTicks = preparedAtTicks + ToStopwatchTicks(lead);
        var windowEndTicks = windowStartTicks + ToStopwatchTicks(_profile.MediaWindow);
        var windowStartAtUtc = DateTimeOffset.UtcNow + lead;
        for (var index = 0; index < targetCalls; index++)
        {
            callTrackers[index].Arm(
                windowStartTicks,
                windowEndTicks,
                windowStartAtUtc,
                calls[index].State);
        }

        await DelayUntilAsync(windowStartTicks).ConfigureAwait(false);
        var asteriskBefore = await ReadAsteriskResourcesAsync().ConfigureAwait(false);
        var resourceWindow = Stopwatch.StartNew();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        await DelayUntilAsync(windowEndTicks).ConfigureAwait(false);

        var wakeDelay = Stopwatch.GetElapsedTime(windowEndTicks, Stopwatch.GetTimestamp());
        var cpuAfter = process.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        var asteriskAfter = await ReadAsteriskResourcesAsync().ConfigureAwait(false);
        resourceWindow.Stop();

        var channelsDuringMedia = await CountActiveChannelsAsync().ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        var rtpAfter = await WaitForRtpSnapshotsAsync(
                calls,
                windowStartAtUtc + _profile.MediaWindow)
            .ConfigureAwait(false);
        var staleRtpSnapshots = rtpAfter.Count(snapshot =>
            snapshot is null ||
            snapshot.Value.CapturedAtUtc < windowStartAtUtc + _profile.MediaWindow);
        if (staleRtpSnapshots != 0)
        {
            errors.Enqueue(
                $"{staleRtpSnapshots}/{targetCalls} calls had no RTP snapshot captured after the media window.");
        }

        var callQuality = new CalloraCapacityCallQuality[targetCalls];
        for (var index = 0; index < targetCalls; index++)
        {
            callQuality[index] = CalloraCapacityQualityMeasurement.Create(
                index,
                calls[index],
                callTrackers[index].ConnectedThroughoutWindow,
                callTrackers[index].Outbound.Snapshot(),
                callTrackers[index].Inbound.Snapshot(),
                rtpBefore[index],
                rtpAfter[index],
                calls[index].QualitySnapshot,
                _profile.MediaWindow,
                _profile.QualityGate);
        }

        var connectedCalls = calls.Take(targetCalls).Count(call => call.State == CallState.Connected);
        var fullDuplexCalls = callQuality.Count(result =>
            result.Outbound.ApplicationFrames > 0 &&
            result.Inbound.ApplicationFrames > 0);
        var applicationQualityPassedCalls = callQuality.Count(result => result.ApplicationPassed);
        var rtpEvidenceCompleteCalls = callQuality.Count(result => result.RtpEvidenceComplete);
        var qualityPassedCalls = callQuality.Count(result => result.Passed);
        if (qualityPassedCalls != targetCalls)
        {
            errors.Enqueue(
                $"{targetCalls - qualityPassedCalls}/{targetCalls} calls failed the per-direction quality gate.");
        }
        if (channelsDuringMedia != targetCalls)
        {
            errors.Enqueue(
                $"Asterisk reported {channelsDuringMedia} active channels; expected {targetCalls}.");
        }
        if (wakeDelay > TimeSpan.FromSeconds(2))
        {
            errors.Enqueue(
                $"Measurement wake-up was delayed by {wakeDelay.TotalMilliseconds:F3} ms; " +
                "the 2s scheduler tolerance was exceeded.");
        }
        DrainErrors(mediaErrors, errors);
        DrainErrors(sdkWarnings, errors);

        process.Refresh();
        var levelErrors = errors.Skip(errorCountBefore).Take(20).ToArray();
        var sortedLatencies = setupLatencies.Order().ToArray();
        return new CalloraCapacityTrial(
            targetCalls,
            repetition,
            qualityPassedCalls == targetCalls &&
            channelsDuringMedia == targetCalls &&
            levelErrors.Length == 0,
            connectedCalls,
            fullDuplexCalls,
            applicationQualityPassedCalls,
            rtpEvidenceCompleteCalls,
            qualityPassedCalls,
            channelsDuringMedia,
            Percentile(sortedLatencies, 0.50),
            Percentile(sortedLatencies, 0.95),
            Percentile(sortedLatencies, 0.99),
            _profile.MediaWindow.TotalSeconds,
            wakeDelay.TotalMilliseconds,
            NormalizeCpu(cpuAfter - cpuBefore, resourceWindow.Elapsed),
            NormalizeCpu(
                asteriskAfter.CpuUsageMicroseconds - asteriskBefore.CpuUsageMicroseconds,
                resourceWindow.Elapsed),
            asteriskAfter.MemoryBytes,
            process.WorkingSet64,
            process.PeakWorkingSet64,
            allocatedAfter - allocatedBefore,
            gen0After - gen0Before,
            gen1After - gen1Before,
            gen2After - gen2Before,
            callQuality.Sum(result => result.Outbound.ApplicationFrames),
            callQuality.Sum(result => result.Inbound.ApplicationFrames),
            callQuality.Sum(result => (long)(result.Outbound.RtpPackets ?? 0)),
            callQuality.Sum(result => (long)(result.Inbound.RtpPackets ?? 0)),
            callQuality,
            levelErrors);
    }

    private static CalloraCapacityTrial CreateSetupFailureTrial(
        int targetCalls,
        int connectedCalls,
        ConcurrentBag<double> setupLatencies,
        ConcurrentQueue<string> errors)
    {
        var sortedLatencies = setupLatencies.Order().ToArray();
        return new CalloraCapacityTrial(
            TargetCalls: targetCalls,
            Repetition: 1,
            Stable: false,
            ConnectedCalls: connectedCalls,
            FullDuplexMediaCalls: 0,
            ApplicationQualityPassedCalls: 0,
            RtpEvidenceCompleteCalls: 0,
            QualityPassedCalls: 0,
            AsteriskChannelsDuringMedia: -1,
            SetupP50Milliseconds: Percentile(sortedLatencies, 0.50),
            SetupP95Milliseconds: Percentile(sortedLatencies, 0.95),
            SetupP99Milliseconds: Percentile(sortedLatencies, 0.99),
            ObservedMediaWindowSeconds: 0,
            MeasurementWakeDelayMilliseconds: 0,
            MediaCpuPercentOfMachine: 0,
            AsteriskCpuPercentOfMachine: 0,
            AsteriskMemoryBytes: 0,
            WorkingSetBytes: 0,
            PeakWorkingSetBytes: 0,
            ManagedAllocatedBytesDuringMedia: 0,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0,
            OutboundApplicationFrames: 0,
            InboundApplicationFrames: 0,
            OutboundRtpPackets: 0,
            InboundRtpPackets: 0,
            CallQuality: [],
            Errors: errors.Take(20).ToArray());
    }

    private static void CopyErrors(
        IEnumerable<string> source,
        ConcurrentQueue<string> destination)
    {
        foreach (var error in source)
        {
            destination.Enqueue(error);
        }
    }

    private static void DrainErrors(
        ConcurrentQueue<string> source,
        ConcurrentQueue<string> destination)
    {
        while (source.TryDequeue(out var error))
        {
            destination.Enqueue(error);
        }
    }

    private static async Task<CallRtpStatistics?[]> WaitForRtpSnapshotsAsync(
        IReadOnlyList<ICall> calls,
        DateTimeOffset? minimumCapturedAtUtc)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var complete = true;
            for (var index = 0; index < calls.Count; index++)
            {
                var snapshot = calls[index].RtpStatistics;
                if (snapshot is null ||
                    minimumCapturedAtUtc is { } minimum &&
                    snapshot.Value.CapturedAtUtc < minimum)
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return calls.Select(call => call.RtpStatistics).ToArray();
    }

    private static async Task DelayUntilAsync(long targetTimestamp)
    {
        while (true)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), targetTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining).ConfigureAwait(false);
        }
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        checked((long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency));

    private async Task<IPhoneLine> RegisterAsync(VoipClient client)
    {
        var registration = await client.ConnectAsync(
                new SipAccount
                {
                    SipServer = _asterisk.ContainerIpAddress,
                    Port = 5060,
                    Username = _asterisk.Username,
                    Password = _asterisk.Password,
                    Transport = DomainSipTransport.Udp,
                },
                new ConnectOptions { Timeout = _profile.ConnectTimeout })
            .ConfigureAwait(false);
        if (!registration.IsSuccess || registration.Line is null)
        {
            throw new InvalidOperationException(
                $"Capacity registration failed: {registration.Status}; {registration.Error}");
        }

        return registration.Line;
    }

    private async Task<DialMeasurement> DialAsync(
        VoipClient client,
        IPhoneLine line,
        int index)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await client.DialAndWaitUntilConnectedAsync(
                    line,
                    _asterisk.CallTargetUri("echo"),
                    new DialWaitOptions { ConnectTimeout = _profile.ConnectTimeout })
                .ConfigureAwait(false);
            stopwatch.Stop();
            return result.IsSuccess && result.Call is not null
                ? new DialMeasurement(index, stopwatch.Elapsed, result.Call, null)
                : new DialMeasurement(
                    index,
                    stopwatch.Elapsed,
                    null,
                    $"{result.Status}; state={result.FinalCallState}; " +
                    $"reason={result.Call?.TerminationReason}; error={result.Error}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DialMeasurement(
                index,
                stopwatch.Elapsed,
                null,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task HangupAllAsync(
        IEnumerable<ICall> calls,
        ConcurrentQueue<string> errors)
    {
        await Task.WhenAll(calls.Select(async call =>
        {
            try
            {
                await call.HangupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Enqueue(
                    $"Call {call.CallId} cleanup: {ex.GetType().Name}: {ex.Message}");
            }
        })).ConfigureAwait(false);
    }

    private async Task<int> WaitForNoChannelsAsync(ConcurrentQueue<string> errors)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var active = -1;
        try
        {
            do
            {
                active = await CountActiveChannelsAsync().ConfigureAwait(false);
                if (active == 0)
                {
                    return 0;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
            while (DateTimeOffset.UtcNow < deadline);

            errors.Enqueue($"Asterisk still reported {active} active channels after cleanup.");
        }
        catch (Exception ex)
        {
            errors.Enqueue($"Channel cleanup query: {ex.GetType().Name}: {ex.Message}");
        }

        return active;
    }

    private async Task<int> CountActiveChannelsAsync()
    {
        var output = await _asterisk
            .ExecAsync("asterisk", "-rx", "core show channels")
            .ConfigureAwait(false);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var marker = line.IndexOf("active channel", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0)
            {
                continue;
            }

            if (int.TryParse(
                line[..marker].Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count))
            {
                return count;
            }
        }

        throw new InvalidOperationException(
            $"Could not parse Asterisk active channel count. Output:{Environment.NewLine}{output}");
    }

    private async Task<AsteriskResources> ReadAsteriskResourcesAsync()
    {
        var output = await _asterisk
            .ExecAsync(
                "sh",
                "-c",
                "cat /sys/fs/cgroup/memory.current; cat /sys/fs/cgroup/cpu.stat")
            .ConfigureAwait(false);
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 ||
            !long.TryParse(
                lines[0],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var memoryBytes))
        {
            throw new InvalidOperationException(
                $"Could not parse Asterisk cgroup memory metrics:{Environment.NewLine}{output}");
        }

        var usageLine = lines.FirstOrDefault(line =>
            line.StartsWith("usage_usec ", StringComparison.Ordinal));
        if (usageLine is null ||
            !long.TryParse(
                usageLine["usage_usec ".Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var cpuUsageMicroseconds))
        {
            throw new InvalidOperationException(
                $"Could not parse Asterisk cgroup CPU metrics:{Environment.NewLine}{output}");
        }

        return new AsteriskResources(cpuUsageMicroseconds, memoryBytes);
    }

    private CalloraCapacityReport CreateReport(
        DateTimeOffset startedAt,
        CalloraCapacityMachine machine,
        int largestValidated,
        int? firstUnstable,
        bool runCompleted,
        bool cleanTeardown,
        double cleanupSeconds,
        int channelsAfterCleanup,
        IReadOnlyList<CalloraCapacityTrial> trials,
        IEnumerable<string> errors)
    {
        return new CalloraCapacityReport(
            startedAt,
            DateTimeOffset.UtcNow,
            machine,
            _profile,
            largestValidated,
            firstUnstable,
            runCompleted &&
            firstUnstable is null &&
            largestValidated == _profile.Levels[^1] &&
            cleanTeardown,
            runCompleted,
            cleanTeardown,
            cleanupSeconds,
            channelsAfterCleanup,
            trials.ToArray(),
            errors.Take(20).ToArray());
    }

    private async Task WriteReportAsync(CalloraCapacityReport report)
    {
        var reportDirectory = Path.GetDirectoryName(_profile.ReportPath);
        if (!string.IsNullOrEmpty(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
        }

        var temporaryPath = _profile.ReportPath + ".tmp";
        await File.WriteAllTextAsync(
                temporaryPath,
                report.ToJson(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(false);
        File.Move(temporaryPath, _profile.ReportPath, overwrite: true);
    }

    private VoipClient NewClient(ILoggerFactory loggerFactory) =>
        new(new VoipConfiguration
        {
            UserAgent = "CalloraCapacityBenchmark/1.0",
            LoggerFactory = loggerFactory,
            SrtpPolicy = SrtpPolicy.Disabled,
            PreferredAudioCodecs = ["PCMU"],
            MaxConcurrentCallsPerLine = _profile.SdkCallLimit,
        });

    private static CalloraCapacityMachine CaptureMachine() =>
        new(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    private static double NormalizeCpu(TimeSpan cpu, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero
            ? 0
            : cpu.TotalMilliseconds / elapsed.TotalMilliseconds /
              Environment.ProcessorCount * 100;

    private static double NormalizeCpu(long cpuMicroseconds, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero
            ? 0
            : cpuMicroseconds / 1000d / elapsed.TotalMilliseconds /
              Environment.ProcessorCount * 100;

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }

    private sealed record DialMeasurement(
        int Index,
        TimeSpan Elapsed,
        ICall? Call,
        string? Error);

    private sealed record AsteriskResources(
        long CpuUsageMicroseconds,
        long MemoryBytes);
}
