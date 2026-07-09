namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// ICE agent role (RFC 8445 §6.1.1). The controlling agent nominates the candidate pair used
/// for media; the controlled agent follows the nomination. For full/full ICE the offerer is
/// controlling by default; a role conflict is resolved via the tie-breaker (§7.3.1.1).
/// </summary>
internal enum IceRole
{
    /// <summary>This agent decides which validated pair is nominated.</summary>
    Controlling,

    /// <summary>This agent follows the controlling agent's nomination.</summary>
    Controlled,
}
