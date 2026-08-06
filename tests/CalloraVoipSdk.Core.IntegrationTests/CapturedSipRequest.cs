using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed record CapturedSipRequest(
    string Method,
    string RequestUri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    IPEndPoint RemoteEndPoint)
{
    /// <summary>Per-line TLS identity carried by the send (issue #183), or null for the default identity.</summary>
    public TlsConfiguration? LineTls { get; init; }
}
