using System.Buffers.Binary;
using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies triggered connectivity checks (RFC 8445 §7.3.1.4): when the inbound handler accepts a
/// check from a source other than the nominated remote (a peer-reflexive path, §7.3.1.3), the media
/// attachment sends one confirming connectivity check back to that source, learn-once. Checks from
/// the nominated remote are not re-triggered (consent freshness already validates that path).
/// </summary>
public sealed class IceMediaAttachmentTriggeredCheckTests
{
    private const string LocalUfrag = "localU";
    private const string PeerUfrag = "peerU";
    private const string LocalPassword = "localP";
    private static readonly IPEndPoint NominatedRemote = new(IPAddress.Loopback, 40000);
    private static readonly StunMessageCodec Codec = new();

    private static CallMediaParameters IceParameters() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 41000),
        RemoteEndPoint = NominatedRemote,
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        IceEnabled = true,
        IceControlling = true,
        LocalIceUfrag = LocalUfrag,
        LocalIcePwd = LocalPassword,
        RemoteIceUfrag = PeerUfrag,
        RemoteIcePwd = "peerP",
    };

    // A valid inbound check targeting us: USERNAME "{our}:{their}", ICE-CONTROLLED (no conflict with
    // our controlling role), signed with our local password.
    private static byte[] InboundCheckFromPeer()
    {
        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = StunMessage.CreateBindingRequest().TransactionId,
            Attributes =
            [
                new UsernameAttribute { Value = LocalUfrag + ":" + PeerUfrag },
                new PriorityAttribute { Value = 100u },
                new IceControlledAttribute { TieBreaker = 5 },
            ],
        };
        return Codec.EncodeWithIntegrity(request, StunKeyDerivation.ShortTermKey(LocalPassword), addFingerprint: true);
    }

    private static bool IsBindingRequestTo(ReadOnlyMemory<byte> datagram, IPEndPoint destination, IPEndPoint expected)
        => destination.Equals(expected)
           && datagram.Length >= 2
           && (BinaryPrimitives.ReadUInt16BigEndian(datagram.Span) & 0x0110) == 0x0000;

    [Fact]
    public async Task Accepted_check_from_new_source_triggers_one_confirming_check()
    {
        var newSource = new IPEndPoint(IPAddress.Loopback, 55555);
        var triggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var triggeredCount = 0;

        ValueTask Capture(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken ct)
        {
            if (IsBindingRequestTo(datagram, destination, newSource))
            {
                Interlocked.Increment(ref triggeredCount);
                triggered.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        await using var attachment = new IceMediaAttachment(IceMediaParameters.FromCall(IceParameters()), Capture, NullLoggerFactory.Instance);

        attachment.OnStunPacketReceived(InboundCheckFromPeer(), newSource);
        await triggered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref triggeredCount));

        // Learn-once: a second accepted check from the same source does not trigger another.
        attachment.OnStunPacketReceived(InboundCheckFromPeer(), newSource);
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref triggeredCount));
    }

    [Fact]
    public async Task Accepted_check_from_the_nominated_remote_is_not_re_triggered()
    {
        var triggeredToRemote = 0;

        ValueTask Capture(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken ct)
        {
            if (IsBindingRequestTo(datagram, destination, NominatedRemote))
                Interlocked.Increment(ref triggeredToRemote);
            return ValueTask.CompletedTask;
        }

        await using var attachment = new IceMediaAttachment(IceMediaParameters.FromCall(IceParameters()), Capture, NullLoggerFactory.Instance);

        attachment.OnStunPacketReceived(InboundCheckFromPeer(), NominatedRemote);
        await Task.Delay(150);

        Assert.Equal(0, Volatile.Read(ref triggeredToRemote));
    }
}
