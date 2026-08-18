using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// L4 — #317 slice 2: the receive-simulcast layers the peer confirmed are readable on the public
/// <see cref="IPeerConnection.NegotiatedReceiveSimulcastRids"/> after <see cref="IPeerConnection.SetRemoteDescriptionAsync"/>,
/// so a forwarding layer knows which layers to expect before frames arrive. The SDK does not itself answer
/// with simulcast (that is a browser's job — #228), so the confirming answer here is the SDK peer's real
/// answer with the send-simulcast lines injected, exactly the shape a browser returns.
/// </summary>
public sealed class WebRtcRecvSimulcastSurfaceTests
{
    [Fact]
    public async Task The_confirmed_recv_layers_are_readable_on_the_peer_after_the_answer()
    {
        await using var offerer = RecvSimulcastClient(["hi", "lo"]);
        await using var answerer = PlainVideoClient();
        await using var a = offerer.CreatePeer();
        await using var b = answerer.CreatePeer();

        var offer = a.CreateOffer();
        var plainAnswer = await b.SetRemoteDescriptionAsync(offer);
        await a.SetRemoteDescriptionAsync(InjectSendSimulcast(plainAnswer, ["hi", "lo"]));

        Assert.Equal(["hi", "lo"], a.NegotiatedReceiveSimulcastRids.OrderBy(r => r == "hi" ? 0 : 1));
    }

    [Fact]
    public async Task A_peer_that_answers_plainly_confirms_no_recv_layers()
    {
        // Two SDK peers: the answerer does not simulcast, so nothing is confirmed — the honest empty result,
        // not a phantom set derived from our own offer.
        await using var offerer = RecvSimulcastClient(["hi", "lo"]);
        await using var answerer = PlainVideoClient();
        await using var a = offerer.CreatePeer();
        await using var b = answerer.CreatePeer();

        var offer = a.CreateOffer();
        await a.SetRemoteDescriptionAsync(await b.SetRemoteDescriptionAsync(offer));

        Assert.Empty(a.NegotiatedReceiveSimulcastRids);
    }

    // Injects a=rid send / a=simulcast:send and a RID extmap into a real answer's video section — the shape a
    // browser returns when it accepts a recv-simulcast offer. A non-colliding extmap id keeps the SDP valid.
    private static string InjectSendSimulcast(string answerSdp, IReadOnlyList<string> sendRids)
    {
        var lines = answerSdp.Replace("\r\n", "\n").Split('\n').ToList();
        var videoIdx = lines.FindIndex(l => l.StartsWith("m=video ", StringComparison.Ordinal));
        Assert.True(videoIdx >= 0, "the answer has a video m-line");

        var usedIds = lines
            .Where(l => l.StartsWith("a=extmap:", StringComparison.Ordinal))
            .Select(l => l["a=extmap:".Length..].Split(' ')[0])
            .ToHashSet(StringComparer.Ordinal);
        var ridId = Enumerable.Range(1, 14).First(i => !usedIds.Contains(i.ToString()));

        var inject = new List<string> { $"a=extmap:{ridId} urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id" };
        inject.AddRange(sendRids.Select(r => $"a=rid:{r} send"));
        inject.Add("a=simulcast:send " + string.Join(';', sendRids));
        lines.InsertRange(videoIdx + 1, inject);
        return string.Join("\r\n", lines);
    }

    private static WebRtcClient RecvSimulcastClient(IReadOnlyList<string> recvLayers) => new(new WebRtcConfiguration
    {
        EnableVideo = true,
        VideoCodecs = ["VP8"],
        SimulcastRecvLayers = recvLayers,
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    });

    private static WebRtcClient PlainVideoClient() => new(new WebRtcConfiguration
    {
        EnableVideo = true,
        VideoCodecs = ["VP8"],
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    });
}
