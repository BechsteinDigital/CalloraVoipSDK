using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Pure SDES crypto selection for SDP answers (RFC 4568 §5.1.2/§5.1.3): picks the first
/// offered <c>a=crypto</c> line whose suite we support and generates fresh local key
/// material for the answer. The answer MUST carry the answerer's <em>own</em> master
/// key/salt — echoing the offerer's key would make both directions derive the same
/// keystream and break the peer's decryption (and any confidentiality claim).
/// </summary>
internal static class SdesCryptoSelector
{
    /// <summary>
    /// One negotiated SDES selection: the local answer attribute (own key) and the
    /// remote offer attribute it responds to (the peer's key, used later to decrypt
    /// the inbound direction).
    /// </summary>
    internal sealed record Selection(SdpCryptoAttribute LocalAnswer, SdpCryptoAttribute RemoteOffer);

    /// <summary>
    /// Selects the first offered crypto line with a supported suite and an inline key,
    /// and builds the matching answer attribute: same tag (RFC 4568 §5.1.2), same suite,
    /// freshly generated local key params. Returns <see langword="null"/> when no offered
    /// line is usable.
    /// </summary>
    public static Selection? SelectAnswer(IReadOnlyList<SdpCryptoAttribute> offered)
    {
        foreach (var line in offered)
        {
            var suite = TryMapSuite(line.CryptoSuite);
            if (suite is null)
                continue;

            // Only inline keying is supported (RFC 4568 §6.1); skip lines we could not
            // parse a remote key from — answering them would negotiate an undecryptable call.
            if (!line.KeyParams.StartsWith("inline:", StringComparison.OrdinalIgnoreCase))
                continue;

            var localAnswer = new SdpCryptoAttribute
            {
                Tag = line.Tag,
                CryptoSuite = line.CryptoSuite,
                KeyParams = GenerateInlineKeyParams(suite.Value)
            };
            return new Selection(localAnswer, line);
        }

        return null;
    }

    /// <summary>
    /// Maps an RFC 4568/6188 suite token to the implemented <see cref="SrtpCryptoSuite"/>.
    /// Suite names are case-sensitive tokens; unknown suites yield <see langword="null"/>.
    /// </summary>
    public static SrtpCryptoSuite? TryMapSuite(string suiteName) =>
        SrtpCryptoSuiteNames.TryParse(suiteName);

    /// <summary>
    /// Builds the local crypto line for an SDES offer (RFC 4568 §5.1.1): tag 1, the
    /// mandatory-to-implement <c>AES_CM_128_HMAC_SHA1_80</c> suite, and fresh inline key
    /// material we retain as the outbound encrypt key. A single offered suite keeps the
    /// later offer/answer key match unambiguous (the peer answers with the same suite).
    /// </summary>
    public static SdpCryptoAttribute BuildDefaultOffer() => new()
    {
        Tag = 1,
        CryptoSuite = SrtpCryptoSuiteNames.DefaultSuiteName,
        KeyParams = GenerateInlineKeyParams(SrtpCryptoSuite.AesCm128HmacSha1_80)
    };

    /// <summary>
    /// Generates fresh master key + salt for one suite as an SDES inline key param
    /// (RFC 4568 §6.1): base64(key || salt), 16+14 bytes for AES-128 suites and
    /// 32+14 bytes for AES-256 suites (RFC 3711 §3.2.1 / RFC 6188).
    /// </summary>
    public static string GenerateInlineKeyParams(SrtpCryptoSuite suite)
    {
        var material = RandomNumberGenerator.GetBytes(
            SrtpCryptoSuiteNames.KeyLength(suite) + SrtpCryptoSuiteNames.SaltLength);
        return $"inline:{Convert.ToBase64String(material)}";
    }
}
