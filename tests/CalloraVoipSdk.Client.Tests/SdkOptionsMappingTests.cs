using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Mapping gate for <see cref="SdkOptions"/> → <see cref="SdkConfiguration"/> (HARD-E4). The
/// host-facing options must carry every configurable media/security feature onto the runtime
/// configuration; a missing field silently pins the consumer to a default they cannot override
/// through <c>AddCallora(...)</c>.
/// </summary>
public sealed class SdkOptionsMappingTests
{
    [Fact]
    public void Maps_the_previously_unconfigurable_features_onto_the_configuration()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dtlsCertificate = new CertificateRequest("CN=map", ecdsa, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var options = new SdkOptions
        {
            OfferDtlsSrtp = true,
            EnableVideo = true,
            PreferredVideoCodecs = ["VP8", "H264"],
            BridgeAudioFormat = BridgeAudioFormat.Pcmu,
            InboundMediaTimeout = TimeSpan.FromSeconds(42),
            HangupHeldCallOnMediaSilence = true,
            DtlsCertificate = dtlsCertificate,
        };

        var config = options.ToConfiguration(loggerFactory: null);

        Assert.True(config.OfferDtlsSrtp);
        Assert.True(config.EnableVideo);
        Assert.Equal(["VP8", "H264"], config.PreferredVideoCodecs);
        Assert.Equal(BridgeAudioFormat.Pcmu, config.BridgeAudioFormat);
        Assert.Equal(TimeSpan.FromSeconds(42), config.InboundMediaTimeout);
        Assert.True(config.HangupHeldCallOnMediaSilence);
        Assert.Same(dtlsCertificate, config.DtlsCertificate);
    }

    [Fact]
    public void Unset_features_fall_through_to_the_configuration_defaults()
    {
        var config = new SdkOptions().ToConfiguration(loggerFactory: null);
        var configurationDefaults = new SdkConfiguration();

        Assert.False(config.OfferDtlsSrtp);
        Assert.False(config.EnableVideo);
        Assert.Null(config.PreferredVideoCodecs);
        Assert.Null(config.DtlsCertificate);
        Assert.Equal(configurationDefaults.BridgeAudioFormat, config.BridgeAudioFormat);
        Assert.Equal(configurationDefaults.InboundMediaTimeout, config.InboundMediaTimeout);
        Assert.False(config.HangupHeldCallOnMediaSilence);
    }
}
