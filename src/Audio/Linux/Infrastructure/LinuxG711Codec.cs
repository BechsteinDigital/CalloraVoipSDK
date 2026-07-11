namespace CalloraVoipSdk.Audio.Linux;

/// <summary>
/// G.711 (µ-law / A-law) PCM16 codec for the Linux audio backend. Pure static and stateless;
/// extracted from <see cref="LinuxAudioDevice"/> so the device stays focused and within the
/// per-file size budget. <paramref name="payloadType"/> selects the variant: 0 = µ-law, 8 = A-law.
/// </summary>
internal static class LinuxG711Codec
{
    /// <summary>Encodes PCM16 little-endian bytes into G.711 (µ-law when payloadType 0, else A-law).</summary>
    public static byte[] Encode(byte[] pcm, int payloadType)
    {
        var sampleCount = pcm.Length / 2;
        var encoded = new byte[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            encoded[i] = payloadType == 0 ? MuLawEncode(sample) : ALawEncode(sample);
        }

        return encoded;
    }

    /// <summary>Decodes G.711 bytes (µ-law when payloadType 0, else A-law) into PCM16 little-endian bytes.</summary>
    public static byte[] Decode(byte[] payload, int payloadType)
    {
        var pcm = new byte[payload.Length * 2];
        for (var i = 0; i < payload.Length; i++)
        {
            var sample = payloadType == 0 ? MuLawDecode(payload[i]) : ALawDecode(payload[i]);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }

        return pcm;
    }

    private static byte MuLawEncode(short pcm)
    {
        const int Bias = 0x84;
        const int Clip = 32635;

        var sign = (pcm >> 8) & 0x80;
        if (sign != 0)
            pcm = (short)-pcm;
        if (pcm > Clip)
            pcm = Clip;

        pcm += Bias;
        var exp = MuExp[(pcm >> 7) & 0xFF];
        var mant = (pcm >> (exp + 3)) & 0x0F;
        return (byte)(~(sign | (exp << 4) | mant));
    }

    private static short MuLawDecode(byte mu)
    {
        mu = (byte)~mu;
        var sign = mu & 0x80;
        var exp = (mu >> 4) & 7;
        var mant = mu & 0x0F;
        var value = ((mant << 3) + 0x84) << exp;
        return (short)(sign != 0 ? -(value - 0x84) : value - 0x84);
    }

    private static byte ALawEncode(short pcm)
    {
        var sign = (pcm & 0x8000) != 0 ? 0 : 0x80;
        if (sign == 0)
            pcm = (short)-pcm;
        if (pcm > 32767)
            pcm = 32767;

        var exp = 7;
        for (var mask = 0x4000; (pcm & mask) == 0 && exp > 0; exp--, mask >>= 1)
        {
        }

        var mant = exp == 0 ? pcm >> 1 : (pcm >> (exp + 3)) & 0x0F;
        return (byte)((sign | (exp << 4) | mant) ^ 0x55);
    }

    private static short ALawDecode(byte a)
    {
        a ^= 0x55;
        var sign = a & 0x80;
        var exp = (a >> 4) & 7;
        var mant = a & 0x0F;

        var value = exp == 0
            ? (mant << 1) | 1
            : ((mant | 0x10) << 1 | 1) << (exp - 1);

        value <<= 3;
        return (short)(sign != 0 ? value : -value);
    }

    private static readonly int[] MuExp =
    [
        0,0,1,1,2,2,2,2,3,3,3,3,3,3,3,3,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,
        5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,
        6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
        6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
        7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
        7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7
    ];
}
