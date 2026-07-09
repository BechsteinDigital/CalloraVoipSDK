using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// The 64-bit ICE tie-breaker (RFC 8445 §5.2) an agent picks once at start-up and carries in
/// the ICE-CONTROLLING / ICE-CONTROLLED attribute of every connectivity check; it resolves a
/// role conflict when both agents believe they hold the same role (see <see cref="IceRoleConflict"/>).
/// </summary>
internal static class IceTieBreaker
{
    /// <summary>Generates a cryptographically random 64-bit tie-breaker.</summary>
    public static ulong Generate()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    /// <summary>
    /// Derives a stable 64-bit tie-breaker deterministically from a per-session seed (the local
    /// ICE password). This lets the outbound and inbound halves of one agent compute the <em>same</em>
    /// tie-breaker independently, so a role conflict (RFC 8445 §7.3.1.1) resolves identically in
    /// both directions. The local password is itself random per session (RFC 8445 §5.3), so the
    /// result satisfies the §5.2 "random 64-bit number" requirement while staying reproducible.
    /// </summary>
    /// <param name="seed">The per-session seed, typically the local ICE password.</param>
    public static ulong Derive(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
