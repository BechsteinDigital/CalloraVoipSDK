using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Proves the WebRTC relay binding producer assembles a working TURN control stack from a gathered allocation:
/// the factory it returns, given the transport's targeted send, yields a binding whose relay send path installs
/// a permission for the peer (RFC 8656 §9) and frames the datagram as a Send indication to the relay server
/// (§10), and whose control sink threads the CreatePermission response back. A fake TURN server answers over the
/// captured targeted send — no socket runs.
/// </summary>
public sealed class WebRtcRelayBindingTests
{
    private static readonly IPEndPoint Server = new(IPAddress.Parse("198.51.100.9"), 3478);
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("203.0.113.7"), 50000);

    [Fact]
    public async Task CreateFactory_builds_a_binding_that_permissions_the_peer_then_relays_the_check()
    {
        var codec = new StunMessageCodec();
        var allocation = new TurnAllocateResult
        {
            RelayedEndPoint = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 49152),
            LifetimeSeconds = 600,
            EffectiveCredentials = new StunCredentials
            {
                Username = "user", Password = "pass", Realm = "callora.example", Nonce = "nonce-1"
            },
        };

        var factory = WebRtcRelayBinding.CreateFactory(Server, allocation, NullLoggerFactory.Instance);

        var sentToServer = new List<byte[]>();
        RelayIceBinding? binding = null;
        ValueTask TargetedSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            Assert.Equal(Server, target); // everything the relay stack sends goes to the TURN server
            var raw = datagram.ToArray();
            sentToServer.Add(raw);

            // The fake TURN server answers a CreatePermission request through the binding's control sink.
            var message = codec.Decode(raw);
            if (message is { MessageClass: StunMessageClass.Request }
                && (TurnMessageMethod)(ushort)message.MessageMethod == TurnMessageMethod.CreatePermission)
            {
                binding!.OnControl(EmptySuccess(codec, message));
            }

            return ValueTask.CompletedTask;
        }

        binding = factory(TargetedSend);

        Assert.NotNull(binding);
        Assert.Equal(Server, binding.Indication.RelayServer);

        var check = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await binding.RelaySend(check, Peer, CancellationToken.None);

        Assert.Contains(sentToServer, b => IsCreatePermissionFor(codec, b, Peer));
        Assert.Contains(sentToServer, b => IsSendIndicationFor(codec, b, Peer, check));

        // The CreatePermission must be MESSAGE-INTEGRITY-authenticated with the allocation's effective
        // credentials (RFC 8656 §9 / RFC 5389 §10.2) — proving the gathered REALM/NONCE were wired through,
        // not just that a request was sent.
        var permission = sentToServer.Single(b => IsCreatePermissionFor(codec, b, Peer));
        var authKey = allocation.EffectiveCredentials!.WithRealmAndNonce("callora.example", "nonce-1").DeriveHmacKey();
        Assert.True(codec.VerifyIntegrity(permission, authKey),
            "CreatePermission must carry a valid MESSAGE-INTEGRITY derived from the allocation credentials");
    }

    [Fact]
    public async Task CreateFactory_builds_a_keepalive_that_refreshes_the_allocation_then_deletes_it_on_dispose()
    {
        var codec = new StunMessageCodec();
        var allocation = new TurnAllocateResult
        {
            RelayedEndPoint = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 49152),
            LifetimeSeconds = 2, // refresh cadence = lifetime/2 = 1s, so the loop refreshes without a long wait
            EffectiveCredentials = new StunCredentials
            {
                Username = "user", Password = "pass", Realm = "callora.example", Nonce = "nonce-1"
            },
        };

        var factory = WebRtcRelayBinding.CreateFactory(Server, allocation, NullLoggerFactory.Instance);

        var refreshLifetimes = new List<uint>();
        var firstKeepaliveRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RelayIceBinding? binding = null;

        ValueTask TargetedSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            Assert.Equal(Server, target);
            var message = codec.Decode(datagram.ToArray());
            if (message is { MessageClass: StunMessageClass.Request }
                && (TurnMessageMethod)(ushort)message.MessageMethod == TurnMessageMethod.Refresh)
            {
                var requested = TurnAttributeMapper.DecodeLifetime(message)?.Seconds ?? 0;
                lock (refreshLifetimes) refreshLifetimes.Add(requested);
                if (requested > 0)
                    firstKeepaliveRefresh.TrySetResult(); // a keepalive refresh (not the teardown) round-tripped
                binding!.OnControl(RefreshSuccess(codec, message, requested));
            }

            return ValueTask.CompletedTask;
        }

        binding = factory(TargetedSend);
        Assert.NotNull(binding);
        Assert.NotNull(binding.KeepAlive);

        binding.KeepAlive.Start();
        await firstKeepaliveRefresh.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await binding.KeepAlive.DisposeAsync(); // must issue the teardown Refresh(0)

        lock (refreshLifetimes)
        {
            Assert.Contains(2u, refreshLifetimes); // a keepalive refresh requested the allocation lifetime
            Assert.Contains(0u, refreshLifetimes); // teardown deleted the allocation (Refresh lifetime 0)
        }
    }

    private static byte[] RefreshSuccess(IStunMessageCodec codec, StunMessage request, uint lifetime) =>
        codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = new[] { TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetime }) }
        });

    private static bool IsCreatePermissionFor(IStunMessageCodec codec, byte[] datagram, IPEndPoint peer)
    {
        var message = codec.Decode(datagram);
        return message is { MessageClass: StunMessageClass.Request }
            && (TurnMessageMethod)(ushort)message.MessageMethod == TurnMessageMethod.CreatePermission
            && TurnAttributeMapper.DecodeXorPeerAddress(message)?.EndPoint.Address.Equals(peer.Address) == true;
    }

    private static bool IsSendIndicationFor(IStunMessageCodec codec, byte[] datagram, IPEndPoint peer, byte[] data)
    {
        var message = codec.Decode(datagram);
        return message is { MessageClass: StunMessageClass.Indication }
            && (TurnMessageMethod)(ushort)message.MessageMethod == TurnMessageMethod.Send
            && peer.Equals(TurnAttributeMapper.DecodeXorPeerAddress(message)?.EndPoint)
            && data.AsSpan().SequenceEqual(TurnAttributeMapper.DecodeData(message)?.Value.ToArray());
    }

    private static byte[] EmptySuccess(IStunMessageCodec codec, StunMessage request) =>
        codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = Array.Empty<StunAttribute>()
        });
}
