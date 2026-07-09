using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies that ICE connectivity checks put the RFC 8445 §7.2.2 attributes on the wire
/// (I2b wiring): the probe hands PRIORITY and ICE-CONTROLLING / ICE-CONTROLLED to the STUN
/// client, and the client carries them through the real encode path without breaking the
/// request. USE-CANDIDATE (regular nomination) is deliberately absent until a later package.
/// </summary>
public sealed class StunIceCheckWiringTests
{
    private static readonly IPEndPoint Local = new(IPAddress.Loopback, 40000);
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 41000);

    [Fact]
    public async Task Controlling_check_forwards_priority_and_controlling_attribute()
    {
        var client = new RecordingStunClient();
        var probe = new StunIceProbe(client, NullLoggerFactory.Instance);

        var ok = await probe.TryCheckConnectivityAsync(
            Local, Remote,
            localIceUfrag: "localU", remoteIceUfrag: "remoteU", remoteIcePassword: "pw",
            localCandidatePriority: 987654u, isControlling: true, tieBreaker: 0xABCDEF0123456789,
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.True(ok);
        var attrs = client.LastAdditionalAttributes;
        Assert.NotNull(attrs);

        var priority = attrs!.OfType<PriorityAttribute>().Single();
        Assert.Equal(987654u, priority.Value);

        var controlling = attrs.OfType<IceControllingAttribute>().Single();
        Assert.Equal(0xABCDEF0123456789, controlling.TieBreaker);

        Assert.Empty(attrs.OfType<IceControlledAttribute>());
        Assert.Empty(attrs.OfType<UseCandidateAttribute>()); // no nomination in this package
    }

    [Fact]
    public async Task Controlled_check_forwards_controlled_attribute()
    {
        var client = new RecordingStunClient();
        var probe = new StunIceProbe(client, NullLoggerFactory.Instance);

        await probe.TryCheckConnectivityAsync(
            Local, Remote,
            localIceUfrag: "localU", remoteIceUfrag: "remoteU", remoteIcePassword: "pw",
            localCandidatePriority: 1u, isControlling: false, tieBreaker: 42,
            timeout: TimeSpan.FromMilliseconds(50));

        var attrs = client.LastAdditionalAttributes;
        Assert.NotNull(attrs);
        var controlled = attrs!.OfType<IceControlledAttribute>().Single();
        Assert.Equal(42ul, controlled.TieBreaker);
        Assert.Empty(attrs.OfType<IceControllingAttribute>());
    }

    [Fact]
    public async Task Binding_request_with_ice_attributes_succeeds_over_loopback()
    {
        // End-to-end integrity: the ICE attributes survive the real encode path (covered by
        // MESSAGE-INTEGRITY) and a real STUN server answers with the mapped endpoint.
        var codec = new StunMessageCodec();
        await using var server = new StunServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            codec,
            responseIntegrityKey: null,
            NullLogger<StunServer>.Instance);
        server.Start(new StunBindingRequestHandler(codec, NullLogger<StunBindingRequestHandler>.Instance));

        using var mediaSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        mediaSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var mediaEndPoint = (IPEndPoint)mediaSocket.LocalEndPoint!;

        var client = new StunClient(codec, NullLogger<StunClient>.Instance);
        IReadOnlyList<StunAttribute> iceAttributes = StunIceCheckAttributes.Build(
            priority: 1234u, isControlling: true, tieBreaker: 99, useCandidate: false);

        var result = await client.QueryBindingAsync(
            server.LocalEndPoint,
            sharedUdpSocket: mediaSocket,
            additionalAttributes: iceAttributes);

        Assert.Equal(mediaEndPoint, result.MappedEndPoint);
    }

    private sealed class RecordingStunClient : IStunClient
    {
        public IReadOnlyList<StunAttribute>? LastAdditionalAttributes { get; private set; }

        public Task<StunBindingResult> QueryBindingAsync(
            IPEndPoint serverEndPoint,
            StunCredentials? credentials = null,
            StunTransport transport = StunTransport.Udp,
            string? tlsTargetHost = null,
            RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback = null,
            IPEndPoint? localEndPoint = null,
            Socket? sharedUdpSocket = null,
            IReadOnlyList<StunAttribute>? additionalAttributes = null,
            CancellationToken ct = default)
        {
            LastAdditionalAttributes = additionalAttributes;
            return Task.FromResult(new StunBindingResult { MappedEndPoint = serverEndPoint, IsXorMapped = true });
        }
    }
}
