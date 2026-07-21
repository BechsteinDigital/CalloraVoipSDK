using CalloraVoipSdk.InteropHarness.Media;

namespace CalloraVoipSdk.SoakTests.Media;

public sealed class RtpMediaLoopbackRoundTripTests
{
    [Fact]
    public async Task RoundTripAsync_PlainRtp_DeliversPayloadToPeer()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync();

        var payload = new byte[160];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        var received = await loopback.RoundTripAsync(payload, TimeSpan.FromSeconds(10));

        Assert.Equal(payload, received);
    }
}
