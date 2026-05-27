using System.Net;

namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed record CapturedSipRequest(
    string Method,
    string RequestUri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    IPEndPoint RemoteEndPoint);
