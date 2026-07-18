using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// A remote ICE candidate the connectivity-check driver may nominate (RFC 8445 §5.1.3): the transport
/// address a check is sent to and the candidate's priority (RFC 8445 §5.1.2.1), which orders the checks
/// so the highest-priority responding pair is nominated first.
/// </summary>
/// <param name="EndPoint">The remote transport address a connectivity check is sent to.</param>
/// <param name="Priority">The candidate priority used to order checks (higher is preferred).</param>
internal readonly record struct IceRemoteCandidate(IPEndPoint EndPoint, long Priority);
