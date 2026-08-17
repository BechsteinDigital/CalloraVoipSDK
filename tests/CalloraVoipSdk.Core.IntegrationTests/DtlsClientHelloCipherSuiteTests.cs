using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L1 — #323: what our ClientHello actually offers. #229 removed the CBC suite from the server's list, but
/// the client role does not pin one at all (<c>DtlsSrtpClient</c> derives from BouncyCastle's
/// <c>DefaultTlsClient</c> without overriding <c>GetCipherSuites</c>), so what we advertise when the peer
/// answers <c>a=setup:passive</c> was an open question rather than a known fact.
/// </summary>
/// <remarks>
/// Measured off the wire, not read out of the library: the assertion parses the cipher-suite list from the
/// real first flight of a loopback handshake. That also makes it the regression guard #323 asks for — a
/// BouncyCastle update that widens the default list shows up here instead of in a packet capture during a
/// certification review.
/// </remarks>
public sealed class DtlsClientHelloCipherSuiteTests
{
    // The suites BouncyCastle's default list contributes that use AES-CBC. Whether they are offered is the
    // measurement; the numbers are from the TLS registry (RFC 8422 / RFC 5289).
    private static readonly Dictionary<ushort, string> CbcSuites = new()
    {
        [0xC023] = "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256",
        [0xC024] = "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384",
        [0xC009] = "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA",
        [0xC00A] = "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA",
        [0xC027] = "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256",
        [0xC028] = "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384",
        [0xC013] = "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA",
        [0xC014] = "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA",
        [0x0067] = "TLS_DHE_RSA_WITH_AES_128_CBC_SHA256",
        [0x006B] = "TLS_DHE_RSA_WITH_AES_256_CBC_SHA256",
        [0x0033] = "TLS_DHE_RSA_WITH_AES_128_CBC_SHA",
        [0x0039] = "TLS_DHE_RSA_WITH_AES_256_CBC_SHA",
        [0x003C] = "TLS_RSA_WITH_AES_128_CBC_SHA256",
        [0x003D] = "TLS_RSA_WITH_AES_256_CBC_SHA256",
        [0x002F] = "TLS_RSA_WITH_AES_128_CBC_SHA",
        [0x0035] = "TLS_RSA_WITH_AES_256_CBC_SHA",
    };

    /// <summary>
    /// Captures the ClientHello of a real handshake and reports which CBC suites it offers. The assertion is
    /// the finding: an empty list means #323 is moot, a non-empty one names exactly what a reviewer would see.
    /// </summary>
    [Fact]
    public async Task The_client_hello_offers_no_cbc_cipher_suites()
    {
        var offered = await CaptureClientHelloCipherSuitesAsync();

        Assert.NotEmpty(offered); // the capture worked at all
        var cbc = offered.Where(CbcSuites.ContainsKey).Select(s => CbcSuites[s]).ToArray();

        Assert.True(
            cbc.Length == 0,
            $"The ClientHello offers {cbc.Length} CBC suite(s): {string.Join(", ", cbc)}. "
            + $"Full list ({offered.Count}): {string.Join(", ", offered.Select(s => $"0x{s:X4}"))}");
    }

    /// <summary>
    /// Pins the exact list, in order. A BouncyCastle update that adds a suite — or a filter that drops one it
    /// should not — shows up here rather than in a packet capture during a certification review. Deliberately
    /// brittle: that is what a wire-visible inventory is for.
    /// </summary>
    [Fact]
    public async Task The_client_hello_offers_exactly_this_list()
    {
        var offered = await CaptureClientHelloCipherSuitesAsync();

        Assert.Equal(
            new ushort[]
            {
                0x1301, // TLS_AES_128_GCM_SHA256          (TLS 1.3 suite, inert on our DTLS 1.2-only client)
                0x1303, // TLS_CHACHA20_POLY1305_SHA256    (same)
                0xC02B, // TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256  ← what a browser picks
                0xCCA9, // TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256
                0xC02F, // TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256    ← keeps RSA-certificate peers usable
                0xCCA8, // TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
                0x009E, // TLS_DHE_RSA_WITH_AES_128_GCM_SHA256
                0xCCAA, // TLS_DHE_RSA_WITH_CHACHA20_POLY1305_SHA256
                0x009C, // TLS_RSA_WITH_AES_128_GCM_SHA256          (AEAD, but static RSA — no forward secrecy)
                0x00FF, // TLS_EMPTY_RENEGOTIATION_INFO_SCSV        (a signalling value, not a suite)
            },
            offered);
    }

    // Runs one loopback handshake far enough to see the client's first flight and parses its cipher-suite
    // list. The handshake itself is allowed to fail — only the first datagram is of interest.
    private static async Task<IReadOnlyList<ushort>> CaptureClientHelloCipherSuitesAsync()
    {
        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueDatagramTransport? server = null;
        var client = new QueueDatagramTransport(datagram =>
        {
            captured.TrySetResult(datagram.ToArray());
            server!.Enqueue(datagram);
        });
        server = new QueueDatagramTransport(datagram => { /* the server's flight is not needed */ });

        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var handshaker = new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var handshake = handshaker.HandshakeAsync(
            DtlsRole.Client, client, clientCertificate, serverCertificate.Fingerprint, timeout.Token);

        var first = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        timeout.Cancel();
        try { await handshake; } catch { /* only the first flight is under test */ }

        return ParseClientHelloCipherSuites(first);
    }

    // DTLS 1.2 record (RFC 6347 §4.1) + handshake header (§4.2.2) + ClientHello body (RFC 5246 §7.4.1.2).
    private static IReadOnlyList<ushort> ParseClientHelloCipherSuites(byte[] datagram)
    {
        var span = datagram.AsSpan();
        Assert.True(span.Length > 25, "datagram too short to be a ClientHello");
        Assert.Equal(22, span[0]);  // ContentType.handshake
        Assert.Equal(1, span[13]);  // HandshakeType.client_hello

        var offset = 13 + 12;       // record header + DTLS handshake header
        offset += 2 + 32;           // client_version + random
        offset += 1 + span[offset]; // session_id
        offset += 1 + span[offset]; // cookie (DTLS)

        var listLength = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
        offset += 2;
        var suites = new List<ushort>(listLength / 2);
        for (var i = 0; i < listLength; i += 2)
            suites.Add(BinaryPrimitives.ReadUInt16BigEndian(span[(offset + i)..]));

        return suites;
    }
}
