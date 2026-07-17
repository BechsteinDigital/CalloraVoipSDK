using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the running ICE transport state on the call (WebRTC roadmap, ADR-009 step 1): the call exposes
/// a live <see cref="CallIceState"/> that moves <c>Connected → Disconnected</c> on RFC 7675 consent loss
/// and raises <c>IceConnectionStateChanged</c>, complementing the one-shot <see cref="ICall.IceSnapshot"/>.
/// </summary>
public sealed class CallIceConnectionStateTests : IDisposable
{
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public CallIceConnectionStateTests()
    {
        _channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        _call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            _channel, new FakePhoneLine(), NullLogger<Call>.Instance);
    }

    public void Dispose() => _channel.Dispose();

    [Fact]
    public void Ice_connection_state_starts_disabled()
    {
        ICall call = _call;
        Assert.Equal(CallIceState.Disabled, call.IceConnectionState);
    }

    [Fact]
    public void Connected_then_disconnected_updates_state_and_raises_transitions()
    {
        ICall call = _call;
        var transitions = new List<(CallIceState Old, CallIceState New)>();
        call.IceConnectionStateChanged += (_, e) =>
        {
            Assert.Same(call, e.Call);
            transitions.Add((e.OldState, e.NewState));
        };

        _call.SetIceConnectionState(CallIceState.Connected);
        _call.SetIceConnectionState(CallIceState.Disconnected);

        Assert.Equal(CallIceState.Disconnected, call.IceConnectionState);
        Assert.Equal(
            [(CallIceState.Disabled, CallIceState.Connected), (CallIceState.Connected, CallIceState.Disconnected)],
            transitions);
    }

    [Fact]
    public void Setting_the_same_state_is_idempotent_and_raises_no_event()
    {
        _call.SetIceConnectionState(CallIceState.Connected);

        var count = 0;
        _call.IceConnectionStateChanged += (_, _) => count++;
        _call.SetIceConnectionState(CallIceState.Connected);

        Assert.Equal(0, count);
        Assert.Equal(CallIceState.Connected, ((ICall)_call).IceConnectionState);
    }
}
