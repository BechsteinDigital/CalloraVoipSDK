using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-007: RFC 4733 telephone-event (DTMF) end to end on the active WebRTC BUNDLE path. Covers negotiation
/// (the event payload type is carried into the audio track config, or null when absent), the outbound send
/// (a telephone-event burst on the audio SSRC over the real DTLS-keyed loopback transport), and the inbound
/// receive (reassembled to a single <c>DtmfReceived</c>, never surfaced as audio).
/// </summary>
public sealed class BundledMediaSessionDtmfTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;    // PCMU
    private const byte TelephoneEventPt = 101;  // RFC 4733 dynamic PT

    // ── negotiation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Negotiation_carries_telephone_event_so_dtmf_can_be_sent()
    {
        var (offer, answer) = Exchange(withTelephoneEvent: true);

        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(withTelephoneEvent: true), Handshaker(),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var lease = session;

        // telephone-event was negotiated → SendDtmf resolves the payload type and does not reject the call.
        // The send is fail-closed-suppressed (no DTLS key on this un-started session), so it completes without
        // throwing rather than emitting plaintext.
        await session!.SendDtmfAsync(toneCode: 5, durationMs: 100);
    }

    [Fact]
    public async Task Without_telephone_event_send_dtmf_is_a_clean_error()
    {
        var (offer, answer) = Exchange(withTelephoneEvent: false);

        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(withTelephoneEvent: false), Handshaker(),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var lease = session;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session!.SendDtmfAsync(toneCode: 5));
    }

    [Fact]
    public async Task Send_dtmf_rejects_an_out_of_range_tone_code()
    {
        var (offer, answer) = Exchange(withTelephoneEvent: true);
        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(withTelephoneEvent: true), Handshaker(),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);
        await using var lease = session;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session!.SendDtmfAsync(toneCode: 16));
    }

    // ── send + receive end to end ──────────────────────────────────────────────

    [Fact]
    public async Task Dtmf_flows_over_a_dtls_keyed_bundle_and_is_not_delivered_as_audio()
    {
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        var (client, server) = CreatePair(certA, certB);
        await using var clientLease = client;
        await using var serverLease = server;

        var dtmf = new TaskCompletionSource<(byte Tone, int Duration)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioSeen = 0;
        server.DtmfReceived += (tone, duration) => dtmf.TrySetResult((tone, duration));
        // A telephone-event packet must NOT reach the audio sink — record if any audio arrives so we can prove
        // the event stream was consumed as DTMF, not forwarded as an audio frame.
        server.AudioReceived += _ => Interlocked.Increment(ref audioSeen);

        await server.StartAsync();
        await client.StartAsync();

        // Media is suppressed until the shared DTLS handshake keys the transport; keep sending the tone so the
        // first fully reassembled event to land proves the keyed bundle carries DTMF end to end.
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!dtmf.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await client.SendDtmfAsync(toneCode: 7, durationMs: 120);
            await Task.Delay(40, overall.Token);
        }

        var (tone, duration) = await dtmf.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, tone);
        // 120 ms on the 8 kHz event clock reassembles back to ~120 ms (floored at the RFC 4733 minimum).
        Assert.InRange(duration, 120, 130);

        // The telephone-event burst was demultiplexed as DTMF, never handed to the audio sink.
        Assert.Equal(0, Volatile.Read(ref audioSeen));

        // Wire evidence: the sender actually emitted RTP (the DTMF burst), and the receiver decoded it.
        Assert.True(client.SnapshotStats().PacketsSent > 0, "client should have sent telephone-event RTP");
        Assert.True(server.SnapshotStats().PacketsReceived > 0, "server should have received RTP");
    }

    [Fact]
    public async Task Ordinary_audio_still_flows_alongside_a_telephone_event_track()
    {
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        var (client, server) = CreatePair(certA, certB);
        await using var clientLease = client;
        await using var serverLease = server;

        var audio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dtmfSeen = 0;
        server.AudioReceived += p => audio.TrySetResult(p.Payload.ToArray());
        server.DtmfReceived += (_, _) => Interlocked.Increment(ref dtmfSeen);

        await server.StartAsync();
        await client.StartAsync();

        var audioPayload = new byte[] { 9, 8, 7, 6 };
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!audio.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await client.SendAudioAsync(audioPayload);
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(audioPayload, await audio.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // Plain audio on the audio PT is never misread as a telephone-event.
        Assert.Equal(0, Volatile.Read(ref dtmfSeen));
    }

    // ── inbound reassembly semantics (injected, no socket) ─────────────────────

    [Fact]
    public async Task A_repeated_end_of_event_packet_of_one_burst_yields_exactly_one_dtmf()
    {
        await using var session = SingleSession();
        var events = new List<(byte Tone, int Duration)>();
        session.DtmfReceived += (tone, duration) => events.Add((tone, duration));

        const uint ssrc = 0x11223344;
        const uint timestamp = 5000;
        // One burst, tone 4: a mid-event packet then TWO identical end-of-event packets (the RFC 4733 §2.5.1.4
        // reliability retransmission SendDtmfAsync emits). The completion latch must fire DtmfReceived once only.
        session.InjectInboundAudioForTest(TelephoneEvent(ssrc, timestamp, tone: 4, endOfEvent: false, durationRtpUnits: 400, marker: true));
        session.InjectInboundAudioForTest(TelephoneEvent(ssrc, timestamp, tone: 4, endOfEvent: true, durationRtpUnits: 800));
        session.InjectInboundAudioForTest(TelephoneEvent(ssrc, timestamp, tone: 4, endOfEvent: true, durationRtpUnits: 800));

        Assert.Single(events);
        Assert.Equal(4, events[0].Tone);
    }

    [Fact]
    public async Task Two_sequential_tones_with_distinct_timestamps_yield_two_dtmf_events()
    {
        await using var session = SingleSession();
        var events = new List<(byte Tone, int Duration)>();
        session.DtmfReceived += (tone, duration) => events.Add((tone, duration));

        const uint ssrc = 0x55667788;
        // Tone 2 on one RTP timestamp, then tone 9 on a NEW timestamp: a distinct timestamp starts a fresh event
        // (the reassembly key is SSRC+timestamp+tone), so exactly two tones are surfaced, in order.
        session.InjectInboundAudioForTest(TelephoneEvent(ssrc, timestamp: 1000, tone: 2, endOfEvent: false, durationRtpUnits: 200, marker: true));
        session.InjectInboundAudioForTest(TelephoneEvent(ssrc, timestamp: 1000, tone: 2, endOfEvent: true, durationRtpUnits: 400));
        session.InjectInboundAudioForTest(TelephoneEvent(ssrc, timestamp: 2000, tone: 9, endOfEvent: false, durationRtpUnits: 200, marker: true));
        session.InjectInboundAudioForTest(TelephoneEvent(ssrc, timestamp: 2000, tone: 9, endOfEvent: true, durationRtpUnits: 400));

        Assert.Equal(2, events.Count);
        Assert.Equal(2, events[0].Tone);
        Assert.Equal(9, events[1].Tone);
    }

    // Builds a telephone-event RTP packet on the negotiated telephone-event PT for the audio SSRC.
    private static Core.Infrastructure.Rtp.Packets.RtpPacket TelephoneEvent(
        uint ssrc, uint timestamp, byte tone, bool endOfEvent, ushort durationRtpUnits, bool marker = false)
        => new()
        {
            PayloadType = TelephoneEventPt,
            Marker = marker,
            Ssrc = ssrc,
            Timestamp = timestamp,
            Payload = RtpTelephoneEventCodec.BuildPayload(tone, endOfEvent, durationRtpUnits),
        };

    // A single un-started session (its socket binds but no transport runs) — enough to drive the injected
    // inbound reassembly path without a peer.
    private static BundledMediaSession SingleSession()
    {
        for (var attempt = 1; ; attempt++)
        {
            var localPort = FreeUdpPort();
            var remotePort = FreeUdpPort();
            try
            {
                return new BundledMediaSession(
                    Options(localPort, remotePort, isClient: true, DtlsCertificate.GenerateEcdsaP256().Fingerprint,
                        controlling: true, localUfrag: "cli0", localPwd: ClientPwd, remoteUfrag: "srv0", remotePwd: ServerPwd),
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
                    DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);
            }
            catch (SocketException) when (attempt < 8)
            {
            }
        }
    }

    // ── harness ────────────────────────────────────────────────────────────────

    private const string ClientPwd = "clienticepassword1234567890";
    private const string ServerPwd = "servericepassword1234567890";

    private static (BundledMediaSession Client, BundledMediaSession Server) CreatePair(
        DtlsCertificate certA, DtlsCertificate certB)
    {
        for (var attempt = 1; ; attempt++)
        {
            var portA = FreeUdpPort();
            var portB = FreeUdpPort();
            BundledMediaSession? client = null;
            try
            {
                client = new BundledMediaSession(
                    Options(portA, portB, isClient: true, certB.Fingerprint, controlling: true,
                        localUfrag: "cli0", localPwd: ClientPwd, remoteUfrag: "srv0", remotePwd: ServerPwd),
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certA, NullLoggerFactory.Instance);
                var server = new BundledMediaSession(
                    Options(portB, portA, isClient: false, certA.Fingerprint, controlling: false,
                        localUfrag: "srv0", localPwd: ServerPwd, remoteUfrag: "cli0", remotePwd: ClientPwd),
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certB, NullLoggerFactory.Instance);
                return (client, server);
            }
            catch (SocketException) when (attempt < 8)
            {
                client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static BundledMediaSessionOptions Options(
        int localPort, int remotePort, bool isClient, DtlsFingerprint remoteFingerprint, bool controlling,
        string localUfrag, string localPwd, string remoteUfrag, string remotePwd)
    {
        var remote = new IPEndPoint(IPAddress.Loopback, remotePort);
        return new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
            RemoteEndPoint = remote,
            MidExtensionId = MidExtId,
            Audio = new BundledTrackConfig
            {
                Mid = "audio",
                Ssrc = 0x0A0A0A0A,
                PayloadType = AudioPayloadType,
                SamplesPerPacket = 160,
                TelephoneEventPayloadType = TelephoneEventPt,
                TelephoneEventClockRate = 8000,
            },
            DtlsIsClient = isClient,
            RemoteFingerprint = remoteFingerprint,
            Ice = new IceMediaParameters(
                remote, IceEnabled: true, IceControlling: controlling,
                LocalIceUfrag: localUfrag, LocalIcePwd: localPwd,
                RemoteIceUfrag: remoteUfrag, RemoteIcePwd: remotePwd),
        };
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    // ── negotiation harness ──────────────────────────────────────────────────────

    private static IReadOnlyList<SdpCodecDefinition> AudioCodecs(bool withTelephoneEvent)
    {
        var codecs = new List<SdpCodecDefinition>
        {
            new() { PayloadType = 0, Name = "PCMU", ClockRate = 8000 },
        };
        if (withTelephoneEvent)
            codecs.Add(new SdpCodecDefinition { PayloadType = TelephoneEventPt, Name = "telephone-event", ClockRate = 8000 });
        return codecs;
    }

    private static (SdpSessionDescription Offer, SdpSessionDescription Answer) Exchange(bool withTelephoneEvent)
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        var codecs = AudioCodecs(withTelephoneEvent);

        var offer = negotiator.CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), codecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = OfferDtls(), Ice = OfferIce() });

        var answer = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), codecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = AnswerDtls(), Ice = AnswerIce() }).Answer!;

        return (offer, answer);
    }

    private static WebRtcPeerOptions PeerOptions(bool withTelephoneEvent) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        AudioCodecs = AudioCodecs(withTelephoneEvent),
        Dtls = AnswerDtls(),
        Ice = AnswerIce(),
    };

    private static IDtlsSrtpHandshaker Handshaker() => new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance);

    private static SdpDtlsParameters OfferDtls() => new() { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "actpass" };
    private static SdpDtlsParameters AnswerDtls() => new() { Algorithm = "sha-256", Fingerprint = "11:22:33" };
    private static SdpIceParameters OfferIce() => new() { Ufrag = "remoteU", Pwd = "remotepassword1234567890" };
    private static SdpIceParameters AnswerIce() => new() { Ufrag = "localU", Pwd = "localpassword1234567890" };
}
