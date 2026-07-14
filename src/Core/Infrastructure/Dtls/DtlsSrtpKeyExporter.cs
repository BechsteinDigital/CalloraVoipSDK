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

        var clientKey = material.AsMemory(0, keyLength);
        var serverKey = material.AsMemory(keyLength, keyLength);
        var clientSalt = material.AsMemory(2 * keyLength, saltLength);
        var serverSalt = material.AsMemory(2 * keyLength + saltLength, saltLength);

        var (localKey, localSalt) = isClient ? (clientKey, clientSalt) : (serverKey, serverSalt);
        var (remoteKey, remoteSalt) = isClient ? (serverKey, serverSalt) : (clientKey, clientSalt);

        return new DtlsSrtpNegotiatedKeys
        {
            Suite = suite,
            LocalKeys = new SrtpKeyMaterial { MasterKey = localKey, MasterSalt = localSalt, Suite = suite },
            RemoteKeys = new SrtpKeyMaterial { MasterKey = remoteKey, MasterSalt = remoteSalt, Suite = suite },
        };
    }
}
