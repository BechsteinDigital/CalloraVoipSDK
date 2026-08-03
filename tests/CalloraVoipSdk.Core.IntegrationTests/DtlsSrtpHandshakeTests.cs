using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// In-memory loopback handshakes between the SDK's DTLS-SRTP client and server
/// (RFC 5763/5764): key export symmetry, SRTP interoperability of the exported keys,
/// fingerprint enforcement, and use_srtp profile negotiation.
/// </summary>
public sealed class DtlsSrtpHandshakeTests
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Handshake_ExportsMirroredKeysOnBothSides()
    {
        var (clientResult, serverResult) = await RunLoopbackHandshakeAsync();
        using var client = clientResult;
        using var server = serverResult;

        // Two SDK peers both prefer AEAD-GCM, so the handshake negotiates GCM-128 and exports its
        // 16-byte key + 12-byte salt on both sides (RFC 7714 preferred over AES-CM).
        Assert.Equal(SrtpCryptoSuite.AeadAes128Gcm, client.Keys.Suite);
        Assert.Equal(client.Keys.Suite, server.Keys.Suite);

        // RFC 5764 §4.2: the client's write keys are the server's read keys and vice versa.
        AssertKeysEqual(client.Keys.LocalKeys, server.Keys.RemoteKeys);
        AssertKeysEqual(client.Keys.RemoteKeys, server.Keys.LocalKeys);

        // Directions must not share a keystream.
        Assert.NotEqual(
            Convert.ToHexString(client.Keys.LocalKeys.MasterKey.Span),
            Convert.ToHexString(client.Keys.RemoteKeys.MasterKey.Span));
    }

    [Fact]
    public async Task Handshake_ExportedKeysProduceWorkingSrtpPath()
    {
        var (clientResult, serverResult) = await RunLoopbackHandshakeAsync();
        using var client = clientResult;
        using var server = serverResult;

        using var protect = new SrtpContext(client.Keys.LocalKeys);
        using var unprotect = new SrtpContext(server.Keys.RemoteKeys);

        var rtpPacket = CreateRtpPacket();
        var roundTripped = unprotect.Unprotect(protect.Protect(rtpPacket));

        Assert.Equal(rtpPacket, roundTripped);
    }

    [Fact]
    public async Task Handshake_FailsWhenServerFingerprintDoesNotMatch()
    {
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var wrongFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        var (clientTransport, serverTransport) = CreateTransportPair();
        var handshaker = CreateHandshaker();
        using var timeout = new CancellationTokenSource(HandshakeTimeout);

        // Client expects a fingerprint that is not the server's — it must abort.
        var clientTask = handshaker.HandshakeAsync(
            DtlsRole.Client, clientTransport, clientCertificate, wrongFingerprint, timeout.Token);
        var serverTask = handshaker.HandshakeAsync(
            DtlsRole.Server, serverTransport, serverCertificate, clientCertificate.Fingerprint, timeout.Token);

        await Assert.ThrowsAsync<DtlsSrtpHandshakeException>(() => clientTask);
        await Assert.ThrowsAsync<DtlsSrtpHandshakeException>(() => serverTask);
    }

    [Fact]
    public async Task Handshake_FailsWhenClientFingerprintDoesNotMatch()
    {
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var wrongFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        var (clientTransport, serverTransport) = CreateTransportPair();
        var handshaker = CreateHandshaker();
        using var timeout = new CancellationTokenSource(HandshakeTimeout);

        // Server expects a fingerprint that is not the client's — mutual auth must fail.
        var clientTask = handshaker.HandshakeAsync(
            DtlsRole.Client, clientTransport, clientCertificate, serverCertificate.Fingerprint, timeout.Token);
        var serverTask = handshaker.HandshakeAsync(
            DtlsRole.Server, serverTransport, serverCertificate, wrongFingerprint, timeout.Token);

        await Assert.ThrowsAsync<DtlsSrtpHandshakeException>(() => serverTask);
        await Assert.ThrowsAsync<DtlsSrtpHandshakeException>(() => clientTask);
    }

    [Fact]
    public async Task Handshake_CancellationAbortsInsteadOfHanging()
    {
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();

        // Client transport sends into the void — the handshake can never progress.
        var clientTransport = new QueueDatagramTransport(_ => { });
        var handshaker = CreateHandshaker();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handshaker.HandshakeAsync(
                DtlsRole.Client, clientTransport, clientCertificate,
                serverCertificate.Fingerprint, cts.Token));
    }

    [Fact]
    public Task Handshake_SilentPeer_TimesOutAsClient_WithoutExternalCancellation()
        => AssertHandshakeTimesOutAsync(DtlsRole.Client);

    [Fact]
    public Task Handshake_SilentPeer_TimesOutAsServer_WithoutExternalCancellation()
        => AssertHandshakeTimesOutAsync(DtlsRole.Server);

    [Fact]
    public async Task Handshake_SilentPeer_TimesOutRepeatedly_WithoutAccumulatingHangs()
    {
        // #163 P1-1: five sequential silent handshakes must each terminate on their own
        // deadline. A leaked worker thread or an unbounded transport queue would surface as
        // one of the later attempts failing to complete within the watchdog window.
        for (var i = 0; i < 5; i++)
            await AssertHandshakeTimesOutAsync(DtlsRole.Client);
    }

    [Fact]
    public void Fingerprint_FormatsAsRfc8122UppercaseHex()
    {
        var fingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        Assert.Equal(DtlsFingerprint.Sha256Algorithm, fingerprint.Algorithm);
        Assert.Equal(32 * 3 - 1, fingerprint.Value.Length);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint.Value);
    }

    [Fact]
    public void Fingerprint_MatchesIsCaseInsensitive()
    {
        var fingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;
        var lowered = new DtlsFingerprint
        {
            Algorithm = "SHA-256",
            Value = fingerprint.Value.ToLowerInvariant(),
        };

        Assert.True(fingerprint.Matches(lowered));
    }

    [Fact]
    public void Profiles_SelectFromOffered_HonoursLocalPreferenceOrder()
    {
        // Among the AES-CM profiles the peer prefers the 32-bit tag, we prefer the 80-bit one (RFC 5764 §4.1.2).
        var offered = new[] { 0x0002, 0x0001 };

        Assert.Equal(0x0001, DtlsSrtpProfiles.SelectFromOffered(offered));
        Assert.Equal(0x0002, DtlsSrtpProfiles.SelectFromOffered(new[] { 0x0002 }));
        // AEAD-GCM outranks AES-CM: a GCM-only offer selects GCM-128, and GCM wins even when AES-CM is also offered.
        Assert.Equal(0x0007, DtlsSrtpProfiles.SelectFromOffered(new[] { 0x0007 }));
        Assert.Equal(0x0007, DtlsSrtpProfiles.SelectFromOffered(new[] { 0x0001, 0x0007 }));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static DtlsSrtpHandshaker CreateHandshaker() =>
        new(NullLogger<DtlsSrtpHandshaker>.Instance);

    private static async Task AssertHandshakeTimesOutAsync(DtlsRole role)
    {
        var certificate = DtlsCertificate.GenerateEcdsaP256();
        var peerFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint;

        // Transport that never yields a peer flight — the handshake can only end on its deadline.
        var transport = new QueueDatagramTransport(_ => { });
        var handshaker = new DtlsSrtpHandshaker(
            NullLogger<DtlsSrtpHandshaker>.Instance,
            new DtlsHandshakeOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(250) });

        // No external cancellation token: only the product deadline can end this handshake.
        var handshakeTask = handshaker.HandshakeAsync(
            role, transport, certificate, peerFingerprint, CancellationToken.None);

        // Watchdog: a regression that fails to enforce the deadline hangs forever — fail loudly
        // instead of stalling the test run.
        var finished = await Task.WhenAny(handshakeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(
            ReferenceEquals(finished, handshakeTask),
            $"DTLS handshake as {role} did not honour its product deadline and hung.");

        await Assert.ThrowsAsync<DtlsSrtpHandshakeTimeoutException>(() => handshakeTask);
    }

    private static (QueueDatagramTransport Client, QueueDatagramTransport Server) CreateTransportPair()
    {
        QueueDatagramTransport? client = null;
        QueueDatagramTransport? server = null;
        client = new QueueDatagramTransport(datagram => server!.Enqueue(datagram));
        server = new QueueDatagramTransport(datagram => client.Enqueue(datagram));
        return (client, server);
    }

    private static async Task<(DtlsSrtpHandshakeResult Client, DtlsSrtpHandshakeResult Server)>
        RunLoopbackHandshakeAsync()
    {
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var (clientTransport, serverTransport) = CreateTransportPair();
        var handshaker = CreateHandshaker();
        using var timeout = new CancellationTokenSource(HandshakeTimeout);

        var clientTask = handshaker.HandshakeAsync(
            DtlsRole.Client, clientTransport, clientCertificate,
            serverCertificate.Fingerprint, timeout.Token);
        var serverTask = handshaker.HandshakeAsync(
            DtlsRole.Server, serverTransport, serverCertificate,
            clientCertificate.Fingerprint, timeout.Token);

        // Await the server first on failure so its exception (the usual root cause)
        // surfaces instead of the client's derived alert.
        try
        {
            await Task.WhenAll(clientTask, serverTask);
        }
        catch when (serverTask.IsFaulted)
        {
            await serverTask;
        }

        return (clientTask.Result, serverTask.Result);
    }

    private static void AssertKeysEqual(SrtpKeyMaterial expected, SrtpKeyMaterial actual)
    {
        Assert.Equal(Convert.ToHexString(expected.MasterKey.Span), Convert.ToHexString(actual.MasterKey.Span));
        Assert.Equal(Convert.ToHexString(expected.MasterSalt.Span), Convert.ToHexString(actual.MasterSalt.Span));
        Assert.Equal(expected.Suite, actual.Suite);
    }

    private static byte[] CreateRtpPacket()
    {
        var packet = new byte[12 + 32];
        packet[0] = 0x80; // V=2
        packet[1] = 0x00; // PT=0
        packet[2] = 0x12; // seq
        packet[3] = 0x34;
        packet[8] = 0xAB; // SSRC
        for (var i = 12; i < packet.Length; i++)
            packet[i] = (byte)i;
        return packet;
    }
}
