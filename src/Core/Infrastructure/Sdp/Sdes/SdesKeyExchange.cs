using System;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Sdes;

/// <summary>
/// SDES (RFC 4568) key exchange helper: generates our own local master key/salt for an
/// accepted crypto suite and composes the negotiated local + remote master keys into the
/// domain-neutral <see cref="SrtpSessionKeyMaterial"/> carrier.
/// </summary>
/// <remarks>
/// RFC 4568 §6.1 requires each party to advertise its <b>own</b> key material — the answerer
/// must never echo the offerer's key back. <see cref="TryCreateLocalCrypto"/> produces a fresh
/// CSPRNG key for our outbound direction; the offerer's key is used only for our inbound direction.
/// </remarks>
internal static class SdesKeyExchange
{
    /// <summary>
    /// Generates a fresh local <c>a=crypto</c> attribute for the suite of an accepted remote
    /// crypto line. Reuses the accepted tag and suite name, but carries a newly generated,
    /// CSPRNG-random master key + salt (RFC 4568 §6.1) — it never reflects the remote key.
    /// Returns <see langword="null"/> when the suite is not supported.
    /// </summary>
    /// <param name="acceptedRemoteCrypto">The remote crypto line this answer accepts.</param>
    public static SdpCryptoAttribute? TryCreateLocalCrypto(SdpCryptoAttribute acceptedRemoteCrypto)
    {
        ArgumentNullException.ThrowIfNull(acceptedRemoteCrypto);

        return TryCreateFreshCrypto(acceptedRemoteCrypto.CryptoSuite, acceptedRemoteCrypto.Tag);
    }

    /// <summary>
    /// Generates a fresh local <c>a=crypto</c> attribute for an outbound SDES <b>offer</b>
    /// (RFC 4568 §6.1). Unlike <see cref="TryCreateLocalCrypto"/> there is no accepted remote
    /// line to mirror, so the caller supplies the desired suite name and context tag. The key
    /// material is CSPRNG-random. Returns <see langword="null"/> when the suite is not supported.
    /// </summary>
    /// <param name="cryptoSuite">Suite name to advertise, e.g. <c>AES_CM_128_HMAC_SHA1_80</c>.</param>
    /// <param name="tag">Crypto context tag (RFC 4568 §9.1); defaults to <c>1</c>.</param>
    public static SdpCryptoAttribute? TryCreateOfferCrypto(string cryptoSuite, int tag = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cryptoSuite);

        return TryCreateFreshCrypto(cryptoSuite, tag);
    }

    /// <summary>
    /// Builds a crypto line carrying a freshly generated CSPRNG master key + salt for the given
    /// suite name and tag. Returns <see langword="null"/> when the suite is unsupported.
    /// </summary>
    private static SdpCryptoAttribute? TryCreateFreshCrypto(string cryptoSuite, int tag)
    {
        if (!SrtpCryptoSuiteMapper.TryParseSuiteName(cryptoSuite, out var suite))
            return null;

        var keyLength = SrtpCryptoSuiteMapper.GetMasterKeyLength(suite);
        var material = RandomNumberGenerator.GetBytes(keyLength + SrtpCryptoSuiteMapper.MasterSaltLength);
        var inline = "inline:" + Convert.ToBase64String(material);

        return new SdpCryptoAttribute
        {
            Tag = tag,
            CryptoSuite = cryptoSuite,
            KeyParams = inline
        };
    }

    /// <summary>
    /// Composes negotiated local and remote <c>a=crypto</c> lines into domain SDES key material.
    /// <paramref name="localCrypto"/> is the key we advertise (outbound protection);
    /// <paramref name="remoteCrypto"/> is the far end's key (inbound unprotection).
    /// Returns <see langword="null"/> when either side is missing, the suites disagree, the suite
    /// is unsupported, or the inline key material is malformed — in every such case SDES keys are
    /// simply not carried (the caller leaves <c>SrtpKeys</c> null).
    /// </summary>
    public static SrtpSessionKeyMaterial? TryBuildSessionKeyMaterial(
        SdpCryptoAttribute? localCrypto,
        SdpCryptoAttribute? remoteCrypto)
    {
        if (localCrypto is null || remoteCrypto is null)
            return null;

        if (!SrtpCryptoSuiteMapper.TryParseSuiteName(remoteCrypto.CryptoSuite, out var suite))
            return null;

        // Answer and offer must agree on the suite (RFC 4568 §6.1); otherwise the key/salt split
        // for the two directions would be inconsistent and the material is unusable.
        if (!SrtpCryptoSuiteMapper.TryParseSuiteName(localCrypto.CryptoSuite, out var localSuite)
            || localSuite != suite)
        {
            return null;
        }

        try
        {
            var local = SrtpKeyMaterial.ParseInline(localCrypto.KeyParams, suite);
            var remote = SrtpKeyMaterial.ParseInline(remoteCrypto.KeyParams, suite);

            return new SrtpSessionKeyMaterial
            {
                Suite = SrtpCryptoSuiteMapper.ToDomainKind(suite),
                LocalMasterKey = local.MasterKey,
                LocalMasterSalt = local.MasterSalt,
                RemoteMasterKey = remote.MasterKey,
                RemoteMasterSalt = remote.MasterSalt
            };
        }
        catch (FormatException)
        {
            // Malformed inline key material (bad base64 or too short). Degrade gracefully:
            // no SDES keys are carried and the caller keeps SrtpKeys null.
            return null;
        }
    }
}
