using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// What the agent does with the "no address yet" placeholder a trickling peer sends.
/// </summary>
/// <remarks>
/// <para>
/// A description that carries no candidates yet names its media address as <c>0.0.0.0</c> on the discard
/// port 9 (RFC 4145 §4, RFC 3264 §5.1) — the standard way to say "the address follows by trickle". Taken
/// for a real destination it became a checklist pair that nothing can ever answer, and because it
/// inherited host priority it outranked every genuine pair.
/// </para>
/// <para>
/// Regular nomination waits for higher-priority pairs to resolve, so a working pair stayed validated and
/// unused until the placeholder's check timed out — about two seconds, on every call, regardless of how
/// many real candidates existed. That last part is what made it look like a strategy problem for so long:
/// removing real candidates changed nothing, because the blocker was never one of them.
/// </para>
/// </remarks>
public sealed class IcePlaceholderRemoteTests
{
    [Theory]
    [InlineData("0.0.0.0", 9)]     // what a trickling browser actually sends
    [InlineData("0.0.0.0", 40000)] // the address is the signal, not the port
    [InlineData("::", 9)]          // the IPv6 spelling of the same thing
    public async Task A_placeholder_remote_produces_no_candidate_pair(string address, int port)
    {
        var parameters = new IceMediaParameters(
            new IPEndPoint(IPAddress.Parse(address), port),
            IceEnabled: true,
            IceControlling: true,
            LocalIceUfrag: "loc",
            LocalIcePwd: "locPassword",
            RemoteIceUfrag: "rem",
            RemoteIcePwd: "remPassword");

        await using var attachment = new IceMediaAttachment(
            parameters, (_, _, _) => ValueTask.CompletedTask, NullLoggerFactory.Instance);

        // The placeholder is not a peer endpoint either: inbound media claiming to come from it is not
        // the peer, it is an address nobody can legitimately send from.
        Assert.False(attachment.IsKnownRemoteEndPoint(new IPEndPoint(IPAddress.Parse(address), port)));
    }

    [Fact]
    public async Task A_dual_stack_socket_still_recognises_an_ipv4_peer()
    {
        // A dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d while the candidate was advertised
        // as a.b.c.d. Compared verbatim the peer's own traffic is refused — SIPSorcery learned this as
        // issue #1603, and the DTLS source filter now depends on this comparison.
        var advertised = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 40000);
        var parameters = new IceMediaParameters(
            advertised,
            IceEnabled: true,
            IceControlling: true,
            LocalIceUfrag: "loc",
            LocalIcePwd: "locPassword",
            RemoteIceUfrag: "rem",
            RemoteIcePwd: "remPassword");

        await using var attachment = new IceMediaAttachment(
            parameters, (_, _, _) => ValueTask.CompletedTask, NullLoggerFactory.Instance);

        var asSeenOnDualStack = new IPEndPoint(IPAddress.Parse("::ffff:192.168.1.5"), 40000);
        Assert.True(attachment.IsKnownRemoteEndPoint(asSeenOnDualStack));
    }

    [Fact]
    public async Task A_hairpin_source_is_recognised_once_the_deployment_translates_it()
    {
        // A peer that reaches us through a TURN server on this machine shows up with a local interface
        // address, while the candidate it advertised carries the relay's public one. Compared as observed
        // the two never match and the peer's own media is refused as if it came from a stranger — silent
        // call, nothing in the log naming a cause. Only the deployment knows its topology, so it supplies
        // the mapping.
        var advertised = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 50000);   // the relay's public address
        var asObserved = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 50000);      // what the wire shows

        var parameters = new IceMediaParameters(
            advertised,
            IceEnabled: true,
            IceControlling: true,
            LocalIceUfrag: "loc",
            LocalIcePwd: "locPassword",
            RemoteIceUfrag: "rem",
            RemoteIcePwd: "remPassword");

        await using var withoutTranslation = new IceMediaAttachment(
            parameters, (_, _, _) => ValueTask.CompletedTask, NullLoggerFactory.Instance);
        Assert.False(withoutTranslation.IsKnownRemoteEndPoint(asObserved));

        await using var withTranslation = new IceMediaAttachment(
            parameters, (_, _, _) => ValueTask.CompletedTask, NullLoggerFactory.Instance,
            remoteEndPointTranslator: ep => ep.Equals(asObserved) ? advertised : ep);
        Assert.True(withTranslation.IsKnownRemoteEndPoint(asObserved));

        // And it stays a source-side rule: the advertised candidate is still itself, not something the
        // translation rewrote.
        Assert.True(withTranslation.IsKnownRemoteEndPoint(advertised));
    }

    [Fact]
    public async Task A_throwing_translator_does_not_take_the_session_with_it()
    {
        // A deployment's delegate on the media path, invoked per inbound datagram. The contract says it
        // must not throw; one that does would otherwise break every source comparison, not just its own.
        // Falling back to the untranslated source refuses a hairpin peer — and keeps everyone else.
        var advertised = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 40000);
        var parameters = new IceMediaParameters(
            advertised,
            IceEnabled: true,
            IceControlling: true,
            LocalIceUfrag: "loc",
            LocalIcePwd: "locPassword",
            RemoteIceUfrag: "rem",
            RemoteIcePwd: "remPassword");

        await using var attachment = new IceMediaAttachment(
            parameters, (_, _, _) => ValueTask.CompletedTask, NullLoggerFactory.Instance,
            remoteEndPointTranslator: _ => throw new InvalidOperationException("translator blew up"));

        Assert.True(attachment.IsKnownRemoteEndPoint(advertised));
    }

    [Theory]
    [InlineData("192.168.1.5", 40000)]
    [InlineData("127.0.0.1", 9)] // a real address on the discard port is still somewhere to send
    public async Task A_real_remote_still_becomes_a_candidate_pair(string address, int port)
    {
        // The guard must not swallow the non-trickling case, where the description names a real address
        // and that address is the only candidate there is. The port alone decides nothing: fixtures and
        // some peers use 9 for an endpoint they fully intend to reach.
        var remote = new IPEndPoint(IPAddress.Parse(address), port);
        var parameters = new IceMediaParameters(
            remote,
            IceEnabled: true,
            IceControlling: true,
            LocalIceUfrag: "loc",
            LocalIcePwd: "locPassword",
            RemoteIceUfrag: "rem",
            RemoteIcePwd: "remPassword");

        await using var attachment = new IceMediaAttachment(
            parameters, (_, _, _) => ValueTask.CompletedTask, NullLoggerFactory.Instance);

        Assert.True(attachment.IsKnownRemoteEndPoint(remote));
    }
}
