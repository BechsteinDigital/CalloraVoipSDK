using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Derives SRTP master keys from a completed DTLS handshake using the TLS keying-material
/// exporter with the <c>EXTRACTOR-dtls_srtp</c> label (RFC 5764 §4.2). The exported block
/// is laid out as <c>client_write_key || server_write_key || client_write_salt ||
/// server_write_salt</c>; which half is "local" depends on the handshake role.
/// </summary>
internal static class DtlsSrtpKeyExporter
{
    private const string ExporterLabel = "EXTRACTOR-dtls_srtp";

    /// <summary>
    /// Exports and splits the SRTP keying material for the negotiated protection profile.
    /// Must be called after the handshake completed (the exporter requires the master
    /// secret and, in this SDK, a negotiated <c>extended_master_secret</c>).
    /// </summary>
    /// <param name="context">The BouncyCastle TLS context of the completed handshake.</param>
    /// <param name="protectionProfile">The negotiated <c>use_srtp</c> protection profile.</param>
    /// <param name="isClient">Whether this endpoint acted as the DTLS client.</param>
    public static DtlsSrtpNegotiatedKeys Export(TlsContext context, int protectionProfile, bool isClient)
    {
        ArgumentNullException.ThrowIfNull(context);

        var suite = DtlsSrtpProfiles.ToCryptoSuite(protectionProfile);
        var keyLength = SrtpCryptoSuiteNames.KeyLength(suite);
        const int saltLength = SrtpCryptoSuiteNames.SaltLength;

        // RFC 5764 §4.2: 2 * (SRTPSecurityParams.master_key_len + master_salt_len).
        var material = context.ExportKeyingMaterial(
            ExporterLabel, context_value: null, length: 2 * (keyLength + saltLength));

        return SplitKeyingMaterial(material, suite, keyLength, saltLength, isClient);
    }

    /// <summary>
    /// Splits the concatenated <c>EXTRACTOR-dtls_srtp</c> output (<c>client_write_key || server_write_key ||
    /// client_write_salt || server_write_salt</c>) into the local/remote halves, copying each half into its own
    /// buffer and then wiping <paramref name="material"/>. Copying (rather than returning aliasing views) is what
    /// lets the concatenated block — which carries <em>both</em> endpoints' write keys and salts — be zeroed here
    /// instead of lingering on the managed heap for the lifetime of the SRTP contexts. Internal for testing.
    /// </summary>
    internal static DtlsSrtpNegotiatedKeys SplitKeyingMaterial(
        byte[] material, SrtpCryptoSuite suite, int keyLength, int saltLength, bool isClient)
    {
        ArgumentNullException.ThrowIfNull(material);

        try
        {
            // Independent copies so the returned key material does not alias — and thereby retain — the full
            // exported block that the finally below wipes.
            var clientKey = material.AsSpan(0, keyLength).ToArray();
            var serverKey = material.AsSpan(keyLength, keyLength).ToArray();
            var clientSalt = material.AsSpan(2 * keyLength, saltLength).ToArray();
            var serverSalt = material.AsSpan(2 * keyLength + saltLength, saltLength).ToArray();

            var (localKey, localSalt) = isClient ? (clientKey, clientSalt) : (serverKey, serverSalt);
            var (remoteKey, remoteSalt) = isClient ? (serverKey, serverSalt) : (clientKey, clientSalt);

            return new DtlsSrtpNegotiatedKeys
            {
                Suite = suite,
                LocalKeys = new SrtpKeyMaterial { MasterKey = localKey, MasterSalt = localSalt, Suite = suite },
                RemoteKeys = new SrtpKeyMaterial { MasterKey = remoteKey, MasterSalt = remoteSalt, Suite = suite },
            };
        }
        finally
        {
            // Key hygiene: the aggregate exporter secret is no longer needed once the halves are copied out.
            CryptographicOperations.ZeroMemory(material);
        }
    }
}
