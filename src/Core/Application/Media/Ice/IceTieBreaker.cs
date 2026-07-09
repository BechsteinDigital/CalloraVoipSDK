using System.Buffers.Binary;
using System.Security.Cryptography;

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
}
