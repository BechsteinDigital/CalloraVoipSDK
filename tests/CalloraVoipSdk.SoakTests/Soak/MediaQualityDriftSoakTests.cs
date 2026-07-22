using CalloraVoipSdk.InteropHarness.Audit;
using CalloraVoipSdk.InteropHarness.Media;
using CalloraVoipSdk.InteropHarness.Metrics;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class MediaQualityDriftSoakTests
{
    /// <summary>Die Qualitäts-Matrix: jeder Codec × jede Transport-Sicherheit.</summary>
    public static IEnumerable<object[]> Matrix() => new[]
    {
        new object[] { LoopbackCodec.Pcmu, LoopbackSecurity.Plain },
        new object[] { LoopbackCodec.Opus, LoopbackSecurity.Plain },
        new object[] { LoopbackCodec.Pcmu, LoopbackSecurity.Srtp },
        new object[] { LoopbackCodec.Opus, LoopbackSecurity.Srtp },
    };

    [Theory, Trait("Category", "SoakShort")]
    [MemberData(nameof(Matrix))]
    public Task MediaQuality_Short(LoopbackCodec codec, LoopbackSecurity security)
        => RunQualityAsync(SoakProfile.Short, codec, security);

    [Theory, Trait("Category", "SoakLong")]
    [MemberData(nameof(Matrix))]
    public Task MediaQuality_Long(LoopbackCodec codec, LoopbackSecurity security)
        => RunQualityAsync(SoakProfile.Long, codec, security);

    private static async Task RunQualityAsync(SoakProfile profile, LoopbackCodec codec, LoopbackSecurity security)
    {
        var tag = $"[{codec}/{security}]";
        await using var loopback = await RtpMediaLoopback.StartAsync(
            codec: codec, security: security, metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: profile.Duration, frameInterval: TimeSpan.FromMilliseconds(20));

        // Short: kurze Laufdauer — Startup-Jitter dominiert, Trend nicht aussagekräftig.
        // Smoke prüft nur: Codec/Sicherheit sind verdrahtet und Pakete kommen an.
        var isShort = profile.Duration.TotalSeconds <= 5;
        var minSnapshots = isShort ? 3 : 10;
        Assert.True(snapshots.Count >= minSnapshots, $"{tag} Zu wenige Snapshots: {snapshots.Count}");

        // Artefakt VOR den Assertions: Qualitäts-Messreihe je Codec/Sicherheit auch bei Fehlschlag festhalten.
        SoakArtifactSink.TryWrite(SoakArtifactSink.CreateReport(
            "MediaQualityDrift",
            new Dictionary<string, string>
            {
                ["Codec"] = codec.ToString(),
                ["Security"] = security.ToString(),
                ["DurationSec"] = ((int)profile.Duration.TotalSeconds).ToString(),
            },
            qualitySeries: snapshots));

        var last = snapshots[^1];
        Assert.True(last.PacketsDelivered > 0, $"{tag} Es müssen Pakete ausgeliefert worden sein.");

        // Sauberer UDP-Loopback: der Jitter-Buffer darf nie überlaufen (Overflow ist eine
        // eigenständige Verlustursache, getrennt von Late-Drops und unverdeckbarem Verlust).
        Assert.Equal(0, last.PacketsDroppedOverflow);

        if (!isShort)
        {
            var jitter = TrendAssertions.NoUpwardDrift(
                snapshots, s => s.JitterMs, toleranceRatio: 0.50, metricName: $"{tag} JitterMs");
            Assert.False(jitter.HasDrift, jitter.Detail);
        }
    }

    // Verifiziert F002: auf reinem UDP-Loopback (kein echter Verlust) MUSS UnrecoverableLoss 0 sein.
    // Late-angekommene Pakete werden nicht mehr als UnrecoverableLoss gezählt — der Late-Drop-Pfad rückt jetzt
    // den Delivered-Sequence-Cursor vorwärts, sodass kein falscher Gap entsteht (siehe RtpCallMediaSession
    // HandleJitterBufferAddResult/Late + EmitConcealmentFramesIfNeeded, docs/audit/INTEROP_SOAK_AUDIT.md F002).
    [Fact, Trait("Category", "SoakLong")]
    public async Task LongCall_UnrecoverableLoss_IsZeroOnLoopback()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(20), frameInterval: TimeSpan.FromMilliseconds(20));

        Assert.Equal(0, snapshots[^1].PacketsUnrecoverableLoss);
    }

    // F004 (Facade-Kopplungslücke): Der bare L2-RtpCallMediaSession verdrahtet KEINEN RTCP-Quality-Monitor
    // — das tut erst der L3-CallMediaOrchestrator. RoundTripTimeMs bleibt daher der statische Anlauf-Hint
    // und ist über den ganzen Lauf konstant (keine Live-RTCP-SR/RR-Messung). Dieser Test hält die aktuelle
    // Schicht-Grenze fest; schlägt er fehl (RTT variiert), wurde RTCP-Monitoring nach L2 gezogen → Register
    // aktualisieren. Siehe docs/audit/INTEROP_SOAK_AUDIT.md (F004).
    [Fact, Trait("Category", "SoakLong")]
    public async Task RoundTripTime_IsStaticHint_NotLiveRtcpMeasurement_F004()
    {
        await using var loopback = await RtpMediaLoopback.StartAsync(
            metricsPublishInterval: TimeSpan.FromMilliseconds(200));

        var snapshots = await loopback.RunAndCollectQualityAsync(
            duration: TimeSpan.FromSeconds(15), frameInterval: TimeSpan.FromMilliseconds(20));

        Assert.True(snapshots.Count >= 10, $"Zu wenige Snapshots: {snapshots.Count}");
        var distinctRtt = snapshots.Select(s => s.RoundTripTimeMs).Distinct().ToArray();
        Assert.True(distinctRtt.Length == 1,
            $"RTT variierte über den Lauf ({string.Join(", ", distinctRtt)}) → RTCP-Monitoring scheint an L2 " +
            "verdrahtet; F004 im Register neu bewerten.");
    }
}
