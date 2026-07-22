using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Codec-Negotiation gegen echten Asterisk: Endpoint 6001 erlaubt PCMU/PCMA/G722; die SDK-Präferenz
/// (<see cref="VoipConfiguration.PreferredAudioCodecs"/>) steuert, welcher Codec im SDP-Offer/Answer
/// tatsächlich gewählt wird. Geprüft über die öffentliche <see cref="ICall.MediaParameters"/>
/// (<c>CodecName</c>/<c>PayloadType</c>). Plain RTP (<see cref="SrtpPolicy.Disabled"/>, siehe F007).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskCodecNegotiationInteropTests
{
    private static VoipClient NewClient(params string[] preferredCodecs) =>
        new(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Disabled,
            PreferredAudioCodecs = preferredCodecs,
        });

    private static async Task<CallMediaParameters> DialAnswerAndReadCodecAsync(AsteriskContainer asterisk, VoipClient client)
    {
        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = asterisk.Username,
                Password = asterisk.Password,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(reg.IsSuccess, $"Registrierung fehlgeschlagen: Status={reg.Status}");

        var result = await client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("answer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");

        var mediaParameters = result.Call!.MediaParameters;
        Assert.NotNull(mediaParameters);
        await result.Call!.HangupAsync();
        return mediaParameters!;
    }

    [DockerRequiredFact]
    public async Task PrefersPcmu_NegotiatesPcmu()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient("PCMU");
        var mediaParameters = await DialAnswerAndReadCodecAsync(asterisk, client);
        Assert.Equal("PCMU", mediaParameters.CodecName);
        Assert.Equal(0, mediaParameters.PayloadType);
    }

    [DockerRequiredFact]
    public async Task PrefersPcma_NegotiatesPcma()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient("PCMA");
        var mediaParameters = await DialAnswerAndReadCodecAsync(asterisk, client);
        Assert.Equal("PCMA", mediaParameters.CodecName);
        Assert.Equal(8, mediaParameters.PayloadType);
    }

    [DockerRequiredFact]
    public async Task PrefersG722_NegotiatesG722()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient("G722");
        var mediaParameters = await DialAnswerAndReadCodecAsync(asterisk, client);
        Assert.Equal("G722", mediaParameters.CodecName);
        Assert.Equal(9, mediaParameters.PayloadType);
    }
}
