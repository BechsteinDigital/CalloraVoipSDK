using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp;

/// <summary>
/// Video codec capabilities the SDK can negotiate (WebRTC phase 2): VP8 (RFC 7741)
/// and H.264 (RFC 6184), both at the mandatory 90 kHz RTP clock. Single source of
/// truth for SDP negotiation and media-parameter extraction.
/// </summary>
internal static class VideoCodecCatalog
{
    /// <summary>Default preference order: VP8 first (WebRTC baseline), then H.264.</summary>
    private static readonly IReadOnlyList<SdpCodecDefinition> Defaults =
    [
        new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 },
        new SdpCodecDefinition { PayloadType = 97, Name = "H264", ClockRate = 90000 },
    ];

    /// <summary>
    /// Resolves an ordered name preference to the supported codec definitions.
    /// Unknown names are ignored; <see langword="null"/> or no match yields the defaults.
    /// </summary>
    public static IReadOnlyList<SdpCodecDefinition> Resolve(IReadOnlyList<string>? preferredNames)
    {
        if (preferredNames is null || preferredNames.Count == 0)
            return Defaults;

        var resolved = preferredNames
            .Select(name => Defaults.FirstOrDefault(
                c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c is not null)
            .Cast<SdpCodecDefinition>()
            .ToArray();

        return resolved.Length > 0 ? resolved : Defaults;
    }

    /// <summary>
    /// True when the codec name is a video codec the SDK's packetisation layer supports.
    /// </summary>
    public static bool IsSupported(string codecName) =>
        Defaults.Any(c => c.Name.Equals(codecName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the fmtp of the given payload type explicitly declares
    /// <c>packetization-mode=1</c> (RFC 6184 §8.1). Absence means mode 0 — a peer that
    /// cannot receive the FU-A fragments the packetisation layer emits.
    /// </summary>
    public static bool HasPacketizationMode1(IReadOnlyList<SdpFmtpAttribute> fmtp, int payloadType) =>
        fmtp.Any(f => f.PayloadType == payloadType
                      && f.Parameters.Replace(" ", string.Empty, StringComparison.Ordinal)
                          .Contains("packetization-mode=1", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Offer-side fmtp lines for the given video codecs: H.264 announces
    /// <c>packetization-mode=1</c> (RFC 6184 §8.1 — matches the FU-A/STAP-A capabilities
    /// of the packetisation layer); VP8 needs no parameters.
    /// </summary>
    public static IReadOnlyList<SdpFmtpAttribute> BuildFmtp(IReadOnlyList<SdpCodecDefinition> codecs) =>
        codecs
            .Where(c => c.Name.Equals("H264", StringComparison.OrdinalIgnoreCase))
            .Select(c => new SdpFmtpAttribute { PayloadType = c.PayloadType, Parameters = "packetization-mode=1" })
            .ToArray();
}
