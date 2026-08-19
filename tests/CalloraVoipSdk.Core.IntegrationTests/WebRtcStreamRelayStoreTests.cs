using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The stream relay retention/adoption store (ADR-073 slice 3, #240): <see cref="WebRtcStreamRelayStore"/>
/// retains the first gathered stream relay candidate (first-wins), adopts it into the session immediately when
/// one already exists (the answerer) or on the next build (the offerer), and disposes a candidate no session
/// ever took (the orphan).
/// </summary>
public sealed class WebRtcStreamRelayStoreTests
{
    [Fact]
    public void First_wins_a_second_gathered_candidate_is_not_retained()
    {
        var store = new WebRtcStreamRelayStore(NullLoggerFactory.Instance);
        var first = new FakeStreamRelayAttachment();
        var second = new FakeStreamRelayAttachment();

        Assert.True(store.OnGathered(first, () => null));   // retained
        Assert.False(store.OnGathered(second, () => null)); // surplus — first-wins
        Assert.NotNull(store.RelayedEndPoint);
    }

    [Fact]
    public async Task Offerer_path_retains_at_gather_and_adopts_on_build()
    {
        var store = new WebRtcStreamRelayStore(NullLoggerFactory.Instance);
        var candidate = new FakeStreamRelayAttachment();

        // No session at gather (offerer) → retained but not yet adopted.
        Assert.True(store.OnGathered(candidate, () => null));
        Assert.False(candidate.Activated);

        var session = NewSession();
        store.AdoptInto(session);
        Assert.True(candidate.Activated); // adopted into the freshly built session

        store.AdoptInto(session);          // idempotent — no second adoption
        await store.DisposeAsync();        // adopted → the session owns it, store does not dispose it
        Assert.False(candidate.Disposed);
        await session.DisposeAsync();
        Assert.True(candidate.Disposed);   // disposed by the session that adopted it
    }

    [Fact]
    public async Task Answerer_path_adopts_immediately_when_a_session_already_exists()
    {
        var store = new WebRtcStreamRelayStore(NullLoggerFactory.Instance);
        var candidate = new FakeStreamRelayAttachment();
        var session = NewSession();

        Assert.True(store.OnGathered(candidate, () => session));
        Assert.True(candidate.Activated); // adopted at gather (answerer)

        await store.DisposeAsync();
        Assert.False(candidate.Disposed); // owned by the session
        await session.DisposeAsync();
        Assert.True(candidate.Disposed);
    }

    [Fact]
    public async Task An_orphan_candidate_no_session_took_is_disposed_by_the_store()
    {
        var store = new WebRtcStreamRelayStore(NullLoggerFactory.Instance);
        var candidate = new FakeStreamRelayAttachment();

        Assert.True(store.OnGathered(candidate, () => null)); // offerer, session never built
        await store.DisposeAsync();
        Assert.True(candidate.Disposed);                      // the store disposes the orphan
    }

    private static BundledMediaSession NewSession()
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        var remote = new IPEndPoint(IPAddress.Loopback, 9);
        var options = new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RemoteEndPoint = remote,
            MidExtensionId = 3,
            Audio = new BundledTrackConfig { Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 0, SamplesPerPacket = 160 },
            DtlsIsClient = true,
            RemoteFingerprint = cert.Fingerprint,
            Ice = new IceMediaParameters(
                remote, IceEnabled: true, IceControlling: true,
                LocalIceUfrag: "cli0", LocalIcePwd: "clienticepassword1234567890",
                RemoteIceUfrag: "srv0", RemoteIcePwd: "servericepassword1234567890"),
        };
        return new BundledMediaSession(
            options, new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);
    }
}
