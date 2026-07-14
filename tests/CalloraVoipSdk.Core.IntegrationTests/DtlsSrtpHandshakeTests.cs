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

        Assert.Equal(SrtpCryptoSuite.AesCm128HmacSha1_80, client.Keys.Suite);
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
        // Peer prefers the 32-bit-tag profile, we prefer the 80-bit one (RFC 5764 §4.1.2).
        var offered = new[] { 0x0002, 0x0001 };

        Assert.Equal(0x0001, DtlsSrtpProfiles.SelectFromOffered(offered));
        Assert.Equal(0x0002, DtlsSrtpProfiles.SelectFromOffered(new[] { 0x0002 }));
        Assert.Null(DtlsSrtpProfiles.SelectFromOffered(new[] { 0x0007 }));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static DtlsSrtpHandshaker CreateHandshaker() =>
        new(NullLogger<DtlsSrtpHandshaker>.Instance);

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
