using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14 #10: after the DTLS-SRTP exporter splits the concatenated keying material, the aggregate secret
/// (which carries both endpoints' write keys and salts) is wiped and the returned halves are independent copies —
/// not aliasing views into the block that gets zeroed. Both properties are asserted together: the returned keys
/// still hold their original bytes even though the source buffer is now all-zero.
/// </summary>
public sealed class DtlsSrtpKeyExporterTests
{
    private const int KeyLength = 16;  // AES-CM-128
    private const int SaltLength = 14; // RFC 3711 §3.2.1

    // client_write_key || server_write_key || client_write_salt || server_write_salt (RFC 5764 §4.2).
    private static byte[] BuildMaterial()
    {
        var material = new byte[2 * (KeyLength + SaltLength)];
        material.AsSpan(0, KeyLength).Fill(0x11);                          // client key
        material.AsSpan(KeyLength, KeyLength).Fill(0x22);                  // server key
        material.AsSpan(2 * KeyLength, SaltLength).Fill(0x33);            // client salt
        material.AsSpan(2 * KeyLength + SaltLength, SaltLength).Fill(0x44); // server salt
        return material;
    }

    [Fact]
    public void Client_role_maps_client_half_to_local_and_wipes_the_aggregate()
    {
        var material = BuildMaterial();

        var keys = DtlsSrtpKeyExporter.SplitKeyingMaterial(
            material, SrtpCryptoSuite.AesCm128HmacSha1_80, KeyLength, SaltLength, isClient: true);

        Assert.True(keys.LocalKeys.MasterKey.Span.SequenceEqual(Repeat(0x11, KeyLength)));
        Assert.True(keys.LocalKeys.MasterSalt.Span.SequenceEqual(Repeat(0x33, SaltLength)));
        Assert.True(keys.RemoteKeys.MasterKey.Span.SequenceEqual(Repeat(0x22, KeyLength)));
        Assert.True(keys.RemoteKeys.MasterSalt.Span.SequenceEqual(Repeat(0x44, SaltLength)));

        // The source block is wiped; the returned keys keeping their bytes proves they are independent copies
        // (an aliasing view would have been zeroed along with the block).
        Assert.True(material.AsSpan().IndexOfAnyExcept((byte)0) < 0, "aggregate keying material was not wiped");
    }

    [Fact]
    public void Server_role_maps_server_half_to_local()
    {
        var material = BuildMaterial();

        var keys = DtlsSrtpKeyExporter.SplitKeyingMaterial(
            material, SrtpCryptoSuite.AesCm128HmacSha1_80, KeyLength, SaltLength, isClient: false);

        Assert.True(keys.LocalKeys.MasterKey.Span.SequenceEqual(Repeat(0x22, KeyLength)));
        Assert.True(keys.LocalKeys.MasterSalt.Span.SequenceEqual(Repeat(0x44, SaltLength)));
        Assert.True(keys.RemoteKeys.MasterKey.Span.SequenceEqual(Repeat(0x11, KeyLength)));
        Assert.True(keys.RemoteKeys.MasterSalt.Span.SequenceEqual(Repeat(0x33, SaltLength)));

        // The wipe runs in the finally, independent of role.
        Assert.True(material.AsSpan().IndexOfAnyExcept((byte)0) < 0, "aggregate keying material was not wiped");
    }

    [Fact]
    public void Disposing_the_negotiated_keys_wipes_both_master_halves()
    {
        var material = BuildMaterial();

        var keys = DtlsSrtpKeyExporter.SplitKeyingMaterial(
            material, SrtpCryptoSuite.AesCm128HmacSha1_80, KeyLength, SaltLength, isClient: true);

        // Sanity: the per-context master halves still hold their bytes before disposal.
        Assert.True(keys.LocalKeys.MasterKey.Span.IndexOfAnyExcept((byte)0) >= 0);
        Assert.True(keys.RemoteKeys.MasterKey.Span.IndexOfAnyExcept((byte)0) >= 0);

        keys.Dispose();

        // Once the SRTP/SRTCP contexts have derived their session keys, disposing the negotiated keys
        // wipes the retained per-context master key/salt buffers (RFC 3711 §9.4) — nothing lingers.
        Assert.True(keys.LocalKeys.MasterKey.Span.IndexOfAnyExcept((byte)0) < 0, "local master key was not wiped");
        Assert.True(keys.LocalKeys.MasterSalt.Span.IndexOfAnyExcept((byte)0) < 0, "local master salt was not wiped");
        Assert.True(keys.RemoteKeys.MasterKey.Span.IndexOfAnyExcept((byte)0) < 0, "remote master key was not wiped");
        Assert.True(keys.RemoteKeys.MasterSalt.Span.IndexOfAnyExcept((byte)0) < 0, "remote master salt was not wiped");
    }

    private static byte[] Repeat(byte value, int count)
    {
        var buffer = new byte[count];
        buffer.AsSpan().Fill(value);
        return buffer;
    }
}
