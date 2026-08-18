using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The relay ICE candidate send path over the stream transport (ADR-073 slice 3, #240): a connectivity check
/// sent through a <see cref="TurnRelayCandidateSendPath"/> whose raw-send writes to a
/// <see cref="StreamRelayMediaTransport"/> installs a TURN permission (RFC 8656 §9) and frames the check as a
/// Send indication (§10) — all over the TCP stream, against a real hosted <see cref="TurnServerHost"/>.
/// </summary>
/// <remarks>
/// The point is that no new abstraction is needed: TurnRelayCandidateSendPath is transport-agnostic (it holds
/// no socket, only an injected raw-send), so the ADR-054 relay-candidate shape composes over the stream simply
/// by injecting a stream write — the payoff of the transport-agnostic seams. The gathered allocation (slice 2)
/// feeds the credentials; the transport (slice 1) carries the control round-trips and the framed indication.
/// </remarks>
public sealed class StreamRelayCandidateSendPathTests
{
    [Fact]
    public async Task A_check_installs_a_permission_and_frames_a_send_indication_over_the_stream()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = IceTransport.Tcp,
            RequireAuthentication = false,
        });
        host.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.LocalEndPoint);
        var stream = tcp.GetStream();
        var codec = new StunMessageCodec();

        // Gather the allocation over the stream (slice 2), then keep the same connection for the send path.
        var allocation = await new TurnStreamAllocationProbe(codec, NullLoggerFactory.Instance)
            .TryAllocateAsync(stream, host.LocalEndPoint, credentials: null, lifetimeSeconds: 600, CancellationToken.None);
        Assert.NotNull(allocation);

        // Assemble the relay candidate send path over the stream transport. The transport carries control
        // responses back into the transactor; the send path's raw-send and the transactor's send both write to
        // the stream (targeted send collapses to a stream write — one connection to the server).
        TurnControlTransactor transactor = null!;
        await using var transport = new StreamRelayMediaTransport(
            stream, host.LocalEndPoint,
            onRelayControl: m => transactor.OnControlDatagram(m),
            onInboundMedia: _ => { },
            NullLogger<StreamRelayMediaTransport>.Instance);
        transactor = new TurnControlTransactor(
            codec, (bytes, ct) => transport.SendControlAsync(bytes, ct).AsTask(),
            NullLogger<TurnControlTransactor>.Instance);
        var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);

        // Record what the send path writes so we can confirm a Send indication was framed over the stream,
        // while still forwarding it to the real transport.
        var written = new List<StunMessage>();
        var sendPath = new TurnRelayCandidateSendPath(
            new TurnRelayIndicationChannel(codec, host.LocalEndPoint), control, allocation!.EffectiveCredentials,
            (datagram, _, ct) =>
            {
                if (codec.Decode(datagram.ToArray()) is { } m)
                    lock (written) written.Add(m);
                return transport.SendControlAsync(datagram, ct);
            },
            NullLogger<TurnRelayCandidateSendPath>.Instance);
        transport.Start();

        var peer = new IPEndPoint(IPAddress.Parse("198.51.100.30"), 50000);
        var check = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        // Completes without throwing → the permission round-trip succeeded over the stream against the real
        // server, and the Send indication was written.
        await sendPath.SendAsync(check, peer, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        lock (written)
        {
            Assert.Contains(written, m =>
                m.MessageClass == StunMessageClass.Indication &&
                (TurnMessageMethod)(ushort)m.MessageMethod == TurnMessageMethod.Send);
        }
    }
}
