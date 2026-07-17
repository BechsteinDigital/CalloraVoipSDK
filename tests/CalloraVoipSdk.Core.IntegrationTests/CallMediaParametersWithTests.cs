using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// After ICE candidate-pair selection the media orchestrator overrides only the transport endpoints
/// and must carry every other negotiated field across. <see cref="CallMediaParameters"/> is now a
/// record so this happens via a <c>with</c> expression (HARD-R5); the prior hand-written copy in
/// <c>ResolveIceCandidatePairAsync</c> silently dropped the SDES/DTLS key material, the ICE role and
/// the video parameters — downgrading secure/video calls to plain RTP after ICE.
/// </summary>
public sealed class CallMediaParametersWithTests
{
    private static IPEndPoint Ep(int port) => new(IPAddress.Loopback, port);

    private static CallMediaParameters FullyPopulated() => new()
    {
        LocalEndPoint = Ep(4000),
        RemoteEndPoint = Ep(5000),
        LocalRtcpEndPoint = Ep(4001),
        RemoteRtcpEndPoint = Ep(5001),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        IceEnabled = true,
        IceControlling = false,                 // non-default (default is true)
        IsSrtpNegotiated = true,
        IsSrtcpEncrypted = true,
        SrtpSuite = "AES_CM_128_HMAC_SHA1_80",
        SrtpLocalKeyParams = "inline:LOCALKEY",
        SrtpRemoteKeyParams = "inline:REMOTEKEY",
        IsDtlsNegotiated = true,
        DtlsIsClient = true,
        DtlsRemoteFingerprintAlgorithm = "sha-256",
        DtlsRemoteFingerprintValue = "AA:BB:CC",
        Video = new CallVideoParameters
        {
            PayloadType = 96,
            CodecName = "VP8",
            LocalEndPoint = Ep(4002),
            RemoteEndPoint = Ep(5002)
        }
    };

    [Fact]
    public void Overriding_endpoints_preserves_every_other_negotiated_field()
    {
        var original = FullyPopulated();

        // Exactly the transformation ResolveIceCandidatePairAsync now performs.
        var derived = original with
        {
            LocalEndPoint = Ep(6000),
            RemoteEndPoint = Ep(7000),
            LocalRtcpEndPoint = Ep(6001),
            RemoteRtcpEndPoint = Ep(7001)
        };

        // Endpoints are overridden…
        Assert.Equal(6000, derived.LocalEndPoint.Port);
        Assert.Equal(7000, derived.RemoteEndPoint.Port);
        Assert.Equal(6001, derived.LocalRtcpEndPoint!.Port);
        Assert.Equal(7001, derived.RemoteRtcpEndPoint!.Port);

        // …and every field the hand-written copy used to drop survives.
        Assert.False(derived.IceControlling);
        Assert.True(derived.IsSrtpNegotiated);
        Assert.True(derived.IsSrtcpEncrypted);
        Assert.Equal("AES_CM_128_HMAC_SHA1_80", derived.SrtpSuite);
        Assert.Equal("inline:LOCALKEY", derived.SrtpLocalKeyParams);
        Assert.Equal("inline:REMOTEKEY", derived.SrtpRemoteKeyParams);
        Assert.True(derived.IsDtlsNegotiated);
        Assert.True(derived.DtlsIsClient);
        Assert.Equal("sha-256", derived.DtlsRemoteFingerprintAlgorithm);
        Assert.Equal("AA:BB:CC", derived.DtlsRemoteFingerprintValue);
        Assert.Same(original.Video, derived.Video);
    }

    [Fact]
    public void ToString_does_not_leak_the_srtp_or_dtls_key_material()
    {
        var text = FullyPopulated().ToString();

        // The record's synthesized ToString would have dumped every property, including secrets.
        Assert.DoesNotContain("LOCALKEY", text);
        Assert.DoesNotContain("REMOTEKEY", text);
        Assert.DoesNotContain("AA:BB:CC", text);
        // Operational fields stay visible for diagnostics.
        Assert.Contains("PayloadType = 0", text);
        Assert.Contains("IsDtlsNegotiated = True", text);
    }
}
