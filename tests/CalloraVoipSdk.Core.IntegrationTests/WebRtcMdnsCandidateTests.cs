using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class WebRtcMdnsCandidateTests
{
    private sealed class CountingResolver : IMdnsResolver
    {
        public int Calls;
        public string? LastHost;
        private readonly IPAddress? _result;
        public CountingResolver(IPAddress? result) => _result = result;
        public Task<IPAddress?> ResolveAsync(string hostname, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastHost = hostname;
            return Task.FromResult(_result);
        }
    }

    private static WebRtcPeerConnection BuildPeer(IMdnsResolver resolver)
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        return new WebRtcPeerConnection(
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
                Dtls = new SdpDtlsParameters { Algorithm = cert.Fingerprint.Algorithm, Fingerprint = cert.Fingerprint.Value },
                Ice = new SdpIceParameters { Ufrag = "mdns", Pwd = "mdnspassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance,
            mdnsResolver: resolver);
    }

    [Fact]
    public async Task LocalCandidate_Triggers_Mdns_Resolution()
    {
        var resolver = new CountingResolver(IPAddress.Parse("192.168.7.7"));
        await using var peer = BuildPeer(resolver);

        await peer.AddIceCandidateAsync("candidate:1 1 udp 2113937151 abcd1234.local 54321 typ host");

        // Die Auflösung läuft im Hintergrund; kurz pollen, bis der Resolver gerufen wurde.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (Volatile.Read(ref resolver.Calls) == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);

        Assert.Equal(1, Volatile.Read(ref resolver.Calls));
        Assert.Equal("abcd1234.local", resolver.LastHost);
    }

    [Fact]
    public async Task Ip_Candidate_Does_Not_Trigger_Mdns_Resolution()
    {
        var resolver = new CountingResolver(null);
        await using var peer = BuildPeer(resolver);

        await peer.AddIceCandidateAsync("candidate:1 1 udp 2113937151 192.168.1.20 54321 typ host");
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref resolver.Calls)); // reiner IP-Pfad unverändert
    }
}
