using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The shared AES-CM keystream cipher (HARD-F2). It replaces the per-packet <c>Aes.Create()</c> +
/// <c>CreateEncryptor()</c> + two <c>byte[16]</c> that <c>SrtpContext</c>/<c>SrtcpContext</c> allocated
/// inline: the key schedule and buffers are created once per context and reused. It must be
/// byte-identical to that fresh-per-call implementation and allocate far less over many packets.
/// </summary>
public sealed class AesCmCipherTests
{
    private static readonly byte[] Key = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");

    private static byte[] Iv()
    {
        var iv = new byte[16];
        for (var i = 0; i < iv.Length; i++)
            iv[i] = (byte)(i * 11 + 3);
        return iv;
    }

    private static byte[] Payload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
            payload[i] = (byte)(i * 7 + 1);
        return payload;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(160)]
    [InlineData(1000)]
    public void Xor_matches_a_fresh_per_call_aes_cm_reference(int length)
    {
        var iv = Iv();
        var data = Payload(length);
        var expected = ReferenceAesCm(Key, iv, (byte[])data.Clone());

        using var cipher = new AesCmCipher(Key);
        cipher.Xor(iv, data);

        Assert.Equal(expected, data);
    }

    [Fact]
    public void Xor_is_self_inverse_across_reused_calls()
    {
        var iv = Iv();
        var original = Payload(200);
        var data = (byte[])original.Clone();

        using var cipher = new AesCmCipher(Key);
        cipher.Xor(iv, data);            // encrypt
        Assert.NotEqual(original, data);
        cipher.Xor(iv, data);            // same keystream (re-seeded from iv) decrypts back
        Assert.Equal(original, data);
    }

    [Fact]
    public void Reused_cipher_allocates_less_than_a_fresh_aes_per_packet()
    {
        const int packets = 20_000;
        var iv = Iv();
        var payload = Payload(160);

        // Warm up the JIT so the measurement reflects steady-state allocation.
        RunReused(iv, payload, 200);
        RunFresh(iv, payload, 200);

        var reused = MeasureAllocation(() => RunReused(iv, payload, packets));
        var fresh = MeasureAllocation(() => RunFresh(iv, payload, packets));

        Assert.True(
            reused < fresh,
            $"Expected reused ({reused} B) < fresh-per-packet ({fresh} B) over {packets} packets.");
    }

    private static void RunReused(byte[] iv, byte[] payload, int packets)
    {
        using var cipher = new AesCmCipher(Key);
        var buffer = (byte[])payload.Clone();
        for (var i = 0; i < packets; i++)
            cipher.Xor(iv, buffer);
    }

    private static void RunFresh(byte[] iv, byte[] payload, int packets)
    {
        var buffer = (byte[])payload.Clone();
        for (var i = 0; i < packets; i++)
            ReferenceAesCm(Key, iv, buffer);
    }

    // The pre-F2 approach: a fresh Aes + encryptor + two byte[16] per call.
    private static byte[] ReferenceAesCm(byte[] key, ReadOnlySpan<byte> iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();

        var block = new byte[16];
        var counter = new byte[16];
        iv.CopyTo(counter);
        var offset = 0;
        var c = 0;
        while (offset < data.Length)
        {
            counter[14] = (byte)(c >> 8);
            counter[15] = (byte)c;
            enc.TransformBlock(counter, 0, 16, block, 0);
            var chunk = Math.Min(16, data.Length - offset);
            for (var i = 0; i < chunk; i++)
                data[offset + i] ^= block[i];
            offset += chunk;
            c++;
        }
        return data;
    }

    private static long MeasureAllocation(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
