using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed class CapturingSipTransportRuntime : ISipTransportRuntime
{
    private readonly List<CapturedSipRequest> _requests = new();
    private readonly object _sync = new();
    private TaskCompletionSource<CapturedSipRequest> _nextRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 5060);

    public void Dispose()
    {
    }

    public IDisposable SubscribeRequests(Action<IPEndPoint, SipRequest> handler) => NoopDisposable.Instance;

    public IDisposable SubscribeResponses(Action<IPEndPoint, SipResponse> handler) => NoopDisposable.Instance;

    public Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default)
    {
        Capture(method, requestUri, headers, body, remoteEndPoint);
        return Task.CompletedTask;
    }

    public Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct = default)
    {
        Capture(method, requestUri, headers, body, remoteEndPoint);
        return Task.CompletedTask;
    }

    public Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        CancellationToken ct = default) =>
        Task.FromResult(new IPEndPoint(IPAddress.Loopback, port));

    public Task<IReadOnlyList<SipRouteCandidate>> ResolveRemoteRouteCandidatesAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SipRouteCandidate>>(
            [new SipRouteCandidate { EndPoint = new IPEndPoint(IPAddress.Loopback, port), Transport = transport, Source = "test" }]);

    public IPEndPoint GetLocalEndPoint(SipTransportProtocol transport) => LocalEndPoint;

    public Task<CapturedSipRequest> WaitForRequestAsync(string method, TimeSpan timeout)
    {
        lock (_sync)
        {
            var existing = _requests.FirstOrDefault(r => r.Method.Equals(method, StringComparison.Ordinal));
            if (existing is not null)
                return Task.FromResult(existing);

            return _nextRequest.Task.WaitAsync(timeout);
        }
    }

    private void Capture(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint)
    {
        var request = new CapturedSipRequest(method, requestUri, headers, body, remoteEndPoint);
        TaskCompletionSource<CapturedSipRequest> signal;
        lock (_sync)
        {
            _requests.Add(request);
            signal = _nextRequest;
            _nextRequest = new TaskCompletionSource<CapturedSipRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult(request);
    }
}
