using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.InteropHarness.Perf;

/// <summary>
/// Micro-benchmarks of the SRTP per-packet crypto hot path — the code that runs on <b>every</b> media packet
/// of <b>every</b> call, in both directions, so a regression here scales with the whole server's load. The
/// internal <c>SrtpContext</c> is constructed here (the harness has internals access); the perf gate lives in
/// the test project and asserts the throughput floors.
/// </summary>
public static class MediaCryptoBenchmarks
{
    private const int WarmupIterations = 20_000;
    private const int MeasuredIterations = 200_000;

    // A representative 20 ms G.711 packet: 12-byte RTP header + 160-byte payload.
    private static byte[] BuildRtpPacket()
    {
        var packet = new byte[12 + 160];
        packet[0] = 0x80;             // V=2, P=0, X=0, CC=0
        packet[1] = 0x00;             // M=0, PT=0 (PCMU)
        packet[2] = 0x12; packet[3] = 0x34; // sequence number
        packet[4] = 0x00; packet[5] = 0x00; packet[6] = 0x10; packet[7] = 0x00; // timestamp
        packet[8] = 0xDE; packet[9] = 0xAD; packet[10] = 0xBE; packet[11] = 0xEF; // SSRC
        for (var i = 12; i < packet.Length; i++)
            packet[i] = (byte)(i * 31);
        return packet;
    }

    /// <summary>SRTP <c>Protect</c> (encrypt + auth) throughput for AES-CM-128 / HMAC-SHA1-80.</summary>
    public static PerfMeasurement SrtpProtectAesCm128() =>
        MeasureProtect("SrtpProtect.AesCm128", SrtpCryptoSuite.AesCm128HmacSha1_80, saltLength: 14);

    /// <summary>SRTP <c>Protect</c> (AEAD encrypt) throughput for AEAD-AES-128-GCM (the GA-preferred suite).</summary>
    public static PerfMeasurement SrtpProtectAeadGcm128() =>
        MeasureProtect("SrtpProtect.AeadGcm128", SrtpCryptoSuite.AeadAes128Gcm, saltLength: 12);

    private static PerfMeasurement MeasureProtect(string name, SrtpCryptoSuite suite, int saltLength)
    {
        var masterKey = new byte[16];
        var masterSalt = new byte[saltLength];
        for (var i = 0; i < masterKey.Length; i++) masterKey[i] = (byte)(0x10 + i);
        for (var i = 0; i < masterSalt.Length; i++) masterSalt[i] = (byte)(0xA0 + i);

        using var material = new SrtpKeyMaterial(masterKey, masterSalt, suite);
        using var context = new SrtpContext(material);
        var packet = BuildRtpPacket();

        return PerfRunner.Measure(name, WarmupIterations, MeasuredIterations, () => context.Protect(packet));
    }
}
