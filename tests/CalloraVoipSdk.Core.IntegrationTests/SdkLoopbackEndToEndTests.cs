using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// True SDK-to-SDK end-to-end tests over 127.0.0.1 loopback UDP. Two in-process endpoints
/// (UAC "alice" dials, UAS "bob" answers) exchange a registrar-less direct-IP call through the
/// real SIP transport, signaling, SDP negotiation, media orchestrator, and RTP/SRTP media path.
/// Nothing about transport or media is mocked — this is the integration baseline that proves the
/// stack interoperates as a whole and that SRTP is genuinely wired end-to-end.
/// </summary>
public sealed class SdkLoopbackEndToEndTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CleartextCall_EstablishesAndExchangesAudioBothWays_ThenTerminatesOnBye()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var uas = new LoopbackSdkEndpoint("bob", SrtpPolicy.Disabled);
        using var uac = new LoopbackSdkEndpoint("alice", SrtpPolicy.Disabled);

        var inbound = uas.NextInboundCallAsync();
        var caller = await uac.DialAsync(uas.SipPort, "bob", SrtpPolicy.Disabled, cts.Token);
        var callee = await inbound;

        await AwaitAllAsync(Timeout, caller.Connected, callee.Connected);

        // Plain RTP: no SRTP keys negotiated on either leg.
        Assert.False(caller.NegotiatedMedia?.IsSrtpNegotiated ?? true);
        Assert.Null(caller.NegotiatedMedia?.SrtpKeys);
        Assert.Null(callee.NegotiatedMedia?.SrtpKeys);

        var fromAlice = Payload(0xA1);
        var fromBob = Payload(0xB2);
        var deliveredToBob = callee.ExpectAudioAsync(fromAlice, Timeout);
        var deliveredToAlice = caller.ExpectAudioAsync(fromBob, Timeout);

        await SendInterleavedBurstAsync(caller, fromAlice, callee, fromBob);

        Assert.Equal(fromAlice, await deliveredToBob);
        Assert.Equal(fromBob, await deliveredToAlice);

        await caller.HangupAsync();
        await AwaitAllAsync(Timeout, caller.Terminated, callee.Terminated);
    }

    [Fact]
    public async Task SrtpCall_NegotiatesSdesAndDeliversAudioAsCleartextBothWays()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var uas = new LoopbackSdkEndpoint("bob", SrtpPolicy.Required);
        using var uac = new LoopbackSdkEndpoint("alice", SrtpPolicy.Required);

        var inbound = uas.NextInboundCallAsync();
        var caller = await uac.DialAsync(uas.SipPort, "bob", SrtpPolicy.Required, cts.Token);
        var callee = await inbound;

        await AwaitAllAsync(Timeout, caller.Connected, callee.Connected);

        // SDES negotiated on both legs: SRTP flagged, its own key material composed, SAVP profile.
        Assert.True(caller.NegotiatedMedia?.IsSrtpNegotiated);
        Assert.True(callee.NegotiatedMedia?.IsSrtpNegotiated);
        Assert.NotNull(caller.NegotiatedMedia!.SrtpKeys);
        Assert.NotNull(callee.NegotiatedMedia!.SrtpKeys);
        Assert.Contains("SAVP", caller.NegotiatedMedia!.MediaProfile, StringComparison.Ordinal);

        var fromAlice = Payload(0x5A);
        var fromBob = Payload(0x6B);
        var deliveredToBob = callee.ExpectAudioAsync(fromAlice, Timeout);
        var deliveredToAlice = caller.ExpectAudioAsync(fromBob, Timeout);

        await SendInterleavedBurstAsync(caller, fromAlice, callee, fromBob);

        // Correct cleartext arrives on both sides only if SRTP was actually applied on send and
        // correctly unprotected on receive — the core proof that SRTP is wired end-to-end.
        Assert.Equal(fromAlice, await deliveredToBob);
        Assert.Equal(fromBob, await deliveredToAlice);

        await caller.HangupAsync();
        await AwaitAllAsync(Timeout, caller.Terminated, callee.Terminated);
    }

    [Fact]
    public async Task Dtmf_Rfc4733EventFromCaller_RaisesCalleeDtmfWithCorrectTone()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var uas = new LoopbackSdkEndpoint("bob", SrtpPolicy.Disabled);
        using var uac = new LoopbackSdkEndpoint("alice", SrtpPolicy.Disabled);

        var inbound = uas.NextInboundCallAsync();
        var caller = await uac.DialAsync(uas.SipPort, "bob", SrtpPolicy.Disabled, cts.Token);
        var callee = await inbound;

        await AwaitAllAsync(Timeout, caller.Connected, callee.Connected);

        const byte tone = 5;
        var received = callee.FirstDtmf;

        // Send a few events to ride over the media-session start race and any packet loss.
        for (var i = 0; i < 5 && !received.IsCompleted; i++)
        {
            await caller.SendDtmfAsync(tone);
            await Task.Delay(100);
        }

        var winner = await Task.WhenAny(received, Task.Delay(Timeout));
        Assert.True(winner == received, "Callee never raised the inbound DTMF event.");
        Assert.Equal(tone, await received);

        await caller.HangupAsync();
        await AwaitAllAsync(Timeout, caller.Terminated, callee.Terminated);
    }

    [Fact]
    public async Task Teardown_DisposesBothEndpointsCleanly_AndSecondDisposeIsIdempotent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var uas = new LoopbackSdkEndpoint("bob", SrtpPolicy.Disabled);
        var uac = new LoopbackSdkEndpoint("alice", SrtpPolicy.Disabled);
        try
        {
            var inbound = uas.NextInboundCallAsync();
            var caller = await uac.DialAsync(uas.SipPort, "bob", SrtpPolicy.Disabled, cts.Token);
            var callee = await inbound;
            await AwaitAllAsync(Timeout, caller.Connected, callee.Connected);

            await caller.HangupAsync();
            await AwaitAllAsync(Timeout, caller.Terminated, callee.Terminated);
        }
        finally
        {
            // First dispose tears down media, signaling, and transport; second dispose must be a no-op.
            uac.Dispose();
            uac.Dispose();
            uas.Dispose();
            uas.Dispose();
        }
    }

    private static async Task SendInterleavedBurstAsync(
        LoopbackCallHandle a,
        byte[] fromA,
        LoopbackCallHandle b,
        byte[] fromB,
        int count = 25,
        int gapMs = 20)
    {
        for (var i = 0; i < count; i++)
        {
            await a.SendAudioAsync(fromA);
            await b.SendAudioAsync(fromB);
            await Task.Delay(gapMs);
        }
    }

    private static async Task AwaitAllAsync(TimeSpan timeout, params Task[] tasks)
    {
        var all = Task.WhenAll(tasks);
        var winner = await Task.WhenAny(all, Task.Delay(timeout));
        if (winner != all)
            throw new TimeoutException("Timed out waiting for expected call state transitions.");
        await all;
    }

    private static byte[] Payload(byte seed)
    {
        var payload = new byte[160];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(seed + i);
        return payload;
    }
}
