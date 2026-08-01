using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>Resolves peer-reachable host endpoints represented by one concrete or wildcard-bound media socket.</summary>
internal interface IWebRtcHostCandidateProvider
{
    /// <summary>Returns deterministic, preferred-first host endpoints using the bound socket port.</summary>
    IReadOnlyList<IPEndPoint> GetHostEndPoints(IPEndPoint boundEndPoint);
}
