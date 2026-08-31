using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Recognising the peer's endpoints, so inbound DTLS need not wait for a nomination.
/// </summary>
/// <remarks>
/// <para>
/// The consumer this exists for is the DTLS source filter. A browser starts its handshake as soon as it has
/// a usable candidate pair, which is well before the controlling agent sets USE-CANDIDATE. While the filter
/// still pointed at the SDP placeholder (<c>0.0.0.0:9</c>) every record was dropped, the peer got no reply,
/// and it retransmitted on a doubling timer — measured in a real call as drops at +406 ms and +813 ms, and
/// a handshake that took two seconds for what needs two round trips.
/// </para>
/// <para>
/// The set is the peer's candidates — what the remote description carried, what trickled in, and what was
/// discovered peer-reflexively through an authenticated check. Everything else is still refused, which is
/// what stops an off-path sender who guesses the local port from flooding ClientHello records. This is how
/// libwebrtc demultiplexes and the rule SIPSorcery settled on in its issues #1559 and #1731.
/// </para>
/// </remarks>
public sealed class IceMediaAttachmentValidatedSourceTests
{
    [Fact]
    public async Task A_peer_reflexive_source_is_recognised_before_any_pair_is_nominated()
    {
        var offererAddr = new IPEndPoint(IPAddress.Loopback, 50011);
        var answererAddr = new IPEndPoint(IPAddress.Loopback, 50012);

        IceMediaAttachment? offerer = null;
        IceMediaAttachment? answerer = null;

        // Cross-wired in memory, as in IceMediaAttachmentNominationTests: each attachment's raw send feeds
        // the other's receive hook, so this is a real STUN exchange with real integrity checks.
        ValueTask OffererSend(ReadOnlyMemory<byte> dg, IPEndPoint dst, CancellationToken ct)
        {
            var copy = dg.ToArray();
            _ = Task.Run(() => answerer!.OnStunPacketReceived(copy, offererAddr));
            return ValueTask.CompletedTask;
        }

        ValueTask AnswererSend(ReadOnlyMemory<byte> dg, IPEndPoint dst, CancellationToken ct)
        {
            var copy = dg.ToArray();
            _ = Task.Run(() => offerer!.OnStunPacketReceived(copy, answererAddr));
            return ValueTask.CompletedTask;
        }

        var offererParams = new IceMediaParameters(
            answererAddr, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword")
        {
            RemoteCandidates = [new IceRemoteCandidate(answererAddr, Priority: 100)],
        };

        // The answerer is told nothing about where the offerer will come from — its remote is the placeholder
        // the SDP carries when a peer trickles. That is precisely the state in which everything used to be
        // dropped.
        var answererParams = new IceMediaParameters(
            new IPEndPoint(IPAddress.Any, 9), IceEnabled: true, IceControlling: false,
            LocalIceUfrag: "answ", LocalIcePwd: "answPassword", RemoteIceUfrag: "offr", RemoteIcePwd: "offrPassword");

        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (answerer = new IceMediaAttachment(
            answererParams, AnswererSend, NullLoggerFactory.Instance,
            onPairNominated: ep => nominated.TrySetResult(ep)))
        await using (offerer = new IceMediaAttachment(
            offererParams, OffererSend, NullLoggerFactory.Instance))
        {
            answerer.Start();
            offerer.Start();

            // Recognised from its authenticated check, and recognised while nothing is nominated yet —
            // which is exactly the window in which the handshake used to be dropped.
            await WaitUntilAsync(() => answerer!.IsKnownRemoteEndPoint(offererAddr));
            Assert.True(answerer!.IsKnownRemoteEndPoint(offererAddr));

            // An endpoint nobody has heard from stays unknown: the filter still refuses an off-path sender
            // who guesses the port, which is the property the whole check exists for.
            Assert.False(answerer.IsKnownRemoteEndPoint(new IPEndPoint(IPAddress.Loopback, 50099)));

            // And nomination still lands on the same source: recognising it early is an addition, not a
            // replacement. Without this the fix would trade a slow handshake for one that never settles.
            var nominatedPair = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(offererAddr, nominatedPair);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }
}
