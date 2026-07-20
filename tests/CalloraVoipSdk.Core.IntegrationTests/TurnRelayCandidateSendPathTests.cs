using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behaviour of <see cref="TurnRelayCandidateSendPath"/>: the outbound send path of a relay ICE local
/// candidate. It installs a TURN permission (RFC 8656 §9) once per distinct peer IP, frames each datagram as a
/// Send indication (RFC 8656 §10) addressed to the peer, and sends it to the relay server through the injected
/// raw-send delegate — and it does not cache a failed permission, so a retransmitted check re-attempts it. A
/// fake control transactor answers CreatePermission in memory, and the raw send is captured, so no socket runs.
/// </summary>
public sealed class TurnRelayCandidateSendPathTests
{
    private static readonly TimeSpan FastRto = TimeSpan.FromMilliseconds(40);
    private static readonly IPEndPoint RelayServer = new(IPAddress.Parse("198.51.100.9"), 3478);
    private static readonly IPEndPoint PeerA = new(IPAddress.Parse("203.0.113.7"), 50000);
    private static readonly IPEndPoint PeerB = new(IPAddress.Parse("203.0.113.9"), 50000);

    [Fact]
    public async Task SendAsync_installs_a_permission_once_per_peer_ip_and_frames_the_check_as_a_send_indication()
    {
        var codec = new StunMessageCodec();
        var permissionRequests = new List<StunMessage>();
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            if ((TurnMessageMethod)(ushort)request.MessageMethod == TurnMessageMethod.CreatePermission)
                permissionRequests.Add(request);
            transactor.OnControlDatagram(EmptySuccess(codec, request));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);

