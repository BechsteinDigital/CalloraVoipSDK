using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A degenerate simulcast configuration — a single distinct RID in a direction — is not simulcast: the SDP
/// builder drops a lone <c>a=rid</c> (RFC 8853; Chrome strips it, #369). Rather than degrade silently, the
/// peer surfaces it once as a warning at the point the configuration enters it (HARD-G3: a reduction is
/// observable, never silent). Two or more distinct RIDs, or none, are quiet.
/// </summary>
public sealed class WebRtcSimulcastConfigWarningTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    [Fact]
    public async Task A_single_send_layer_logs_one_warning()
    {
        var log = new CapturingLogger();
        await using var peer = BuildPeer(log, sendRids: ["only"], recvRids: []);

        var warnings = SimulcastWarnings(log);
        Assert.Single(warnings);
        Assert.Contains("single simulcast send RID", warnings[0]);
    }

    [Fact]
    public async Task A_single_recv_layer_logs_one_warning()
    {
        var log = new CapturingLogger();
        await using var peer = BuildPeer(log, sendRids: [], recvRids: ["only"]);

        var warnings = SimulcastWarnings(log);
        Assert.Single(warnings);
        Assert.Contains("single simulcast receive RID", warnings[0]);
    }

    [Fact]
    public async Task Two_distinct_layers_are_quiet()
    {
        var log = new CapturingLogger();
        await using var peer = BuildPeer(log, sendRids: ["hi", "lo"], recvRids: ["hi", "lo"]);

        Assert.Empty(SimulcastWarnings(log));
    }

    [Fact]
    public async Task No_simulcast_is_quiet()
    {
        var log = new CapturingLogger();
        await using var peer = BuildPeer(log, sendRids: [], recvRids: []);

        Assert.Empty(SimulcastWarnings(log));
    }

    [Fact]
    public async Task Repeated_ids_collapse_to_one_and_warn()
    {
        // Two entries, one distinct id — the SDP builder dedups to a single layer, which is not simulcast.
        var log = new CapturingLogger();
        await using var peer = BuildPeer(log, sendRids: ["hi", "hi"], recvRids: []);

        Assert.Single(SimulcastWarnings(log));
    }

    [Fact]
    public async Task An_added_track_with_a_single_layer_warns()
    {
        var log = new CapturingLogger();
        await using var peer = BuildPeer(log, sendRids: [], recvRids: []);
        Assert.Empty(SimulcastWarnings(log)); // clean config: nothing yet

        peer.AddVideoTrack(new WebRtcAddedVideoTrack { Codecs = H264, SimulcastSendRids = ["only"] });

        var warnings = SimulcastWarnings(log);
        Assert.Single(warnings);
        Assert.Contains("single simulcast send RID", warnings[0]);
    }

    private static IReadOnlyList<string> SimulcastWarnings(CapturingLogger log) =>
        log.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("simulcast", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToArray();

    private static WebRtcPeerConnection BuildPeer(
        CapturingLogger log, IReadOnlyList<string> sendRids, IReadOnlyList<string> recvRids)
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        return new WebRtcPeerConnection(
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = Pcmu,
                VideoTracks =
                [
                    new SdpVideoMediaOptions
                    {
                        Port = 0,
                        Codecs = H264,
                        SimulcastSendRids = sendRids,
                        SimulcastRecvRids = recvRids,
                    },
                ],
                Dtls = new SdpDtlsParameters { Algorithm = cert.Fingerprint.Algorithm, Fingerprint = cert.Fingerprint.Value },
                Ice = new SdpIceParameters { Ufrag = "test", Pwd = "testpassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert,
            new CapturingLoggerFactory(log));
    }
}
