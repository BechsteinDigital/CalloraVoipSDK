using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Thread-safety gate for the mutable per-dialog fields on <c>SipCallSession</c> that are written by
/// signaling/transport threads (HARD-C1). The advertised public contact (host + port) is a logical
/// pair; because <c>int?</c> is a non-atomic 8-byte value and the two fields are written separately,
/// an unsynchronised reader could observe a mismatched pair (new host, old port or vice versa). The
/// write and every read must share the session gate so the pair is always observed consistently.
/// </summary>
public sealed class SipCallSessionAdvertisedContactRaceTests
{
    private static SipCallSession NewOutboundSession()
    {
        var configuration = new SipCallSessionConfiguration
        {
            CallId = "call-c1",
            LocalUri = "sip:alice@example.com",
            RemoteUri = "sip:bob@example.com",
            AuthUsername = "alice",
            UserAgent = "callora-tests",
            Timeout = TimeSpan.FromSeconds(5),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5060),
        };

        var dependencies = new SipCallSessionDependencies
        {
            Transport = new CapturingSipTransportRuntime(),
            DigestAuthenticator = new NoopSipDigestAuthenticator(),
            Logger = NullLogger.Instance,
            ServerTransactions = new NoopSipServerTransactionEngine(),
            IdentityTrustPolicy = new DenyAllSipIdentityTrustPolicy(),
            SdpProvider = new SipSessionSdpProvider
            {
                BuildOffer = (_, _) => string.Empty,
                TryNegotiateAnswer = (_, _, _) => null,
                TryParseMediaParameters = (_, _) => null,
                IsRemoteHold = _ => false,
            },
        };

        return SipCallSession.CreateOutbound(configuration, dependencies);
    }

    [Fact]
    public void RemoteSignalingEndPoint_returns_configured_endpoint()
    {
        var session = NewOutboundSession();
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5060), session.RemoteSignalingEndPoint);
    }

    [Fact]
    public async Task Advertised_contact_pair_is_observed_atomically_under_concurrency()
    {
        var session = NewOutboundSession();
        var reader = new SipCallSessionContextAdapter(session);

        // Only ever two canonical pairs are published; any other combination proves a torn read.
        const string hostA = "203.0.113.10";
        const int portA = 1000;
        const string hostB = "198.51.100.20";
        const int portB = 2000;

        session.SetAdvertisedPublicContact(hostA, portA);

        using var stop = new CancellationTokenSource();
        var inconsistent = 0;

        var writer = Task.Run(() =>
        {
            var toggle = false;
            while (!stop.IsCancellationRequested)
            {
                if (toggle)
                    session.SetAdvertisedPublicContact(hostA, portA);
                else
                    session.SetAdvertisedPublicContact(hostB, portB);
                toggle = !toggle;
            }
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200_000; i++)
            {
                // The atomic snapshot accessor is the only correct way to read the pair; reading the
                // two properties separately would legitimately straddle a write and is not asserted.
                var (host, port) = reader.AdvertisedPublicContact;

                var consistent =
                    (host == hostA && port == portA) ||
                    (host == hostB && port == portB);

                if (!consistent)
                    Interlocked.Increment(ref inconsistent);
            }
        })).ToArray();

        await Task.WhenAll(readers);
        stop.Cancel();
        await writer;

        Assert.Equal(0, Volatile.Read(ref inconsistent));
    }
}
