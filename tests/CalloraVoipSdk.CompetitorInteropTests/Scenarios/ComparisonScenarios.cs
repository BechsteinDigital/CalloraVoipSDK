using System.Diagnostics;
using MiniCore.Compare.Interop.Adapters;
using MiniCore.Compare.Interop.Asterisk;
using MiniCore.Compare.Interop.Audio;
using Xunit;
using Xunit.Abstractions;

namespace MiniCore.Compare.Interop.Scenarios;

public sealed class ComparisonScenarios
{
    private readonly ITestOutputHelper _output;

    public ComparisonScenarios(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<StackKind> Stacks => new()
    {
        StackKind.Callora,
        StackKind.SipSorcery,
        StackKind.Ozeki,
    };

    public static TheoryData<StackKind, string> RemoteRejections => new()
    {
        { StackKind.Callora, "busy" },
        { StackKind.Callora, "decline" },
        { StackKind.Callora, "nonexistent" },
        { StackKind.SipSorcery, "busy" },
        { StackKind.SipSorcery, "decline" },
        { StackKind.SipSorcery, "nonexistent" },
        { StackKind.Ozeki, "busy" },
        { StackKind.Ozeki, "decline" },
        { StackKind.Ozeki, "nonexistent" },
    };

    public static TheoryData<StackKind, bool> CancellationCleanupExpectations => new()
    {
        { StackKind.Callora, false },
        { StackKind.SipSorcery, true },
        { StackKind.Ozeki, true },
    };

    public static TheoryData<StackKind, bool> PbxOutageDetectionExpectations => new()
    {
        { StackKind.Callora, true },
        { StackKind.SipSorcery, true },
        { StackKind.Ozeki, false },
    };

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Registration(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = ComparisonStackFactory.Create(kind);

        await stack.RegisterAsync(asterisk.Account).ConfigureAwait(false);

        Assert.True(stack.IsRegistered, $"{stack.Name} did not report a successful registration.");
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Outbound_call_connects_and_receives_media(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);

        var attempt = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        AssertConnected(stack, attempt);
        Assert.NotNull(attempt.Call);
        await using var call = attempt.Call!;
        Assert.True(call.IsConnected);
        await WaitUntilAsync(
                () => call.ReceivedPacketCount >= 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} received no outbound-call media.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Inbound_call_is_answered_and_receives_media(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);

        var incomingTask = stack.WaitForIncomingAndAnswerAsync(TimeSpan.FromSeconds(15));
        await asterisk.OriginateInboundAsync().ConfigureAwait(false);
        await using var call = await incomingTask.ConfigureAwait(false);

        Assert.True(call.IsConnected);
        await WaitUntilAsync(
                () => call.ReceivedPacketCount >= 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} received no inbound-call media.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task No_answer_honours_connect_timeout(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();

        var attempt = await stack
            .DialAsync(asterisk.Target("noanswer"), TimeSpan.FromSeconds(4))
            .ConfigureAwait(false);

        stopwatch.Stop();
        Assert.Equal(DialAttemptStatus.Timeout, attempt.Status);
        Assert.Null(attempt.Call);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9));
        Assert.Equal(0, stack.ActiveCallCount);
    }

    [Theory]
    [MemberData(nameof(RemoteRejections))]
    public async Task Remote_rejection_cleans_up_and_stack_remains_reusable(
        StackKind kind,
        string extension)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();

