using CalloraVoipSdk.Core.Domain.Lines;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #17.13: the <see cref="LineStateRules"/> transition table must admit every transition the registration
/// channel legitimately drives (initial register, refresh, retry, reconnect, permanent failure, de-register)
/// and reject transitions the state machine never produces.
/// </summary>
public sealed class LineStateRulesTests
{
    [Theory]
    // Initial register + refresh + retry re-entry (StartRegistration enters Registering from anywhere).
    [InlineData(LineState.Unregistered, LineState.Registering)]
    [InlineData(LineState.Registered, LineState.Registering)]
    [InlineData(LineState.Reconnecting, LineState.Registering)]
    [InlineData(LineState.RegistrationFailed, LineState.Registering)]
    [InlineData(LineState.Failed, LineState.Registering)]
    // Registration outcomes from an in-flight attempt.
    [InlineData(LineState.Registering, LineState.Registered)]
    [InlineData(LineState.Registering, LineState.Reconnecting)]
    [InlineData(LineState.Registering, LineState.RegistrationFailed)]
    [InlineData(LineState.Registering, LineState.Failed)]
    // Reconnect loop and escalation.
    [InlineData(LineState.Reconnecting, LineState.Failed)]
    [InlineData(LineState.RegistrationFailed, LineState.Failed)]
    // De-registration from any state (StopRegistration).
    [InlineData(LineState.Registered, LineState.Unregistered)]
    [InlineData(LineState.Registering, LineState.Unregistered)]
    [InlineData(LineState.Failed, LineState.Unregistered)]
    // Direct Registered reporting is exercised by existing consumers/tests → kept permissive.
    [InlineData(LineState.Unregistered, LineState.Registered)]
    // Same-state no-op is always allowed.
    [InlineData(LineState.Registered, LineState.Registered)]
    public void Legitimate_transitions_are_allowed(LineState from, LineState to)
        => Assert.True(LineStateRules.CanTransition(from, to));

    [Theory]
    // Failed is terminal: the only escapes are Registering (re-register) and Unregistered (stop). Every other
    // move out of Failed is illegal.
    [InlineData(LineState.Failed, LineState.Registered)]
    [InlineData(LineState.Failed, LineState.Reconnecting)]
    [InlineData(LineState.Failed, LineState.RegistrationFailed)]
    public void Illegal_transitions_are_rejected(LineState from, LineState to)
        => Assert.False(LineStateRules.CanTransition(from, to));
}
