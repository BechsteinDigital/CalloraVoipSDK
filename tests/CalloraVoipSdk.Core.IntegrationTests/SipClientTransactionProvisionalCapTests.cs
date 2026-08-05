using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-8: a UAC client transaction retained every provisional (1xx) response in an uncapped list. A
/// peer or forking proxy emitting an unbounded 1xx stream within the transaction window would grow it without
/// limit. This test pins the provisional-history cap: the retained snapshot is bounded even under a 1xx flood,
/// while the final response still completes the transaction.
/// </summary>
public sealed class SipClientTransactionProvisionalCapTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 5060);
    private const string Branch = "z9hG4bK-prov-cap";
    private const string CallId = "prov-cap@example.test";

    private static SipClientTransactionRequest InviteRequest() => new()
    {
        Method = "INVITE",
        RequestUri = "sip:bob@example.test",
        RemoteEndPoint = Remote,
        Transport = SipTransportProtocol.Tcp,
        Timeout = TimeSpan.FromSeconds(5),
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = $"SIP/2.0/TCP 198.51.100.7:5060;branch={Branch}",
            ["Max-Forwards"] = "70",
            ["From"] = "<sip:alice@example.test>;tag=uac",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = CallId,
            ["CSeq"] = "1 INVITE",
        },
    };

    private static SipResponse Response(int statusCode, string toTag)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = $"SIP/2.0/TCP 198.51.100.7:5060;branch={Branch}",
            ["From"] = "<sip:alice@example.test>;tag=uac",
            ["To"] = $"<sip:bob@example.test>;tag={toTag}",
            ["Call-ID"] = CallId,
            ["CSeq"] = "1 INVITE",
        };
        return new SipResponse(statusCode, statusCode == 486 ? "Busy Here" : "Ringing", headers, body: string.Empty);
    }

    [Fact]
    public async Task Unbounded_provisional_flood_is_capped_in_the_retained_history()
    {
        using var transport = new CapturingSipTransportRuntime();
        var executor = new SipClientTransactionExecutor(transport, NullLogger.Instance);

        // The subscription is established synchronously inside ExecuteAsync before it awaits the final response.
        var execution = executor.ExecuteAsync(InviteRequest());

        for (var i = 0; i < 200; i++)
            transport.DeliverInboundResponse(Remote, Response(180, $"prov-{i}"));

        // A final response completes the transaction and closes the provisional window.
        transport.DeliverInboundResponse(Remote, Response(486, "final"));

        var result = await execution;

        Assert.Equal(486, result.FinalResponse.Response.StatusCode);
        Assert.True(
            result.ProvisionalResponses.Count <= 64,
            $"retained {result.ProvisionalResponses.Count} provisionals, cap was 64");
    }
}