        var sent = new List<(byte[] Datagram, IPEndPoint Target)>();
        ValueTask RawSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            sent.Add((datagram.ToArray(), target));
            return ValueTask.CompletedTask;
        }

        var sendPath = new TurnRelayCandidateSendPath(
            new TurnRelayIndicationChannel(codec, RelayServer), control, PrimedCredentials(),
            RawSend, NullLogger<TurnRelayCandidateSendPath>.Instance);

        var check = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await sendPath.SendAsync(check, PeerA, CancellationToken.None);
        await sendPath.SendAsync(check, PeerA, CancellationToken.None); // same peer IP → cached permission
        await sendPath.SendAsync(check, PeerB, CancellationToken.None); // new peer IP → new permission

        // One permission per distinct peer IP (2), not per send (3).
        Assert.Equal(2, permissionRequests.Count);
        Assert.Equal(PeerA.Address, PeerOf(permissionRequests[0]).Address);
        Assert.Equal(PeerB.Address, PeerOf(permissionRequests[1]).Address);

        // Every send framed the check as a Send indication to the relay server, addressed to the right peer.
        Assert.Equal(3, sent.Count);
        Assert.All(sent, s => Assert.Equal(RelayServer, s.Target));
        AssertSendIndication(codec, sent[0].Datagram, PeerA, check);
        AssertSendIndication(codec, sent[1].Datagram, PeerA, check);
        AssertSendIndication(codec, sent[2].Datagram, PeerB, check);
    }

    [Fact]
    public async Task A_failed_permission_is_not_cached_and_is_retried_on_the_next_send()
    {
        var codec = new StunMessageCodec();
        var permissionAttempts = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            if ((TurnMessageMethod)(ushort)request.MessageMethod == TurnMessageMethod.CreatePermission)
            {
                permissionAttempts++;
                transactor.OnControlDatagram(permissionAttempts == 1
                    ? Error(codec, request, 400, "Bad Request")
                    : EmptySuccess(codec, request));
            }
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);

        var sent = new List<byte[]>();
        ValueTask RawSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            sent.Add(datagram.ToArray());
            return ValueTask.CompletedTask;
        }

        var sendPath = new TurnRelayCandidateSendPath(
            new TurnRelayIndicationChannel(codec, RelayServer), control, PrimedCredentials(),
            RawSend, NullLogger<TurnRelayCandidateSendPath>.Instance);

        var check = new byte[] { 1, 2, 3 };
        // First send: the permission fails, so the whole send fails and nothing is framed.
        await Assert.ThrowsAsync<TurnException>(async () => await sendPath.SendAsync(check, PeerA, CancellationToken.None));
        Assert.Empty(sent);

        // Second send: the failed permission was not cached, so it is retried — and now succeeds.
        await sendPath.SendAsync(check, PeerA, CancellationToken.None);
        Assert.Equal(2, permissionAttempts);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Concurrent_sends_to_the_same_peer_install_the_permission_exactly_once()
    {
        var codec = new StunMessageCodec();
        var permissionCount = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            if ((TurnMessageMethod)(ushort)request.MessageMethod == TurnMessageMethod.CreatePermission)
                Interlocked.Increment(ref permissionCount);
            transactor.OnControlDatagram(EmptySuccess(codec, request));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);

        var sentCount = 0;
        ValueTask RawSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            Interlocked.Increment(ref sentCount);
            return ValueTask.CompletedTask;
        }

        var sendPath = new TurnRelayCandidateSendPath(
            new TurnRelayIndicationChannel(codec, RelayServer), control, PrimedCredentials(),
            RawSend, NullLogger<TurnRelayCandidateSendPath>.Instance);

        var check = new byte[] { 9, 9, 9 };
        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => sendPath.SendAsync(check, PeerA, CancellationToken.None).AsTask()));

        Assert.Equal(1, permissionCount); // Lazy dedup: exactly one CreatePermission under real concurrency
        Assert.Equal(16, sentCount);
    }

    [Fact]
    public async Task RefreshInstalledPermissionsAsync_re_issues_create_permission_for_every_known_peer()
    {
        var codec = new StunMessageCodec();
        var permissionRequests = new List<StunMessage>();
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            if ((TurnMessageMethod)(ushort)request.MessageMethod == TurnMessageMethod.CreatePermission)
                permissionRequests.Add(request);
            transactor.OnControlDatagram(EmptySuccess(codec, request));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);

        var sendPath = new TurnRelayCandidateSendPath(
            new TurnRelayIndicationChannel(codec, RelayServer), control, PrimedCredentials(),
            (_, _, _) => ValueTask.CompletedTask, NullLogger<TurnRelayCandidateSendPath>.Instance);

        var check = new byte[] { 1 };
        await sendPath.SendAsync(check, PeerA, CancellationToken.None);
        await sendPath.SendAsync(check, PeerB, CancellationToken.None);
        Assert.Equal(2, permissionRequests.Count); // one install per distinct peer IP

        await sendPath.RefreshInstalledPermissionsAsync(CancellationToken.None);

        // Every known peer got a fresh CreatePermission — one per peer, not per send.
        Assert.Equal(4, permissionRequests.Count);
        Assert.Contains(permissionRequests.Skip(2), r => PeerOf(r).Address.Equals(PeerA.Address));
        Assert.Contains(permissionRequests.Skip(2), r => PeerOf(r).Address.Equals(PeerB.Address));
    }

    [Fact]
    public async Task RefreshInstalledPermissionsAsync_is_a_no_op_when_no_permission_is_installed()
    {
        var codec = new StunMessageCodec();
        var permissionRequests = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            if ((TurnMessageMethod)(ushort)request.MessageMethod == TurnMessageMethod.CreatePermission)
                permissionRequests++;
            transactor.OnControlDatagram(EmptySuccess(codec, request));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);

        var sendPath = new TurnRelayCandidateSendPath(
            new TurnRelayIndicationChannel(codec, RelayServer), control, PrimedCredentials(),
            (_, _, _) => ValueTask.CompletedTask, NullLogger<TurnRelayCandidateSendPath>.Instance);

        await sendPath.RefreshInstalledPermissionsAsync(CancellationToken.None);

        Assert.Equal(0, permissionRequests); // nothing installed → nothing to refresh
    }

    [Fact]
    public async Task RefreshInstalledPermissionsAsync_keeps_a_peer_whose_refresh_fails_so_the_next_cycle_retries_it()
    {
        var codec = new StunMessageCodec();
        var permissionAttempts = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            if ((TurnMessageMethod)(ushort)request.MessageMethod == TurnMessageMethod.CreatePermission)
            {
                permissionAttempts++;
                // install succeeds (1), the first refresh fails (2), the second refresh succeeds (3).
                transactor.OnControlDatagram(permissionAttempts == 2
                    ? Error(codec, request, 400, "Bad Request")
                    : EmptySuccess(codec, request));
            }
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);

        var sendPath = new TurnRelayCandidateSendPath(
            new TurnRelayIndicationChannel(codec, RelayServer), control, PrimedCredentials(),
            (_, _, _) => ValueTask.CompletedTask, NullLogger<TurnRelayCandidateSendPath>.Instance);

        await sendPath.SendAsync(new byte[] { 1 }, PeerA, CancellationToken.None); // install (attempt 1)

        // A refresh whose CreatePermission fails must NOT throw and must keep the peer cached.
        await sendPath.RefreshInstalledPermissionsAsync(CancellationToken.None); // attempt 2 (fails, swallowed)

        // The peer is still known, so the next refresh re-attempts it — and succeeds.
        await sendPath.RefreshInstalledPermissionsAsync(CancellationToken.None); // attempt 3 (succeeds)

        Assert.Equal(3, permissionAttempts);
    }

    private static StunCredentials PrimedCredentials() =>
        new() { Username = "user", Password = "pass", Realm = "callora.example", Nonce = "nonce-1" };

    private static IPEndPoint PeerOf(StunMessage message) =>
        TurnAttributeMapper.DecodeXorPeerAddress(message)!.EndPoint;

    private static void AssertSendIndication(
        IStunMessageCodec codec, byte[] datagram, IPEndPoint expectedPeer, byte[] expectedData)
    {
        var message = codec.Decode(datagram)!;
        Assert.Equal(StunMessageClass.Indication, message.MessageClass);
        Assert.Equal(TurnMessageMethod.Send, (TurnMessageMethod)(ushort)message.MessageMethod);
        Assert.Equal(expectedPeer, TurnAttributeMapper.DecodeXorPeerAddress(message)!.EndPoint);
        Assert.Equal(expectedData, TurnAttributeMapper.DecodeData(message)!.Value.ToArray());
    }

    private static byte[] EmptySuccess(IStunMessageCodec codec, StunMessage request) =>
        codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = Array.Empty<StunAttribute>()
        });

    private static byte[] Error(IStunMessageCodec codec, StunMessage request, int code, string reason) =>
        codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.ErrorResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = [new ErrorCodeAttribute { Code = code, Reason = reason }]
        });
}
