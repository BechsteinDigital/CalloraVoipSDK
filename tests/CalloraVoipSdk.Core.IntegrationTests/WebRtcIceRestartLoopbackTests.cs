using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE restart end to end on a connected WebRTC peer (#226): two agents run real ICE over their BUNDLE
/// transports, key DTLS-SRTP, exchange audio — and then the far side rotates its ICE credentials, exactly as a
/// browser does when its network changes. The peer answers the restart offer without being disposed, and media
/// keeps flowing.
/// <para>
/// The two tests here divide the claim deliberately, because continuing media is <b>not</b> by itself evidence
/// that ICE restarted: once a pair is latched and SRTP is keyed, RTP keeps flowing whatever ICE does — verified
/// by mutation, a build that rotates the credentials but never swaps the agent still passes a media-only
/// assertion. So the first test claims only what it can see (nothing was torn down: same socket, same SRTP
/// context, audio still decrypts) and the second one probes the wire for what actually changed — the peer now
/// answers checks authenticated with the rotated credentials, and no longer answers the retired ones.
/// </para>
/// </summary>
public sealed class WebRtcIceRestartLoopbackTests
{
    private const byte AudioPayloadType = 0;
    private const uint CounterpartSsrc = 0x0C0C0C0C;
    private const string PeerUfrag = "peerU";
    private const string PeerPwd = "peerpassword1234567890";
    private const string CounterpartUfrag = "cpUa";
    private const string CounterpartPwd = "cppassword123456789001";
    private const string RestartedCounterpartUfrag = "cpUb";
    private const string RestartedCounterpartPwd = "cprestartedpassword0001";

    [Fact]
    public async Task Media_flows_again_after_the_far_side_restarts_ice()
    {
        var peerCert = DtlsCertificate.GenerateEcdsaP256();
        var counterpartCert = DtlsCertificate.GenerateEcdsaP256();

        var (peer, counterpart, peerPort) = await ConnectPairAsync(peerCert, counterpartCert);
        await using var peerLease = peer;
        await using var counterpartLease = counterpart;

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += state => { if (state == WebRtcConnectionState.Connected) connected.TrySetResult(); };

        var beforeRestart = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterRestart = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectAfterRestart = false;
        peer.AudioReceived += (payload, _) =>
        {
            if (Volatile.Read(ref expectAfterRestart))
                afterRestart.TrySetResult(payload);
            else
                beforeRestart.TrySetResult(payload);
        };

        await peer.StartAsync();
        await counterpart.StartAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var before = new byte[] { 1, 2, 3, 4 };
        await PumpUntilAsync(counterpart, before, beforeRestart.Task);
        Assert.Equal(before, await beforeRestart.Task);

        // ── the restart ──────────────────────────────────────────────────────────
        // The far side rotates its ICE credentials and re-offers. The peer must answer with fresh credentials of
        // its own (RFC 8445 §9.1.1.1) and keep everything below ICE.
        var socketBefore = peer.LocalMediaEndPoint!;
        var answer = await peer.SetRemoteDescriptionAsync(
            Offer(counterpart.LocalEndPoint.Port, counterpartCert, RestartedCounterpartUfrag, RestartedCounterpartPwd));

        var peerAudio = new SdpSessionParser().Parse(answer)
            .Media.First(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(PeerUfrag, peerAudio.IceUfrag);
        Assert.Equal(socketBefore, peer.LocalMediaEndPoint);
        Assert.NotEqual(WebRtcConnectionState.Failed, peer.State);

        // The far side adopts both halves and restarts its own agent — the other end of the same exchange.
        var peerEndPoint = new IPEndPoint(IPAddress.Loopback, peerPort);
        await counterpart.RestartIceAsync(new IceMediaParameters(
            peerEndPoint, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: RestartedCounterpartUfrag, LocalIcePwd: RestartedCounterpartPwd,
            RemoteIceUfrag: peerAudio.IceUfrag, RemoteIcePwd: peerAudio.IcePwd)
        {
            RemoteCandidates = [new IceRemoteCandidate(peerEndPoint, Priority: 100)],
        });

        // Audio sent after the restart still decrypts under the SRTP context established before it — the restart
        // stopped at the ICE layer, and no second handshake ran. (That media flows at all is weaker than it looks;
        // the second test carries the proof that ICE itself moved.)
        Volatile.Write(ref expectAfterRestart, true);
        var after = new byte[] { 9, 8, 7, 6 };
        await PumpUntilAsync(counterpart, after, afterRestart.Task);
        Assert.Equal(after, await afterRestart.Task);

        // Still the same peer — never disposed, never rebuilt.
        Assert.Equal(socketBefore, peer.LocalMediaEndPoint);
        Assert.Equal(WebRtcSignalingState.Stable, peer.SignalingState);
    }

    /// <summary>
    /// The sharp half: after the restart offer is answered, the peer's live socket authenticates inbound
    /// connectivity checks against the <em>rotated</em> credentials and no longer against the retired ones. This
    /// is what distinguishes a restart from a re-offer that merely relabelled the SDP — and it is checked on the
    /// real, connected peer, not on an ICE agent in isolation.
    /// </summary>
    [Fact]
    public async Task After_the_restart_the_peer_answers_the_rotated_credentials_and_not_the_retired_ones()
    {
        var peerCert = DtlsCertificate.GenerateEcdsaP256();
        var counterpartCert = DtlsCertificate.GenerateEcdsaP256();

        var (peer, counterpart, _) = await ConnectPairAsync(peerCert, counterpartCert);
        await using var peerLease = peer;
        await using var counterpartLease = counterpart;

        await peer.StartAsync();
        await counterpart.StartAsync();

        var answer = await peer.SetRemoteDescriptionAsync(
            Offer(counterpart.LocalEndPoint.Port, counterpartCert, RestartedCounterpartUfrag, RestartedCounterpartPwd));
        var peerAudio = new SdpSessionParser().Parse(answer)
            .Media.First(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));

        // Probe the peer's real media socket from a third address, as a peer-reflexive source would.
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var codec = new StunMessageCodec();

        var (fresh, freshTransaction) = IceConsentCheckBuilder.Build(
            codec, localUfrag: RestartedCounterpartUfrag, remoteUfrag: peerAudio.IceUfrag!,
            remotePassword: peerAudio.IcePwd!, priority: 12345u, controlling: true, tieBreaker: 42);
        await probe.SendAsync(fresh, peer.LocalMediaEndPoint!);
        Assert.True(
            await AwaitSuccessResponseAsync(probe, codec, freshTransaction, TimeSpan.FromSeconds(5)),
            "After the restart the peer must answer checks carrying the rotated credentials.");

        var (retired, retiredTransaction) = IceConsentCheckBuilder.Build(
            codec, localUfrag: CounterpartUfrag, remoteUfrag: PeerUfrag, remotePassword: PeerPwd,
            priority: 12345u, controlling: true, tieBreaker: 42);
        await probe.SendAsync(retired, peer.LocalMediaEndPoint!);
        // Not "nothing arrives" — the live agents keep their own traffic on this socket. What must not arrive is
        // an answer to the retired transaction.
        Assert.False(
            await AwaitSuccessResponseAsync(probe, codec, retiredTransaction, TimeSpan.FromSeconds(2)),
            "The retired credentials must no longer be answered after the restart.");
    }

    /// <summary>
    /// #226, local initiation. Same trap as above, one layer up: a rotated ufrag in the <em>offer</em> proves
    /// nothing on its own — it is read from the renegotiator's credential state, so a build that rotates but never
    /// restarts the agent still produces a fresh-looking offer while the live socket keeps answering with the old
    /// password. That is the failure that would strand the far side, because it starts checking against the offer's
    /// credentials as soon as it processes it. So this asserts on the wire: after
    /// <c>CreateIceRestartOfferAsync</c>, the peer's real media socket answers checks authenticated with the
    /// credentials the offer advertises.
    /// </summary>
    [Fact]
    public async Task A_locally_initiated_restart_makes_the_live_socket_answer_the_offered_credentials()
    {
        var peerCert = DtlsCertificate.GenerateEcdsaP256();
        var counterpartCert = DtlsCertificate.GenerateEcdsaP256();

        var (peer, counterpart, _) = await ConnectPairAsync(peerCert, counterpartCert);
        await using var peerLease = peer;
        await using var counterpartLease = counterpart;

        await peer.StartAsync();
        await counterpart.StartAsync();

        var restartOffer = await peer.CreateIceRestartOfferAsync();
        var offered = new SdpSessionParser().Parse(restartOffer)
            .Media.First(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(PeerUfrag, offered.IceUfrag);

        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var codec = new StunMessageCodec();

        var (check, transaction) = IceConsentCheckBuilder.Build(
            codec, localUfrag: CounterpartUfrag, remoteUfrag: offered.IceUfrag!,
            remotePassword: offered.IcePwd!, priority: 12345u, controlling: true, tieBreaker: 42);
        await probe.SendAsync(check, peer.LocalMediaEndPoint!);
        Assert.True(
            await AwaitSuccessResponseAsync(probe, codec, transaction, TimeSpan.FromSeconds(5)),
            "The peer must answer the credentials its own restart offer announces.");

        var (stale, staleTransaction) = IceConsentCheckBuilder.Build(
            codec, localUfrag: CounterpartUfrag, remoteUfrag: PeerUfrag, remotePassword: PeerPwd,
            priority: 12345u, controlling: true, tieBreaker: 42);
        await probe.SendAsync(stale, peer.LocalMediaEndPoint!);
        Assert.False(
            await AwaitSuccessResponseAsync(probe, codec, staleTransaction, TimeSpan.FromSeconds(2)),
            "The credentials used before the restart must no longer be answered.");
    }

    /// <summary>
    /// #226, re-gathering. A restart is triggered by the network changing, and the reflexive address is exactly
    /// what a network change invalidates — so the candidates gathered before it are the ones least likely to
    /// still be right. Gathering used to be refused outright once the peer was started ("the media socket is
    /// owned by the transport's receive loop"), which left a restarted peer offering only its pre-restart view.
    /// It now runs over the live transport, and the socket — the thing DTLS and SRTP are keyed to — is untouched.
    /// </summary>
    [Fact]
    public async Task Re_gathering_after_a_restart_yields_a_fresh_reflexive_candidate_over_the_live_transport()
    {
        var peerCert = DtlsCertificate.GenerateEcdsaP256();
        var counterpartCert = DtlsCertificate.GenerateEcdsaP256();

        using var stunServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var stunEndPoint = (IPEndPoint)stunServer.Client.LocalEndPoint!;
        var codec = new StunMessageCodec();
        using var serverLife = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serving = ServeStunAsync(stunServer, codec, serverLife.Token);

        var (peer, counterpart, _) = await ConnectPairAsync(peerCert, counterpartCert, stunEndPoint);
        await using var peerLease = peer;
        await using var counterpartLease = counterpart;

        var gathered = new List<string>();
        peer.LocalIceCandidateDiscovered += candidate => { lock (gathered) gathered.Add(candidate); };

        await peer.StartAsync();
        await counterpart.StartAsync();
        var socketBefore = peer.LocalMediaEndPoint!;

        await peer.CreateIceRestartOfferAsync();
        await peer.GatherCandidatesAsync();          // used to throw once started

        string[] snapshot;
        lock (gathered) snapshot = [.. gathered];
        var reflexive = Assert.Single(snapshot, c => c.Contains("srflx", StringComparison.Ordinal));
        // The fake server reports the source it saw, so the candidate carries the live media port — proof the
        // probe rode the running transport rather than a socket of its own.
        Assert.Contains($" {socketBefore.Port} ", reflexive, StringComparison.Ordinal);
        Assert.Equal(socketBefore, peer.LocalMediaEndPoint);

        await serverLife.CancelAsync();
        await serving;
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    // A minimal STUN server: answers each Binding request with XOR-MAPPED-ADDRESS of the source it saw.
    private static async Task ServeStunAsync(UdpClient server, StunMessageCodec codec, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var received = await server.ReceiveAsync(ct);
                if (codec.Decode(received.Buffer) is not { MessageMethod: StunMessageMethod.Binding } request)
                    continue;

                var bytes = codec.Encode(new StunMessage
                {
                    MessageClass = StunMessageClass.SuccessResponse,
                    MessageMethod = StunMessageMethod.Binding,
                    TransactionId = request.TransactionId,
                    Attributes = [new XorMappedAddressAttribute { EndPoint = received.RemoteEndPoint }],
                });
                await server.SendAsync(bytes, bytes.Length, received.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    // Drains the probe socket until a success response for this transaction arrives or the deadline passes.
    private static async Task<bool> AwaitSuccessResponseAsync(
        UdpClient probe, StunMessageCodec codec, byte[] transactionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var receive = probe.ReceiveAsync();
            var completed = await Task.WhenAny(receive, Task.Delay(deadline - DateTime.UtcNow));
            if (completed != receive)
                return false;

            var message = codec.Decode(receive.Result.Buffer);
            if (message is { MessageClass: StunMessageClass.SuccessResponse, MessageMethod: StunMessageMethod.Binding }
                && message.TransactionId.AsSpan().SequenceEqual(transactionId))
            {
                return true;
            }
        }

        return false;
    }

    // Sends payload on the counterpart until the awaited receive completes or the deadline passes. RTP is
    // unreliable and the restart re-runs checks, so a single datagram is not a fair test of "media flows".
    private static async Task PumpUntilAsync(BundledMediaSession counterpart, byte[] payload, Task awaited)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!awaited.IsCompleted)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await counterpart.SendAudioAsync(payload);
            await Task.Delay(20, deadline.Token);
        }
    }

    // Both ends need the other's port before construction, so ports are pre-allocated; retry on a bind race.
    private static async Task<(WebRtcPeerConnection Peer, BundledMediaSession Counterpart, int PeerPort)> ConnectPairAsync(
        DtlsCertificate peerCert, DtlsCertificate counterpartCert, IPEndPoint? stunServer = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var peerPort = FreeUdpPort();
            var counterpartPort = FreeUdpPort();
            WebRtcPeerConnection? peer = null;
            try
            {
                peer = BuildPeer(peerPort, peerCert, stunServer);
                var answer = await peer.SetRemoteDescriptionAsync(
                    Offer(counterpartPort, counterpartCert, CounterpartUfrag, CounterpartPwd));
                var counterpart = BuildCounterpart(counterpartPort, peerPort, peerCert.Fingerprint, counterpartCert, answer);
                return (peer, counterpart, peerPort);
            }
            catch (SocketException) when (attempt < 8)
            {
                if (peer is not null)
                    await peer.DisposeAsync();
            }
        }
    }

    private static WebRtcPeerConnection BuildPeer(int localPort, DtlsCertificate cert, IPEndPoint? stunServer = null) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                AudioCodecs = [new SdpCodecDefinition { PayloadType = AudioPayloadType, Name = "PCMU", ClockRate = 8000 }],
                Dtls = new SdpDtlsParameters { Algorithm = cert.Fingerprint.Algorithm, Fingerprint = cert.Fingerprint.Value },
                Ice = new SdpIceParameters { Ufrag = PeerUfrag, Pwd = PeerPwd },
                IceServers = stunServer is null
                    ? []
                    : [new IceServerConfiguration
                        {
                            Type = IceServerType.Stun,
                            Host = stunServer.Address.ToString(),
                            Port = stunServer.Port,
                        }],
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);

    // A remote WebRTC offer (BUNDLE + DTLS + ICE, audio only). The ICE credentials are parameterised so the same
    // builder produces both the initial offer and the restart re-offer.
    private static string Offer(int counterpartPort, DtlsCertificate counterpartCert, string ufrag, string pwd) =>
        new SdpSessionSerializer().Serialize(new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, counterpartPort),
            [new SdpCodecDefinition { PayloadType = AudioPayloadType, Name = "PCMU", ClockRate = 8000 }],
            SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters
                {
                    Algorithm = counterpartCert.Fingerprint.Algorithm,
                    Fingerprint = counterpartCert.Fingerprint.Value,
                    Setup = "actpass",
                },
                Ice = new SdpIceParameters { Ufrag = ufrag, Pwd = pwd },
            }));

    // The far side: a raw session standing in for a browser offerer, with ICE ON so it really answers the peer's
    // checks and drives nomination as the controlling agent (RFC 8445 §6.1.1).
    private static BundledMediaSession BuildCounterpart(
        int localPort, int peerPort, DtlsFingerprint peerFingerprint, DtlsCertificate counterpartCert, string answerSdp)
    {
        var peerAudio = new SdpSessionParser().Parse(answerSdp)
            .Media.First(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));
        // Take the DTLS role opposite the peer's negotiated a=setup.
        var counterpartIsClient = !string.Equals(peerAudio.DtlsSetup, "active", StringComparison.OrdinalIgnoreCase);

        var peerEndPoint = new IPEndPoint(IPAddress.Loopback, peerPort);
        return new BundledMediaSession(
            new BundledMediaSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = peerEndPoint,
                MidExtensionId = 1, // the offer assigned sdes:mid id 1
                Audio = new BundledTrackConfig { Mid = "audio", Ssrc = CounterpartSsrc, PayloadType = AudioPayloadType, SamplesPerPacket = 160 },
                DtlsIsClient = counterpartIsClient,
                RemoteFingerprint = peerFingerprint,
                Ice = new IceMediaParameters(
                    peerEndPoint, IceEnabled: true, IceControlling: true,
                    LocalIceUfrag: CounterpartUfrag, LocalIcePwd: CounterpartPwd,
                    RemoteIceUfrag: peerAudio.IceUfrag, RemoteIcePwd: peerAudio.IcePwd)
                {
                    RemoteCandidates = [new IceRemoteCandidate(peerEndPoint, Priority: 100)],
                },
            },
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), counterpartCert, NullLoggerFactory.Instance);
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
