using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #104 — an IP-authenticated static-IP trunk is recognised by its source address and expects no
/// REGISTER; sending one is at best ignored and at worst rejected. Before this, the SDK always
/// registered: <c>ReregisterOptions.Disabled</c> only stopped <i>re</i>-registration, so such a trunk
/// could not be modelled at all.
///
/// <para>
/// With <see cref="SipAccount.Register"/> = <see langword="false"/> the line skips REGISTER entirely and
/// settles in <see cref="LineState.Ready"/> — operational, but never <see cref="LineState.Registered"/>.
/// Both reference stacks treat registration as detachable this way: SIPSorcery keeps it in a separate
/// <c>SIPRegistrationUserAgent</c> so a call agent works without one, and pjsip skips REGISTER when
/// <c>reg_uri</c> is unset.
/// </para>
/// </summary>
public sealed class RegistrationFreeTrunkTests
{
    private static SipAccount Trunk(bool register) => new()
    {
        Username = "4930123456",
        SipServer = "trunk.example",
        Register = register,
    };

    private static PhoneLine NewLine(SipAccount account, ILineChannel channel) =>
        new(account, channel, new NoopCallRegistry(), maxCalls: 0, NullLoggerFactory.Instance);

    // ── The wire decision: no REGISTER at all ────────────────────────────────

    [Fact]
    public void A_registration_free_line_never_asks_the_channel_to_register()
    {
        var channel = new RecordingLineChannel();
        var line = NewLine(Trunk(register: false), channel);

        line.StartRegistration();

        Assert.Equal(0, channel.StartRegistrationCalls);
        Assert.Equal(LineState.Ready, line.State);
    }

    [Fact]
    public void A_normal_account_still_registers()
    {
        // The default must be untouched: everything that registers today keeps registering.
        var channel = new RecordingLineChannel();
        var line = NewLine(Trunk(register: true), channel);

        line.StartRegistration();

        Assert.Equal(1, channel.StartRegistrationCalls);
        Assert.NotEqual(LineState.Ready, line.State);
    }

    [Fact]
    public void Register_defaults_to_true()
    {
        Assert.True(new SipAccount { Username = "u", SipServer = "s" }.Register);
    }

    // ── Deregistration must not invent a binding ─────────────────────────────

    [Fact]
    public async Task Unregistering_a_registration_free_line_sends_nothing()
    {
        // REGISTER Expires:0 removes a binding. There never was one, so putting that request on the wire
        // would refer to something that does not exist — the peer never expected us to register.
        var channel = new RecordingLineChannel();
        var line = NewLine(Trunk(register: false), channel);
        line.StartRegistration();

        await line.UnregisterAsync();

        Assert.Equal(0, channel.StopRegistrationCalls);
        Assert.Equal(LineState.Unregistered, line.State);
    }

    [Fact]
    public async Task Unregistering_a_normal_line_still_deregisters()
    {
        var channel = new RecordingLineChannel();
        var line = NewLine(Trunk(register: true), channel);
        line.StartRegistration();

        await line.UnregisterAsync();

        Assert.Equal(1, channel.StopRegistrationCalls);
    }

    // ── Ready is a dialling state ────────────────────────────────────────────

    [Fact]
    public async Task Dialling_is_allowed_in_Ready()
    {
        var channel = new RecordingLineChannel();
        var line = NewLine(Trunk(register: false), channel);
        line.StartRegistration();

        // The fake refuses to build a channel, so the dial cannot complete — but reaching that refusal
        // is the point: it proves the state gate was passed rather than throwing "not registered".
        await Assert.ThrowsAsync<NotSupportedException>(() => line.DialAsync("sip:+4930999@trunk.example"));
    }

    [Fact]
    public async Task Dialling_before_the_line_is_started_is_still_refused()
    {
        // The counterpart: Ready must not be a blanket "always allow". An unstarted line still refuses.
        var channel = new RecordingLineChannel();
        var line = NewLine(Trunk(register: false), channel);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => line.DialAsync("sip:+4930999@trunk.example"));
        Assert.Contains("not ready", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unregistered_normal_line_reports_the_registration_wording()
    {
        // The two modes fail differently, and the message should say which one the caller is in.
        var channel = new RecordingLineChannel();
        var line = NewLine(Trunk(register: true), channel);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => line.DialAsync("sip:+4930999@trunk.example"));
        Assert.Contains("not registered", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── State-machine legality ───────────────────────────────────────────────

    [Fact]
    public void Ready_is_appended_so_existing_enum_values_keep_their_numbers()
    {
        // Consumers persist or transmit these; inserting a member would silently renumber the rest.
        Assert.Equal(0, (int)LineState.Unregistered);
        Assert.Equal(1, (int)LineState.Registering);
        Assert.Equal(2, (int)LineState.Registered);
        Assert.Equal(3, (int)LineState.Reconnecting);
        Assert.Equal(4, (int)LineState.RegistrationFailed);
        Assert.Equal(5, (int)LineState.Failed);
        Assert.Equal(6, (int)LineState.Ready);
    }

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }

    private sealed class RecordingLineChannel : ILineChannel
    {
        public int StartRegistrationCalls { get; private set; }
        public int StopRegistrationCalls { get; private set; }

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        {
            StartRegistrationCalls++;
            onStateChange(LineState.Registering);
        }

        public void StopRegistration() => StopRegistrationCalls++;

        public Task StopRegistrationAsync(CancellationToken ct = default)
        {
            StopRegistrationCalls++;
            return Task.CompletedTask;
        }

        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();

        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) =>
            Task.FromResult(new PublishResult("etag", expiresSeconds));

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
