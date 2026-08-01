namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Scheduling class of an outbound ICE connectivity check. Triggered checks preempt nomination checks,
/// which in turn preempt ordinary checklist work (RFC 8445 §6.1.4.2 and §7.3.1.4).
/// </summary>
internal enum IceConnectivityCheckKind
{
    Triggered,
    Nomination,
    Ordinary,
}
