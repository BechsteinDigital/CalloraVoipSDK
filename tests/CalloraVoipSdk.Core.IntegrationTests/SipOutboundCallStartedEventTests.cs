using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Emission contract for <c>OutboundCallStarted</c> (HARD-C3). The event must fire exactly once and
/// only when an outbound INVITE actually succeeds: firing per attempt (before the transaction)
/// dispatched a session that a redirect/retry then disposes, and fired again for every retry target.
/// </summary>
public sealed class SipOutboundCallStartedEventTests
{
    private static SipInviteRequest NewInvite() => new()
    {
        LocalUsername = "alice",
        LocalDomain = "example.com",
        RemoteUri = "sip:bob@192.0.2.10",
        SessionDescription = "v=0\r\n",
        Timeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Does_not_fire_when_the_invite_fails()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance);

        var started = 0;
        service.OutboundCallStarted += (_, _) => Interlocked.Increment(ref started);

        // 486 Busy Here is a non-retryable final response — the attempt fails and its session is
        // disposed, so no OutboundCallStarted must have been dispatched for it.
        transport.ResponseFactory = request =>
            request.Method.Equals("INVITE", StringComparison.Ordinal)
                ? CreateResponse(request, 486, "Busy Here")
                : null;

        await Assert.ThrowsAnyAsync<Exception>(() => service.InviteAsync(NewInvite()));

        Assert.Equal(0, Volatile.Read(ref started));
    }

    [Fact]
    public async Task Fires_exactly_once_on_success()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance);

        var started = 0;
        service.OutboundCallStarted += (_, _) => Interlocked.Increment(ref started);

        transport.ResponseFactory = request =>
            request.Method.Equals("INVITE", StringComparison.Ordinal)
                ? CreateResponse(request, 200, "OK")
                : null;

        await service.InviteAsync(NewInvite());

        Assert.Equal(1, Volatile.Read(ref started));
    }

    [Fact]
    public async Task Fires_only_once_across_a_retry_that_finally_succeeds()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance);

        var started = 0;
        service.OutboundCallStarted += (_, _) => Interlocked.Increment(ref started);

        // First INVITE → 415 forces exactly one reduced-body retry (a new session); the retry → 200.
        // Two attempts, two sessions — but only one successful start, so exactly one event.
        var inviteCount = 0;
        transport.ResponseFactory = request =>
        {
            if (!request.Method.Equals("INVITE", StringComparison.Ordinal))
                return null;

            return Interlocked.Increment(ref inviteCount) == 1
                ? CreateResponse(request, 415, "Unsupported Media Type")
                : CreateResponse(request, 200, "OK");
        };

        await service.InviteAsync(NewInvite());

        Assert.Equal(2, Volatile.Read(ref inviteCount));
        Assert.Equal(1, Volatile.Read(ref started));
    }

    private static SipResponse CreateResponse(CapturedSipRequest request, int statusCode, string reasonPhrase)
    {
        var toHeader = request.Headers["To"];
        if (SipProtocol.ExtractTag(toHeader) is null)
            toHeader = $"{toHeader};tag=remote-tag";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = request.Headers["Via"],
            ["From"] = request.Headers["From"],
            ["To"] = toHeader,
            ["Call-ID"] = request.Headers["Call-ID"],
            ["CSeq"] = request.Headers["CSeq"],
            ["Contact"] = "<sip:bob@192.0.2.10:5060>",
        };

        return new SipResponse(statusCode, reasonPhrase, headers, string.Empty);
    }
}
