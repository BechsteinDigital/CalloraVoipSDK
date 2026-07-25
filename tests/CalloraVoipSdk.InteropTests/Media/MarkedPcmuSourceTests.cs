using CalloraVoipSdk.InteropTests.Media;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Media;

public sealed class MarkedPcmuSourceTests
{
    [Fact]
    public void Next_produces_monotonic_readable_sequence_markers()
    {
        var src = new MarkedPcmuSource();
        var f0 = src.Next();
        var f1 = src.Next();

        Assert.Equal(0u, MarkedPcmuSource.ReadSequence(f0.Payload.Span));
        Assert.Equal(1u, MarkedPcmuSource.ReadSequence(f1.Payload.Span));
        Assert.Equal(MarkedPcmuSource.FrameBytes, f0.Payload.Length);
        Assert.Equal(0, f0.PayloadType);          // PCMU
        Assert.Equal(160u, f0.DurationRtpUnits);  // 20 ms @ 8 kHz
    }
}
