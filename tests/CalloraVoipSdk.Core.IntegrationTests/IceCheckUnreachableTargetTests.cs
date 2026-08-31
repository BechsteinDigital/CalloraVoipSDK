using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// What a connectivity check does when the target cannot be reached from this socket at all.
/// </summary>
/// <remarks>
/// <para>
/// A host with several interfaces — a developer machine running Docker, a server with a virtual bridge —
/// gives the peer candidates it cannot answer from our socket. An IPv6 candidate on an IPv4 socket is the
/// clearest case: every send fails with <see cref="SocketError.AddressFamilyNotSupported"/>, identically,
/// forever.
/// </para>
/// <para>
/// Retransmitting those cost the full transaction budget — three transmissions, 500 ms apart — and held
/// the pair "in flight" for it. Regular nomination waits for higher-priority pairs to resolve before it
/// nominates, so one dead high-priority candidate delayed the whole session while a working pair sat
/// validated and unused. Measured at about 1.5 s of the 2 s to nomination.
/// </para>
/// </remarks>
public sealed class IceCheckUnreachableTargetTests
{
    private static readonly IPEndPoint Target = new(IPAddress.Parse("192.168.1.9"), 40000);

    [Fact]
    public async Task An_unreachable_target_is_not_retransmitted_to()
    {
        var attempts = 0;
        var session = NewSession();

        var sent = await session.SendCheckVia(
            (_, _, _) =>
            {
                attempts++;
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            },
            Target,
            useCandidate: false,
            CancellationToken.None);

        Assert.False(sent);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_transient_failure_still_gets_the_full_retransmission_schedule()
    {
        // The distinction the fix rests on: a full send buffer or a momentary routing change may well
        // succeed on the next try, and RFC 8445 §14 wants those retransmitted. Only "this address is not
        // reachable from this socket" is hopeless.
        var attempts = 0;
        var session = NewSession();

        var sent = await session.SendCheckVia(
            (_, _, _) =>
            {
                attempts++;
                throw new SocketException((int)SocketError.NoBufferSpaceAvailable);
            },
            Target,
            useCandidate: false,
            CancellationToken.None);

        Assert.False(sent);
        Assert.Equal(3, attempts);
    }

    /// <summary>A session whose delays return immediately, so the test measures attempts and not seconds.</summary>
    private static IceMediaConsentSession NewSession() => new(
        new StunMessageCodec(),
        sendRaw: (_, _, _) => ValueTask.CompletedTask,
        remoteEndPoint: Target,
        localUfrag: "loc",
        remoteUfrag: "rem",
        remotePassword: "remPassword",
        priority: 100,
        controlling: true,
        tieBreaker: 1,
        onConsentLost: () => { },
        NullLoggerFactory.Instance,
        delay: (_, _) => Task.CompletedTask);
}
