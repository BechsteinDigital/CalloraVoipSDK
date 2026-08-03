using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end multi-track audio (4.7.0 N-audio): an offerer that adds a second audio track (AddAudioTrack →
/// numeric-MID multi-track offer) connects to an answerer over real DTLS-SRTP, and BOTH audio streams flow —
/// the primary audio (mid-less <c>SendAudioAsync</c>/<c>AudioReceived</c>) uninterrupted alongside the added
/// track (mid-tagged <c>SendAudioTrackFrameAsync</c>/<c>AudioTrackFrameReceived</c>). The added stream reaches
/// the answerer tagged with its own MID and payload, proving the whole N-audio send/receive path over the wire
/// (not just the SDP). This is the audio pendant to <see cref="WebRtcMultiTrackPeerToPeerTests"/>.
/// </summary>
public sealed class WebRtcMultiTrackAudioPeerToPeerTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    [Fact]
    public async Task Primary_and_added_audio_both_flow_the_added_arriving_tagged_with_its_mid()
    {
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);

        // Capture, at the answerer, the primary audio (mid-less) and the added track's audio (tagged MID "1").
        var sync = new object();
        byte[]? primaryPayload = null;
        byte[]? addedPayload = null;
        answerer.AudioReceived += (payload, _) =>
        {
            lock (sync) primaryPayload ??= payload;
        };
        answerer.AudioTrackFrameReceived += (mid, payload, _) =>
        {
            if (mid == "1") lock (sync) addedPayload ??= payload;
        };

        await offerer.StartAsync();
        await answerer.StartAsync();
        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        // Distinct payloads per stream so a mis-routed frame is caught, not just presence. G.711 µ-law payloads.
        var primary = Payload(0x11, 160);
        var added = Payload(0x77, 160);

        var timestamp = 8000u;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            lock (sync)
                if (primaryPayload is not null && addedPayload is not null) break;
            overall.Token.ThrowIfCancellationRequested();
            // Primary via the mid-less send seam (cursor-clocked), the added track via the mid-tagged one with an
            // explicit RTP timestamp threaded to the wire (Option B) — proving both stream paths flow at once.
            await offerer.SendAudioAsync(primary);
            await offerer.SendAudioTrackFrameAsync("1", added, timestamp);
            timestamp += 160;
            await Task.Delay(20, overall.Token);
        }

        byte[] gotPrimary, gotAdded;
        lock (sync) { gotPrimary = primaryPayload!; gotAdded = addedPayload!; }

        // Each stream routed to its own path with the right payload: the mid-less send reached the primary
        // AudioReceived, the mid-tagged send reached the added track's AudioTrackFrameReceived, and the two
        // payloads did not cross — proving the sends address distinct wire streams (each its own SSRC, RFC 3550 §8.1).
        Assert.Equal(Convert.ToBase64String(primary), Convert.ToBase64String(gotPrimary));
        Assert.Equal(Convert.ToBase64String(added), Convert.ToBase64String(gotAdded));
        Assert.NotEqual(Convert.ToBase64String(gotPrimary), Convert.ToBase64String(gotAdded));
    }

    [Fact]
    public async Task Inbound_added_audio_surfaces_the_senders_rtp_timestamp_not_zero()
    {
        // ADR-012 follow-up: inbound audio must surface the sender's RTP timestamp all the way to the
        // receive event, not drop it to 0/null. An SFU forwards that timestamp downstream; a stream whose
        // every timestamp is 0 is unplayable — the browser jitter buffer / Opus decoder needs a monotonic
        // clock. Deterministic on the added track, whose send stamps an explicit timestamp on the wire.
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);

        var sync = new object();
        uint? gotTimestamp = null;
        answerer.AudioTrackFrameReceived += (mid, _, rtpTimestamp) =>
        {
            if (mid == "1") lock (sync) gotTimestamp ??= rtpTimestamp;
        };

        await offerer.StartAsync();
        await answerer.StartAsync();
        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        const uint sentTimestamp = 424_242u;
        var added = Payload(0x77, 160);
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            lock (sync)
                if (gotTimestamp is not null) break;
            overall.Token.ThrowIfCancellationRequested();
            await offerer.SendAudioTrackFrameAsync("1", added, sentTimestamp);
            await Task.Delay(20, overall.Token);
        }

        // The exact RTP timestamp the sender stamped reaches the receive event — no longer dropped to 0.
        Assert.Equal(sentTimestamp, gotTimestamp);
    }

    // ── harness (mirrors WebRtcMultiTrackPeerToPeerTests) ──────────────────────

    private static Task Connected(WebRtcPeerConnection peer)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += state => { if (state == WebRtcConnectionState.Connected) tcs.TrySetResult(); };
        return tcs.Task;
    }

    private static async Task<(WebRtcPeerConnection Offerer, WebRtcPeerConnection Answerer)> ConnectPeersAsync()
    {
        var offererCert = DtlsCertificate.GenerateEcdsaP256();
        var answererCert = DtlsCertificate.GenerateEcdsaP256();

        for (var attempt = 1; ; attempt++)
        {
            var offererPort = FreeUdpPort();
            var answererPort = FreeUdpPort();
            WebRtcPeerConnection? offerer = null;
            WebRtcPeerConnection? answerer = null;
            try
            {
                offerer = BuildPeer(offererPort, offererCert, "offr");
                answerer = BuildPeer(answererPort, answererCert, "answ");

                // Offerer adds a SECOND audio track (4.7.0): primary audio=0, added audio=1 (before any video).
                var addedMid = offerer.AddAudioTrack(new WebRtcAddedAudioTrack
                {
                    Codecs = Pcmu,
                    Direction = SdpMediaDirection.SendOnly,
                });
                Assert.Equal("1", addedMid);

                var offer = offerer.CreateOffer();
                var answer = await answerer.SetRemoteDescriptionAsync(offer); // binds the answerer's port
                await offerer.SetRemoteDescriptionAsync(answer);              // binds the offerer's port
                return (offerer, answerer);
            }
            catch (SocketException) when (attempt < 8)
            {
                if (offerer is not null) await offerer.DisposeAsync();
                if (answerer is not null) await answerer.DisposeAsync();
            }
        }
    }

    // Audio-only peers (no config video). The answerer mirrors the offered m-lines via the multi-track answer path.
    private static WebRtcPeerConnection BuildPeer(int localPort, DtlsCertificate cert, string iceTag) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                AudioCodecs = Pcmu,
                Dtls = new SdpDtlsParameters { Algorithm = cert.Fingerprint.Algorithm, Fingerprint = cert.Fingerprint.Value },
                Ice = new SdpIceParameters { Ufrag = iceTag, Pwd = iceTag + "password1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);

    private static byte[] Payload(byte seed, int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
            payload[i] = (byte)(seed + (i % 200));
        return payload;
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
