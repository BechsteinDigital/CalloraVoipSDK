namespace CalloraVoipSdk.InteropHarness.Media;

/// <summary>Codec-Wahl für den <see cref="RtpMediaLoopback"/> (Transport-only, opake Payload).</summary>
public enum LoopbackCodec
{
    /// <summary>PCMU/G.711 µ-law: Payload-Type 0, 8000 Hz, 160 Samples/Paket.</summary>
    Pcmu,

    /// <summary>Opus: dynamischer Payload-Type 111, 48000 Hz, 960 Samples/Paket (RFC 7587).</summary>
    Opus,
}

/// <summary>Transport-Sicherheit für den <see cref="RtpMediaLoopback"/>.</summary>
public enum LoopbackSecurity
{
    /// <summary>Plain RTP (RTP/AVP).</summary>
    Plain,

    /// <summary>SRTP über SDES-Keying (RTP/SAVP, AES_CM_128_HMAC_SHA1_80).</summary>
    Srtp,
}
