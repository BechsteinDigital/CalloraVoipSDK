using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Contract gate for <see cref="IPhoneLine.UnregisterAsync"/> (HARD-E1). Unregistering a line must be a
/// real, awaitable de-registration: the returned task completes only after the channel's REGISTER
/// Expires:0 round-trip, not before it is even sent. The previous stub stopped the refresh loop and
/// returned a completed task immediately.
/// </summary>
public sealed class PhoneLineUnregisterContractTests
{
    [Fact]
    public async Task UnregisterAsync_awaits_the_channel_deregistration()
    {
        var channel = new GatedLineChannel();
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            channel,
            new EmptyCallRegistry(),
            maxCalls: 0,
            NullLoggerFactory.Instance);

        var unregister = line.UnregisterAsync();

        // The channel de-register was invoked and the line's task is still pending it — a stub that
        // returned Task.CompletedTask would already be complete and would not have called the channel.
        Assert.True(channel.StopRegistrationAsyncCalled);
        Assert.False(unregister.IsCompleted);

        channel.ReleaseDeregistration();
        await unregister; // completes only after the awaited de-register
    }

    private sealed class GatedLineChannel : ILineChannel
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool StopRegistrationAsyncCalled { get; private set; }

        public void ReleaseDeregistration() => _gate.TrySetResult();

        public Task StopRegistrationAsync(CancellationToken ct = default)
        {
            StopRegistrationAsyncCalled = true;
            return _gate.Task;
        }

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        { }

        public void StopRegistration() { }

        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();

        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class EmptyCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }

        public IReadOnlyCollection<ICall> Active => [];
    }
}
