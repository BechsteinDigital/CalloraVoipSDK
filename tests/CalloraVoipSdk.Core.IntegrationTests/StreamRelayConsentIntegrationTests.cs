using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The stream relay candidate in the consent loop (ADR-073 slice 4b, #240): a connectivity/consent check sent
/// over the stream and answered as a relayed Data indication (RFC 8656 §10) confirms the check through
/// <see cref="IceMediaConsentSession.OnStunResponse"/>. This closes the two-transport question the ADR raised —
/// the relay candidate's send and receive live on the stream, not the shared media socket, yet the consent
/// transaction matcher is send-path agnostic (it correlates by transaction id), so the stream candidate plugs
/// into the same consent machinery by feeding the unwrapped indication into <c>OnStunResponse</c>.
/// </summary>
public sealed class StreamRelayConsentIntegrationTests
{
    private static readonly IPEndPoint RelayServer = new(IPAddress.Parse("203.0.113.7"), 3478);
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("198.51.100.30"), 50000);

    [Fact]
    public async Task A_consent_check_over_the_stream_is_confirmed_by_a_relayed_data_indication()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await accept;
        var serverStream = server.GetStream();

        var codec = new StunMessageCodec();
        IceMediaConsentSession session = null!;

        // The transport carries the relay candidate's receive half: a relayed Data indication is unwrapped and
        // its inner payload (the check response) is fed straight into the consent matcher.
        await using var transport = new StreamRelayMediaTransport(
            client.GetStream(), RelayServer,
            onRelayControl: _ => { },
            onInboundMedia: _ => { },
            NullLogger<StreamRelayMediaTransport>.Instance);
        transport.SetIndicationRelay(
            new TurnRelayIndicationChannel(codec, RelayServer),
            (_, inner) => session.OnStunResponse(inner));
        transport.Start();

        // The "server + peer": read each consent check off the stream and answer it as a Data indication that
        // echoes the check's transaction id (bytes 8..20), which is all OnStunResponse correlates on.
        using var serverCts = new CancellationTokenSource();
        var serverLoop = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                var frame = await TurnStreamFramer.ReadFrameAsync(serverStream, serverCts.Token);
                if (frame is null) return;
                if (frame.IsChannelData) continue;
                var response = new byte[20];
                frame.Payload.AsSpan(8, 12).CopyTo(response.AsSpan(8));   // echo the transaction id
                var indication = DataIndication(codec, Peer, response);
                await serverStream.WriteAsync(indication, serverCts.Token);
                await serverStream.FlushAsync(serverCts.Token);
            }
        });

        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var lost = false;
        var threeAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;

        // The consent check's send path is the stream: write the check raw to the server, which answers it as a
        // relayed indication. (The permission/Send-indication framing is exercised in slice 3; here the subject
        // is the receive-to-consent leg, so the check goes out plainly and comes back relayed.)
        session = new IceMediaConsentSession(
            codec,
            sendRaw: async (datagram, _, ct) =>
            {
                if (Interlocked.Increment(ref sent) == 3) threeAnswered.TrySetResult();
                await transport.SendControlAsync(datagram, ct);
            },
            Peer,
            localUfrag: "localU", remoteUfrag: "peerU", remotePassword: "peerPwd0123456789abcde",
            priority: 1u, controlling: true, tieBreaker: 1,
            onConsentLost: () => lost = true,
            loggerFactory: NullLoggerFactory.Instance,
            policy: new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5)),
            checkTimeout: TimeSpan.FromSeconds(1),
            utcNow: () => clock.Now,
            delay: (_, ct) => { clock.Advance(TimeSpan.FromSeconds(1)); return Task.Delay(1, ct); },
            nextRandom: () => 0.5);

        session.Start();
        await threeAnswered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync();
        serverCts.Cancel();
        try { await serverLoop; } catch { /* cancelled */ }
        server.Dispose(); client.Dispose(); listener.Stop();

        Assert.False(lost, "consent must stay fresh while checks are answered over the stream relay");
        Assert.True(sent >= 3);
    }

    private static byte[] DataIndication(StunMessageCodec codec, IPEndPoint peer, byte[] innerPayload)
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Data,
            TransactionId = transactionId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peer }, transactionId),
                TurnAttributeMapper.Encode(new TurnDataAttribute { Value = innerPayload }),
            ],
        });
    }

    private sealed class MutableClock
    {
        private long _ticks;
        public MutableClock(DateTimeOffset start) => _ticks = start.UtcTicks;
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
