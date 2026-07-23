using System.IO;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Observation gate for the fire-and-forget hangups PhoneLine starts from synchronous paths
/// (HARD-E2). A hangup issued while cleaning up (line dispose) or rejecting an over-limit inbound
/// call cannot be awaited there, but a failure must not vanish — it must be logged, not silently
/// dropped from a critical path.
/// </summary>
public sealed class PhoneLineHangupObservationTests
{
    [Fact]
    public async Task Dispose_logs_a_failing_hangup_instead_of_dropping_it()
    {
        var capturing = new CapturingLogger();
        var call = new FaultingCall();
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            new NoopLineChannel(),
            new SingleCallRegistry(call),
            maxCalls: 0,
            new CapturingLoggerFactory(capturing));
        call.Line = line; // the dispose filter keeps only calls belonging to this line

        line.Dispose();

        await PollUntil(() => capturing.Entries.Any(
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("Hangup during line dispose failed", StringComparison.Ordinal)));
    }

    private static async Task PollUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Expected a warning for the failing hangup, but none was logged.");
            await Task.Delay(10);
        }
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class SingleCallRegistry(ICall call) : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [call];
    }

    private sealed class NoopLineChannel : ILineChannel
    {
        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        { }

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();
        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    // Only Line and HangupAsync are exercised by PhoneLine.Dispose; the rest is never reached.
    private sealed class FaultingCall : ICall
    {
        public IPhoneLine Line { get; set; } = null!;

        public Task HangupAsync(CancellationToken ct = default) =>
            Task.FromException(new IOException("simulated hangup failure"));

        public CallId CallId { get; } = CallId.New();
        public CallState State => CallState.Ringing;
        public CallMediaParameters? MediaParameters => null;
        public CallIceState IceConnectionState => CallIceState.Disabled;

        public event EventHandler<CallStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<HoldStateChangedEventArgs>? HoldStateChanged { add { } remove { } }
        public event EventHandler<DtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<TransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<CallQualitySnapshotChangedEventArgs>? QualitySnapshotChanged { add { } remove { } }
        public event EventHandler<CallIceConnectionStateChangedEventArgs>? IceConnectionStateChanged { add { } remove { } }

        public CallDirection Direction => throw new NotImplementedException();
        public string RemoteParty => throw new NotImplementedException();
        public DateTimeOffset StartedAt => throw new NotImplementedException();
        public CallQualitySnapshot QualitySnapshot => throw new NotImplementedException();
        public CallRtpStatistics? RtpStatistics => throw new NotImplementedException();
        public CallIceSnapshot? IceSnapshot => throw new NotImplementedException();
        public string? RemoteAssertedIdentity => throw new NotImplementedException();
        public string? Diversion => throw new NotImplementedException();

        public Task AcceptAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task HoldAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnholdAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default) => throw new NotImplementedException();
        public Task BlindTransferAsync(string targetUri, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CallActionResult> RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CallActionResult> RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CallActionResult> SendInfoAsync(string contentType, string body, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CallActionResult> SendOptionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CallActionResult> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CallActionResult> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