        var rejected = await stack
            .DialAsync(asterisk.Target(extension), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        stopwatch.Stop();
        Assert.Equal(DialAttemptStatus.Failed, rejected.Status);
        Assert.Null(rejected.Call);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
        Assert.Equal(0, stack.ActiveCallCount);
        Assert.True(stack.IsRegistered, $"{stack.Name} lost registration after {extension} rejection.");
        await WaitUntilAsync(
                async () => (await asterisk.ShowChannelsAsync().ConfigureAwait(false))
                    .Contains("0 active channels", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(8),
                $"{stack.Name} left an Asterisk channel after {extension} rejection.")
            .ConfigureAwait(false);

        var recovery = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, recovery);
        await using var recoveredCall = recovery.Call!;
        await WaitUntilAsync(
                () => recoveredCall.ReceivedPacketCount >= 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} did not receive media after recovering from {extension} rejection.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(CancellationCleanupExpectations))]
    public async Task Caller_cancellation_observes_wire_cleanup_and_stack_reusability(
        StackKind kind,
        bool expectsCancelCleanup)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await asterisk.EnablePjsipLoggerAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        var canceled = await stack
            .DialAsync(
                asterisk.Target("noanswer"),
                TimeSpan.FromSeconds(30),
                cancellation.Token)
            .ConfigureAwait(false);

        stopwatch.Stop();
        Assert.Equal(DialAttemptStatus.Canceled, canceled.Status);
        Assert.Null(canceled.Call);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(8));
        Assert.Equal(0, stack.ActiveCallCount);
        Assert.True(stack.IsRegistered, $"{stack.Name} lost registration after caller cancellation.");

        var channelWasCleaned = await WaitUntilOrTimeoutAsync(
                async () => (await asterisk.ShowChannelsAsync().ConfigureAwait(false))
                    .Contains("0 active channels", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        var logs = await asterisk.GetLogsAsync().ConfigureAwait(false);
        Assert.Equal(expectsCancelCleanup, logs.Contains("CANCEL sip:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectsCancelCleanup, channelWasCleaned);

        var recovery = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, recovery);
        await using var recoveredCall = recovery.Call!;
        await WaitUntilAsync(
                () => recoveredCall.ReceivedPacketCount >= 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} did not receive media after recovering from caller cancellation.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Remote_bye_ends_call_and_stack_remains_reusable(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await asterisk.EnablePjsipLoggerAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);

        var attempt = await stack
            .DialAsync(asterisk.Target("remotehangup"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, attempt);
        await using var remotelyEndedCall = attempt.Call!;

        await WaitUntilAsync(
                () => !remotelyEndedCall.IsConnected,
                TimeSpan.FromSeconds(8),
                $"{stack.Name} did not expose the remote BYE as a disconnected call.")
            .ConfigureAwait(false);
        Assert.Equal(0, stack.ActiveCallCount);
        Assert.True(stack.IsRegistered, $"{stack.Name} lost registration after the remote BYE.");
        await WaitUntilAsync(
                async () => (await asterisk.ShowChannelsAsync().ConfigureAwait(false))
                    .Contains("0 active channels", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(8),
                $"{stack.Name} left an Asterisk channel after the remote BYE.")
            .ConfigureAwait(false);

        var logs = await asterisk.GetLogsAsync().ConfigureAwait(false);
        Assert.Contains("BYE sip:", logs, StringComparison.OrdinalIgnoreCase);

        var recovery = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, recovery);
        await using var recoveredCall = recovery.Call!;
        await WaitUntilAsync(
                () => recoveredCall.ReceivedPacketCount >= 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} did not receive media after the remote BYE.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(PbxOutageDetectionExpectations))]
    public async Task Pbx_restart_is_detected_and_registration_recovers(
        StackKind kind,
        bool expectsObservableRegistrationLoss)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);
        var outageDetection = Stopwatch.StartNew();

        await asterisk.ClearPersistedContactsAsync().ConfigureAwait(false);
        await asterisk.StopAsync().ConfigureAwait(false);
        var registrationLossWasObserved = await WaitUntilOrTimeoutAsync(
                () => Task.FromResult(!stack.IsRegistered),
                TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);
        outageDetection.Stop();
        Assert.Equal(expectsObservableRegistrationLoss, registrationLossWasObserved);

        var recovery = Stopwatch.StartNew();
        await asterisk.StartAsync().ConfigureAwait(false);
        await WaitUntilAsync(
                async () => !HasNoContacts(await asterisk.ShowContactsAsync().ConfigureAwait(false)),
                TimeSpan.FromSeconds(35),
                $"{stack.Name} did not restore its Asterisk contact after the PBX restart.")
            .ConfigureAwait(false);
        Assert.True(stack.IsRegistered, $"{stack.Name} restored its contact but not its public registration state.");
        recovery.Stop();

        _output.WriteLine(
            $"{stack.Name}: registration loss observed={registrationLossWasObserved} "
            + $"after {outageDetection.Elapsed}; "
            + $"registration restored in {recovery.Elapsed}.");

        var recoveredDial = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, recoveredDial);
        await using var recoveredCall = recoveredDial.Call!;
        await WaitUntilAsync(
                () => recoveredCall.ReceivedPacketCount >= 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} received no media after PBX restart recovery.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Receives_rfc4733_dtmf(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);

        var attempt = await stack
            .DialAsync(asterisk.Target("dtmf"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, attempt);
        await using var call = attempt.Call!;

        await WaitUntilAsync(
                () => call.ReceivedDtmf.Length >= 4,
                TimeSpan.FromSeconds(15),
                $"{stack.Name} did not receive all four DTMF events.")
            .ConfigureAwait(false);

        Assert.Equal("1234", call.ReceivedDtmf);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Wav_playback_reaches_the_remote_echo(StackKind kind)
    {
        await using var temp = await TemporaryDirectory.CreateAsync().ConfigureAwait(false);
        var wavPath = Path.Combine(temp.Path, "tone.wav");
        await TestWaveFile
            .CreateToneAsync(wavPath, TimeSpan.FromMilliseconds(800))
            .ConfigureAwait(false);

        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);
        var attempt = await stack
            .DialAsync(asterisk.Target("echo"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, attempt);
        await using var call = attempt.Call!;
        var before = call.ReceivedPacketCount;

        await call.PlayWavAsync(wavPath).ConfigureAwait(false);

        await WaitUntilAsync(
                () => call.ReceivedPacketCount >= before + 10,
                TimeSpan.FromSeconds(8),
                $"{stack.Name} playback was not echoed back as RTP.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Incoming_media_is_recorded_as_wav(StackKind kind)
    {
        await using var temp = await TemporaryDirectory.CreateAsync().ConfigureAwait(false);
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);
        var attempt = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, attempt);
        await using var call = attempt.Call!;
        await WaitUntilAsync(
                () => call.ReceivedPacketCount >= 5,
                TimeSpan.FromSeconds(8),
                $"{stack.Name} received no media to record.")
            .ConfigureAwait(false);

        await using var recording = await call
            .StartWavRecordingAsync(temp.Path)
            .ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
        await recording.StopAsync().ConfigureAwait(false);

        var output = Assert.Single(recording.OutputFiles);
        var info = new FileInfo(output);
        Assert.True(info.Exists, $"{stack.Name} did not create a recording file.");
        Assert.True(info.Length > TestWaveFile.WaveHeaderSize, $"{stack.Name} created an empty WAV file.");
        var header = await File.ReadAllBytesAsync(output).ConfigureAwait(false);
        Assert.True(header.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.True(header.AsSpan(8, 4).SequenceEqual("WAVE"u8));
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Hold_and_unhold_negotiate_with_Asterisk_and_resume_media(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await asterisk.EnablePjsipLoggerAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);
        var attempt = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, attempt);
        await using var call = attempt.Call!;

        await WaitUntilAsync(
                () => call.ReceivedPacketCount >= 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} received no baseline media before hold.")
            .ConfigureAwait(false);

        await call.HoldAsync().ConfigureAwait(false);
        await WaitUntilAsync(
                () => call.IsOnHold,
                TimeSpan.FromSeconds(8),
                $"{stack.Name} did not enter its public local-hold state.")
            .ConfigureAwait(false);
        Assert.True(call.IsConnected, $"{stack.Name} dropped the call while entering hold.");

        var holdLogs = await asterisk.GetLogsAsync().ConfigureAwait(false);
        Assert.True(
            ContainsHoldDirection(holdLogs),
            $"{stack.Name} produced no externally visible hold SDP at Asterisk.");

        await call.UnholdAsync().ConfigureAwait(false);
        await WaitUntilAsync(
                () => !call.IsOnHold,
                TimeSpan.FromSeconds(8),
                $"{stack.Name} did not leave its public local-hold state.")
            .ConfigureAwait(false);
        Assert.True(call.IsConnected, $"{stack.Name} dropped the call while leaving hold.");

        var unholdLogs = await asterisk.GetLogsAsync().ConfigureAwait(false);
        Assert.True(
            ContainsUnholdDirectionAfterHold(unholdLogs),
            $"{stack.Name} produced no externally visible sendrecv SDP after its hold SDP.");

        var beforeResumedMedia = call.ReceivedPacketCount;
        await WaitUntilAsync(
                () => call.ReceivedPacketCount >= beforeResumedMedia + 10,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} did not resume incoming media after unhold.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Media_bridge_forwards_source_audio_to_echo_leg(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        await using var stack = await CreateRegisteredStackAsync(kind, asterisk).ConfigureAwait(false);

        var sourceAttempt = await stack
            .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        var echoAttempt = await stack
            .DialAsync(asterisk.Target("echo"), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        AssertConnected(stack, sourceAttempt);
        AssertConnected(stack, echoAttempt);
        await using var source = sourceAttempt.Call!;
        await using var echo = echoAttempt.Call!;
        await WaitUntilAsync(
                () => source.ReceivedPacketCount >= 5,
                TimeSpan.FromSeconds(8),
                $"{stack.Name} source leg received no media.")
            .ConfigureAwait(false);
        var echoBefore = echo.ReceivedPacketCount;

        await using var bridge = stack.Bridge(source, echo);

        await WaitUntilAsync(
                () => echo.ReceivedPacketCount >= echoBefore + 20,
                TimeSpan.FromSeconds(10),
                $"{stack.Name} bridge did not forward media to the echo leg.")
            .ConfigureAwait(false);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Cleanup_removes_calls_and_registration(StackKind kind)
    {
        await using var asterisk = await StartAsteriskAsync().ConfigureAwait(false);
        var stack = ComparisonStackFactory.Create(kind);

        {
            await using var ownedStack = stack;
            await stack.RegisterAsync(asterisk.Account).ConfigureAwait(false);
            var attempt = await stack
                .DialAsync(asterisk.Target("answer"), TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            AssertConnected(stack, attempt);
            Assert.Equal(1, stack.ActiveCallCount);
        }

        Assert.False(stack.IsRegistered);
        Assert.Equal(0, stack.ActiveCallCount);
        await WaitUntilAsync(
                async () => (await asterisk.ShowChannelsAsync().ConfigureAwait(false))
                    .Contains("0 active channels", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(8),
                $"{kind} left an active Asterisk channel.")
            .ConfigureAwait(false);
        await WaitUntilAsync(
                async () => HasNoContacts(await asterisk.ShowContactsAsync().ConfigureAwait(false)),
                TimeSpan.FromSeconds(8),
                $"{kind} left an Asterisk registration contact.")
            .ConfigureAwait(false);
    }

    private static async Task<AsteriskTestServer> StartAsteriskAsync()
    {
        var server = new AsteriskTestServer();
        try
        {
            await server.StartAsync().ConfigureAwait(false);
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AssertConnected(IComparisonStack stack, DialAttempt attempt) =>
        Assert.True(
            attempt.Status == DialAttemptStatus.Connected,
            $"{stack.Name} dial failed with {attempt.Status}. {attempt.Detail}");

    private static async Task<IComparisonStack> CreateRegisteredStackAsync(
        StackKind kind,
        AsteriskTestServer asterisk)
    {
        var stack = ComparisonStackFactory.Create(kind);
        try
        {
            await stack.RegisterAsync(asterisk.Account).ConfigureAwait(false);
            return stack;
        }
        catch
        {
            await stack.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.True(predicate(), failureMessage);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        Assert.True(await predicate().ConfigureAwait(false), failureMessage);
    }

    private static async Task<bool> WaitUntilOrTimeoutAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return await predicate().ConfigureAwait(false);
    }

    private static bool HasNoContacts(string output) =>
        output.Contains("No objects found", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Objects found: 0", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsHoldDirection(string logs) =>
        logs.Contains("a=sendonly", StringComparison.OrdinalIgnoreCase)
        || logs.Contains("a=inactive", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsUnholdDirectionAfterHold(string logs)
    {
        var sendOnly = logs.LastIndexOf("a=sendonly", StringComparison.OrdinalIgnoreCase);
        var inactive = logs.LastIndexOf("a=inactive", StringComparison.OrdinalIgnoreCase);
        var hold = Math.Max(sendOnly, inactive);
        return hold >= 0
            && logs.IndexOf("a=sendrecv", hold, StringComparison.OrdinalIgnoreCase) > hold;
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static Task<TemporaryDirectory> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mini-core-compare-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return Task.FromResult(new TemporaryDirectory(path));
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
