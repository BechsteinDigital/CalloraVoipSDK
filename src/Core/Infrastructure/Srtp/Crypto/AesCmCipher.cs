using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// AES Counter-Mode (AES-CM) keystream cipher for SRTP/SRTCP (RFC 3711 §4.1). One instance is created
/// per cryptographic context from its session cipher key and reused for every packet: the AES key
/// schedule (<see cref="Aes"/> + its ECB encryptor) and the two 16-byte working buffers are allocated
/// once here instead of once per packet on the hotpath (HARD-F2). Consolidates the AES-CM XOR that
/// <c>SrtpContext</c> and <c>SrtcpContext</c> previously duplicated.
/// </summary>
/// <remarks>
/// Not thread-safe on its own: the owning context serialises every <see cref="Xor"/> call (and disposal)
/// under its own lock, exactly as when the encryptor and buffers were created inline per call.
/// </remarks>
internal sealed class AesCmCipher : IDisposable
{
    private const int BlockLength = 16;

    // RFC 3711 §4.1: the 16-bit AES-CM block counter caps one keystream at 2^16 blocks (1 MiB).
    private const int MaxKeystreamBytes = 1 << 20;

    private readonly Aes _aes;
    private readonly ICryptoTransform _encryptor;

    // Reused per Xor call (owner-serialised): the counter block fed to AES-ECB and the keystream out.
    private readonly byte[] _counterBlock = new byte[BlockLength];
    private readonly byte[] _keystreamBlock = new byte[BlockLength];

    public AesCmCipher(byte[] cipherKey)
    {
        ArgumentNullException.ThrowIfNull(cipherKey);
        _aes = Aes.Create();
        _aes.Key = cipherKey;
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _encryptor = _aes.CreateEncryptor();
    }

    /// <summary>
    /// XORs the AES-CM keystream — seeded by the 128-bit <paramref name="iv"/> and advanced by a 16-bit
    /// block counter — into <paramref name="data"/> in place (RFC 3711 §4.1). Encryption and decryption
    /// are the same operation.
    /// </summary>
    /// <exception cref="CryptographicException">The payload exceeds the 2^16-block keystream limit.</exception>
    public void Xor(ReadOnlySpan<byte> iv, Span<byte> data)
    {
        if (data.Length > MaxKeystreamBytes)
            throw new CryptographicException("AES-CM payload exceeds the RFC 3711 2^16-block keystream limit.");

        iv.CopyTo(_counterBlock);
        var offset = 0;
        var counter = 0;

        while (offset < data.Length)
        {
            _counterBlock[14] = (byte)(counter >> 8);
            _counterBlock[15] = (byte)counter;

            _encryptor.TransformBlock(_counterBlock, 0, BlockLength, _keystreamBlock, 0);

            var chunk = Math.Min(BlockLength, data.Length - offset);
            for (var i = 0; i < chunk; i++)
                data[offset + i] ^= _keystreamBlock[i];

            offset += chunk;
            counter++;
        }
    }

    public void Dispose()
    {
        _encryptor.Dispose();
        _aes.Dispose();
    }
}
