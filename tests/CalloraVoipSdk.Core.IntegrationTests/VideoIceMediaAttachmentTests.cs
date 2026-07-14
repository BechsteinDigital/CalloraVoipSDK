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
/// Video-m-line ICE on the media layer (RFC 8445 §7.3 / RFC 7675): the video stream attaches ICE
/// to its OWN 5-tuple with the session-shared credentials. Proves the decoupled
/// <see cref="IceMediaParameters"/> view drives an active attachment for the video port exactly as
/// it does for audio — a triggered connectivity check (§7.3.1.4) is answered on the video path —
/// and that a video leg without ICE stays a plain media path (no consent, no responder).
/// </summary>
public sealed class VideoIceMediaAttachmentTests
{
    private const string LocalUfrag = "localU";
    private const string PeerUfrag = "peerU";
    private const string LocalPassword = "localP";
    private static readonly IPEndPoint VideoRemote = new(IPAddress.Loopback, 42000);
    private static readonly StunMessageCodec Codec = new();

    // A video leg with the session-shared ICE credentials stamped (as the enrichers produce).
    private static CallVideoParameters IceVideo(bool iceEnabled = true) => new()
    {
        PayloadType = 96,
        CodecName = "VP8",
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 43000),
        RemoteEndPoint = VideoRemote,
        IceEnabled = iceEnabled,
        IceControlling = true,
        LocalIceUfrag = LocalUfrag,
        LocalIcePwd = LocalPassword,
        RemoteIceUfrag = PeerUfrag,
        RemoteIcePwd = "peerP",
    };

    [Fact]
    public void FromVideo_projects_the_video_5tuple_and_shared_credentials()
    {
        var view = IceMediaParameters.FromVideo(IceVideo());

        Assert.Equal(VideoRemote, view.RemoteEndPoint); // the video port, not audio's
        Assert.True(view.IceEnabled);
        Assert.True(view.IceControlling);
        Assert.Equal(LocalUfrag, view.LocalIceUfrag);
        Assert.Equal(LocalPassword, view.LocalIcePwd);
        Assert.Equal(PeerUfrag, view.RemoteIceUfrag);
        Assert.Equal("peerP", view.RemoteIcePwd);
    }

    [Fact]
    public async Task Video_leg_with_ice_answers_a_triggered_check_on_its_own_socket()
    {
        var newSource = new IPEndPoint(IPAddress.Loopback, 55556);
        var triggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask Capture(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken ct)
        {
            if (IsBindingRequestTo(datagram, destination, newSource))
                triggered.TrySetResult();
            return ValueTask.CompletedTask;
        }

        await using var attachment = new IceMediaAttachment(
            IceMediaParameters.FromVideo(IceVideo()), Capture, NullLoggerFactory.Instance);

        Assert.True(attachment.IsActive);
        attachment.OnStunPacketReceived(InboundCheckFromPeer(), newSource);
        await triggered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Video_leg_without_ice_is_inactive()
    {
        await using var attachment = new IceMediaAttachment(
            IceMediaParameters.FromVideo(IceVideo(iceEnabled: false)),
            (_, _, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance);

        Assert.False(attachment.IsActive);
    }

    // A valid inbound check targeting us on the video path: USERNAME "{our}:{their}", signed with
    // our shared local password, ICE-CONTROLLED (no conflict with our controlling role).
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
}
