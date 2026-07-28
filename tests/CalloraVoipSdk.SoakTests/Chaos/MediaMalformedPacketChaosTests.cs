using CalloraVoipSdk.InteropHarness.Chaos;
using CalloraVoipSdk.InteropHarness.Media;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Chaos;

/// <summary>
/// CORE-011 chaos gate — Fault class 3 (malformed / adversarial packets). The inbound media path must discard
/// garbage, truncated, and corrupted datagrams robustly (no crash, no wedge), and — under SRTP — reject
/// corrupted packets fail-closed rather than delivering forged media.
/// </summary>
public sealed class MediaMalformedPacketChaosTests
{
    private static readonly byte[] Payload = new byte[160];

    // A spread of malformed datagrams that must not crash or wedge the inbound RTP parser: empty, a lone
    // byte, a truncated RTP header (version bits only), a short garbage run, and an oversized garbage blob.
    private static IEnumerable<byte[]> MalformedDatagrams()
    {
        yield return [];
        yield return [0xFF];
        yield return [0x80]; // RTP version 2, nothing else — truncated header
        yield return [0x80, 0x00, 0x00, 0x01];
        yield return Enumerable.Range(0, 64).Select(i => (byte)(i * 7)).ToArray();
        yield return new byte[2000]; // oversized zero blob
    }

    [Fact, Trait("Category", "Chaos")]
    public async Task Plain_rtp_survives_a_barrage_of_malformed_and_corrupted_packets()
    {
        await using var loop = await ChaosRtpMediaLoopback.StartAsync();

        Assert.True(
            await loop.TryRoundTripAsync(Payload, TimeSpan.FromSeconds(3)),
            "media should flow before the barrage");

        // Inject adversarial datagrams straight at the receiver, and corrupt a third of legit forwarded ones.
        for (var round = 0; round < 3; round++)
            foreach (var junk in MalformedDatagrams())
                await loop.Relay.InjectAsync(junk, RelayLeg.B);
        loop.Relay.SetCorruptRate(0.3);

        // The receiver discarded the garbage and kept running — legit media still round-trips through the noise.
        Assert.True(
            await loop.TryRoundTripAsync(Payload, TimeSpan.FromSeconds(5)),
            "legit media should still flow while garbage and corruption are on the wire");
        Assert.True(loop.Relay.Injected >= 18, "the adversarial datagrams should have been injected");
    }

    [Fact, Trait("Category", "Chaos")]
    public async Task Srtp_rejects_corrupted_packets_fail_closed()
    {
        await using var loop = await ChaosRtpMediaLoopback.StartAsync(security: LoopbackSecurity.Srtp);

        // Clean SRTP media flows.
        Assert.True(
            await loop.TryRoundTripAsync(Payload, TimeSpan.FromSeconds(3)),
            "clean SRTP media should flow");

        // Corrupt every forwarded packet: a single flipped byte breaks the SRTP auth tag (RFC 3711). Drain the
        // jitter buffer under corruption so only freshly-corrupted packets remain in flight.
        loop.Relay.SetCorruptRate(1.0);
        await loop.SendForAsync(Payload, TimeSpan.FromSeconds(2));

        // Fail-closed: with every packet failing authentication, no forged frame is delivered to B.
        Assert.False(
            await loop.TryRoundTripAsync(Payload, TimeSpan.FromSeconds(1.5)),
            "corrupted SRTP packets must be rejected, not delivered (fail-closed)");
        Assert.True(loop.Relay.Corrupted > 0, "packets should actually have been corrupted on the wire");

        // Recovery: with corruption off, authenticated media flows again — the path itself was never broken.
        loop.Relay.SetCorruptRate(0);
        Assert.True(
            await loop.TryRoundTripAsync(Payload, TimeSpan.FromSeconds(5)),
            "clean SRTP media should resume once corruption stops");
    }
}
