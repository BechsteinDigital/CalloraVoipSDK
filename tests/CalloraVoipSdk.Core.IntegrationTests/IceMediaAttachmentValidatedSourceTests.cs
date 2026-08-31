using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Reporting a remote source as soon as its inbound ICE check verifies, rather than only once a pair is
/// nominated.
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
/// Reporting it is safe because the check has already been authenticated: a failed MESSAGE-INTEGRITY is
/// discarded rather than answered (<c>IceInboundCheckProcessor</c>), so a source that reaches the callback
/// holds the ICE credential from our own SDP and is by definition not off-path.
/// </para>
/// </remarks>
public sealed class IceMediaAttachmentValidatedSourceTests
{
    [Fact]
    public async Task A_source_is_reported_as_validated_before_any_pair_is_nominated()
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

        var validated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (answerer = new IceMediaAttachment(
            answererParams, AnswererSend, NullLoggerFactory.Instance,
            onPairNominated: ep => nominated.TrySetResult(ep),
            onSourceValidated: ep => validated.TrySetResult(ep)))
        await using (offerer = new IceMediaAttachment(
            offererParams, OffererSend, NullLoggerFactory.Instance))
        {
            answerer.Start();
            offerer.Start();

            var validatedSource = await validated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The offerer's address, learned from its authenticated check — and learned as a source the
            // handshake may already answer, not merely as a candidate to check later.
            Assert.Equal(offererAddr, validatedSource);

            // And nomination still happens, on the same source: the early report is an addition, not a
            // replacement. Without this the fix would trade a slow handshake for one that never settles.
            var nominatedPair = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(offererAddr, nominatedPair);
        }
    }
}
