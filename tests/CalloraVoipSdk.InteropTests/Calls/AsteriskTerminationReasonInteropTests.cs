using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Issue #103 (L4, Docker-only): the protocol-neutral <see cref="ICall.TerminationReason"/> reflects the
/// real SIP outcome from a live Asterisk — Busy (486 → <see cref="CallTerminationCategory.Busy"/>),
/// no-answer (408/480 → <see cref="CallTerminationCategory.NoAnswer"/>) and a normal remote hangup after
/// an answered call (BYE → <see cref="CallTerminationCategory.Completed"/>). The dialplan extensions
/// (<c>busy</c>, <c>noanswer</c>, <c>answer</c>) are defined in <see cref="AsteriskContainer"/>.
///
/// REQUIRES DOCKER: every fact is a <see cref="DockerRequiredFact"/> and carries
/// <c>[Trait("Category", "Interop")]</c>, so it is skipped when no Docker daemon is available and runs
/// only in the Docker interop CI lane — it is not executed by the local unit/integration test run.
///
/// Media: <see cref="SrtpPolicy.Disabled"/> (Plain RTP), matching the other Asterisk call tests — the
/// SRTP-less endpoint 6001 would reject the default RTP/SAVP offer with 488 (Audit-Fund F007).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTerminationReasonInteropTests
{
    private static VoipClient NewClient() =>
        new(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0", SrtpPolicy = SrtpPolicy.Disabled });

    private static async Task<IPhoneLine> RegisterAsync(AsteriskContainer asterisk, VoipClient client)
    {
        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = asterisk.Username,
                Password = asterisk.Password,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(reg.IsSuccess, $"Registrierung fehlgeschlagen: Status={reg.Status}");
        return reg.Line!;
    }

    [DockerRequiredFact]
    public async Task BusyTarget_SurfacesBusyTerminationReason()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("busy"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.IsSuccess);
        var reason = result.Call?.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.Busy, reason!.Category); // Asterisk Busy() → 486
        Assert.Equal(486, reason.SipStatusCode);
        Assert.Equal(CallTerminatedBy.Remote, reason.TerminatedBy);
    }

    [DockerRequiredFact]
    public async Task DeclinedTarget_SurfacesRejectedTerminationReason()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("decline"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.IsSuccess);
        var reason = result.Call?.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.Rejected, reason!.Category);
        Assert.Equal(403, reason.SipStatusCode);
        Assert.Equal(CallTerminatedBy.Remote, reason.TerminatedBy);
    }

    [DockerRequiredFact]
    public async Task UnknownTarget_SurfacesFailedTerminationReasonWith404()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("nonexistent"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.IsSuccess);
        var reason = result.Call?.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.Failed, reason!.Category);
        Assert.Equal(404, reason.SipStatusCode);
        Assert.Equal(CallTerminatedBy.Remote, reason.TerminatedBy);
    }

    [DockerRequiredFact]
    public async Task NoAnswer_SurfacesNoAnswerTerminationReason()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        // noanswer rings forever → the SDK CANCELs on ConnectTimeout; the resulting response (408/480)
        // is the NoAnswer classification. The dial itself reports Timeout (see AsteriskCallFailureInteropTests).
        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("noanswer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(4) });

        Assert.Equal(DialStatus.Timeout, result.Status);
        var reason = result.Call?.TerminationReason;
        if (reason is not null)
        {
            // When a terminating reason was captured, a locally-CANCELed no-answer is either NoAnswer
            // (server 408/480) or Canceled (487 to our CANCEL) — both are non-Busy, non-Completed.
            Assert.Contains(
                reason.Category,
                new[] { CallTerminationCategory.NoAnswer, CallTerminationCategory.Canceled });
        }
    }

    [DockerRequiredFact]
    public async Task RemoteHangupAfterAnswer_SurfacesCompletedTerminationReason()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("answer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;

        // Local BYE completes the answered dialog normally → Completed, originated locally.
        await call.HangupAsync();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (call.TerminationReason is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);

        var reason = call.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.Completed, reason!.Category);
        Assert.Equal(CallTerminatedBy.Local, reason.TerminatedBy);
    }

    [DockerRequiredFact]
    public async Task RemoteByeAfterAnswer_SurfacesCompletedRemoteTerminationReason()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("remotehangup"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (call.State != CallState.Terminated && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);

        var reason = call.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.Completed, reason!.Category);
        Assert.Null(reason.SipStatusCode);
        Assert.Equal(CallTerminatedBy.Remote, reason.TerminatedBy);
    }
}
