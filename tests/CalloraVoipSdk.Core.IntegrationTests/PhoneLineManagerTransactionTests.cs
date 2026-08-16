using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Registering, unregistering and disposing a phone line are transactional in the manager (#165 P2-6): a
/// registration that fails to start leaves nothing registered, an unregister that fails on the wire still
/// tears the line down locally, and one line that throws on disposal does not strand the lines behind it.
/// The failure mode all three share is a line that is invisible to the manager while still fully alive.
/// </summary>
public sealed class PhoneLineManagerTransactionTests
{
    private static SipAccount Account(string user) => new() { Username = user, SipServer = "sip.invalid" };

    private static PhoneLineManager Manager(Func<SipAccount, FakeLineChannel> channelFor)
    {
        var registry = new CallManager();
        return new PhoneLineManager(account => new PhoneLine(
            account, channelFor(account), registry, maxCalls: 0, NullLoggerFactory.Instance));
    }

    [Fact]
    public void A_line_whose_registration_fails_to_start_is_not_left_in_the_manager()
    {
        var channel = new FakeLineChannel { ThrowOnStart = true };
        var manager = Manager(_ => channel);

        Assert.Throws<InvalidOperationException>(() => manager.Register(Account("alice")));

        Assert.Empty(manager.All);
        Assert.True(channel.Disposed, "the half-registered line must be torn down, not just forgotten");
    }

    [Fact]
    public async Task An_unregister_that_fails_on_the_wire_still_disposes_the_line()
    {
        var channel = new FakeLineChannel { ThrowOnStop = true };
        var manager = Manager(_ => channel);
        var line = manager.Register(Account("alice"));

        await Assert.ThrowsAsync<IOException>(() => manager.UnregisterAsync(line.LineId));

        Assert.Empty(manager.All);                 // it is out of the registry either way
        Assert.True(channel.Disposed, "the line must not stay alive after being removed from the manager");
    }

    [Fact]
    public async Task A_cancelled_unregister_still_disposes_the_line()
    {
        var channel = new FakeLineChannel { HangOnStop = true };
        var manager = Manager(_ => channel);
        var line = manager.Register(Account("alice"));

        using var cts = new CancellationTokenSource();
        var unregister = manager.UnregisterAsync(line.LineId, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unregister);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public void Disposing_the_manager_tears_down_every_line_even_when_one_throws()
    {
        var faulty = new FakeLineChannel { ThrowOnDispose = true };
        var healthy = new FakeLineChannel();
        var queue = new Queue<FakeLineChannel>([faulty, healthy]);
        var manager = Manager(_ => queue.Dequeue());

        manager.Register(Account("alice"));
        manager.Register(Account("bob"));

        manager.Dispose();

        Assert.True(healthy.Disposed, "a line behind a faulty one must still be disposed");
        Assert.Empty(manager.All);
    }

    private sealed class FakeLineChannel : ILineChannel
    {
        public bool ThrowOnStart { get; init; }
        public bool ThrowOnStop { get; init; }
        public bool HangOnStop { get; init; }
        public bool ThrowOnDispose { get; init; }
        public bool Disposed { get; private set; }

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        {
            if (ThrowOnStart)
                throw new InvalidOperationException("registration transport is down");
        }

        public void StopRegistration() { }

        public Task StopRegistrationAsync(CancellationToken ct = default)
        {
            if (ThrowOnStop)
                return Task.FromException(new IOException("REGISTER Expires:0 failed"));
            return HangOnStop ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose)
                throw new InvalidOperationException("channel teardown failed");
        }

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();
        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) => throw new NotSupportedException();
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
