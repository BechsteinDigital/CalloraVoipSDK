using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Opt-in DTLS-SRTP identity from a caller-supplied certificate (HARD-E7). A supplied ECDSA P-256
/// certificate must yield a stable identity that actually keys a DTLS-SRTP handshake, while
/// unsupported inputs (RSA, non-P-256 curves, key-less certificates) are rejected fail-closed rather
/// than producing a silently-unusable identity.
/// </summary>
public sealed class DtlsCertificateFromX509Tests
{
    [Fact]
    public void FromX509_preserves_the_certificate_as_a_stable_fingerprint()
    {
        using var certificate = CreateEcdsaCertificate(ECCurve.NamedCurves.nistP256);

        var first = DtlsCertificate.FromX509(certificate);
        var second = DtlsCertificate.FromX509(certificate);

        Assert.Equal(DtlsFingerprint.Sha256Algorithm, first.Fingerprint.Algorithm);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", first.Fingerprint.Value);
        // Stable across calls (unlike the ephemeral default) and faithful to the supplied certificate.
        Assert.Equal(first.Fingerprint.Value, second.Fingerprint.Value);
        Assert.Equal(DtlsFingerprint.FromDerCertificate(certificate.RawData).Value, first.Fingerprint.Value);
    }

    [Fact]
    public void FromX509_rejects_an_rsa_certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=e7-rsa", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        Assert.Throws<ArgumentException>(() => DtlsCertificate.FromX509(certificate));
    }

    [Fact]
    public void FromX509_rejects_a_non_p256_ecdsa_curve()
    {
        using var certificate = CreateEcdsaCertificate(ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentException>(() => DtlsCertificate.FromX509(certificate));
    }

    [Fact]
    public async Task A_supplied_certificate_keys_a_real_dtls_srtp_handshake()
    {
        using var clientCertificate = CreateEcdsaCertificate(ECCurve.NamedCurves.nistP256);
        using var serverCertificate = CreateEcdsaCertificate(ECCurve.NamedCurves.nistP256);
        var client = DtlsCertificate.FromX509(clientCertificate);
        var server = DtlsCertificate.FromX509(serverCertificate);

        var (clientTransport, serverTransport) = TransportPair();
        var handshaker = new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var clientTask = handshaker.HandshakeAsync(
            DtlsRole.Client, clientTransport, client, server.Fingerprint, timeout.Token);
        var serverTask = handshaker.HandshakeAsync(
            DtlsRole.Server, serverTransport, server, client.Fingerprint, timeout.Token);

        try
        {
            await Task.WhenAll(clientTask, serverTask);
        }
        catch when (serverTask.IsFaulted)
        {
            await serverTask;
        }

        using var clientResult = clientTask.Result;
        using var serverResult = serverTask.Result;

        // RFC 5764 §4.2: the client's write keys are the server's read keys — proves the supplied
        // certificates genuinely keyed the DTLS-SRTP exchange, not just parsed into a fingerprint.
        Assert.Equal(
            Convert.ToHexString(clientResult.Keys.LocalKeys.MasterKey.Span),
            Convert.ToHexString(serverResult.Keys.RemoteKeys.MasterKey.Span));
        Assert.Equal(clientResult.Keys.Suite, serverResult.Keys.Suite);
    }

    private static X509Certificate2 CreateEcdsaCertificate(ECCurve curve)
    {
        using var ecdsa = ECDsa.Create(curve);
        var request = new CertificateRequest("CN=e7-test", ecdsa, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static (QueueDatagramTransport Client, QueueDatagramTransport Server) TransportPair()
    {
        QueueDatagramTransport? client = null;
        QueueDatagramTransport? server = null;
        client = new QueueDatagramTransport(datagram => server!.Enqueue(datagram));
        server = new QueueDatagramTransport(datagram => client.Enqueue(datagram));
        return (client, server);
    }
}
