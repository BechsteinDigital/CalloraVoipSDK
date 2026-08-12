using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #157 P2-3: a replayed packet must be rejected <em>before</em> the plaintext allocation and the
/// cipher work. Replaying one recorded valid packet needs no key, and each repeat used to cost an
/// allocation, a full HMAC/GCM verification and a decrypt — an unkeyed attacker could spend our CPU at
/// line rate. The window check is read-only (RFC 3711 §3.3.2), so pulling it forward cannot corrupt
/// state, and it can only reject what the post-authentication check would reject anyway.
/// <para>
/// The ordering is observable without a mock: a replayed packet whose authentication tag has been
/// destroyed now surfaces as a replay rather than an authentication failure — which is only possible
/// if the replay check ran first.
/// </para>
/// </summary>
public sealed class SrtpReplayPrecheckTests
{
    private const int AuthTagLength = 10;
    private const uint Ssrc = 0xCAFEBABE;

    private static SrtpKeyMaterial Material() => SrtpKeyMaterial.ParseInline(
        "inline:" + Convert.ToBase64String(Convert.FromHexString(
            "E1F97A0D3E018BE0D64FA32C06DE4139" + "0EC675AD498AFEEBB6960B3AABE6")),
        SrtpCryptoSuite.AesCm128HmacSha1_80);

    private static byte[] Packet(ushort sequenceNumber, int payloadLength = 32)
    {
        var packet = new byte[12 + payloadLength];
        packet[0] = 0x80;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x11223344);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), Ssrc);
        for (var i = 12; i < packet.Length; i++)
            packet[i] = (byte)i;
        return packet;
    }

    [Fact]
    public void A_replay_is_rejected_before_the_packet_is_authenticated()
    {
        using var sender = new SrtpContext(Material());
        using var receiver = new SrtpContext(Material());

        var wire = sender.Protect(Packet(sequenceNumber: 100));
        receiver.Unprotect(wire);   // accepted: the index enters the replay window

        // Destroy the authentication tag of the replayed copy. Under the old ordering the cipher ran
        // first and this surfaced as an authentication failure; now the replay check rejects it before
        // any allocation or verification happens.
        var replayed = (byte[])wire.Clone();
        replayed[^1] ^= 0xFF;

        Assert.Throws<SrtpReplayException>(() => receiver.Unprotect(replayed));
    }

    [Fact]
    public void An_intact_replay_is_still_rejected_as_a_replay()
    {
        using var sender = new SrtpContext(Material());
        using var receiver = new SrtpContext(Material());

        var wire = sender.Protect(Packet(sequenceNumber: 200));
        receiver.Unprotect(wire);

        Assert.Throws<SrtpReplayException>(() => receiver.Unprotect(wire));
    }

    [Fact]
    public void A_forged_packet_from_an_unknown_ssrc_still_fails_authentication()
    {
        // No window exists for an SSRC we have never accepted, so there is nothing to pre-check and the
        // cipher remains the gate. This is the case that must NOT change: rejecting an unknown source
        // early would mean trusting an unauthenticated header.
        using var receiver = new SrtpContext(Material());

        var forged = new byte[12 + 32 + AuthTagLength];
        forged[0] = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(forged.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(forged.AsSpan(8), 0xDEADBEEF);

        Assert.Throws<SrtpAuthenticationException>(() => receiver.Unprotect(forged));
    }

    [Fact]
    public void A_fresh_sequence_number_is_still_accepted_after_a_rejected_replay()
    {
        // The pre-check must not shift or set anything in the window: a rejected replay may not disturb
        // the sender's ongoing stream.
        using var sender = new SrtpContext(Material());
        using var receiver = new SrtpContext(Material());

        var first = sender.Protect(Packet(sequenceNumber: 300));
        var second = sender.Protect(Packet(sequenceNumber: 301));

        Assert.NotNull(receiver.Unprotect(first));
        Assert.Throws<SrtpReplayException>(() => receiver.Unprotect(first));
        Assert.NotNull(receiver.Unprotect(second));   // unaffected by the rejection in between
    }
}
