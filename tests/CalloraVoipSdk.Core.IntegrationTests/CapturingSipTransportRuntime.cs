using System.IO;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed class CapturingSipTransportRuntime : ISipTransportRuntime
{
    private readonly List<CapturedSipRequest> _requests = new();
    private readonly Dictionary<int, Action<IPEndPoint, SipResponse>> _responseHandlers = new();
    private readonly object _sync = new();
    private TaskCompletionSource<CapturedSipRequest> _nextRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _responseHandlerId;

    public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 5060);

    public Func<CapturedSipRequest, SipResponse?>? ResponseFactory { get; set; }

    /// <summary>
    /// When set, <see cref="SendRequestAsync"/> throws for requests of this method — used to
    /// exercise send-failure paths (e.g. a failed forked-INVITE ACK).
    /// </summary>
    public string? ThrowOnSendMethod { get; set; }

    public int ResponseSubscriptionsCreated { get; private set; }

    public void Dispose()
    {
    }

    public IDisposable SubscribeRequests(Action<IPEndPoint, SipRequest> handler) => NoopDisposable.Instance;

    public IDisposable SubscribeResponses(Action<IPEndPoint, SipResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            ResponseSubscriptionsCreated++;
            var id = ++_responseHandlerId;
            _responseHandlers[id] = handler;
            return new DelegateDisposable(() => RemoveResponseHandler(id));
        }
    }

    public Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default)
    {
        ThrowIfConfigured(method);
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
        ThrowIfConfigured(method);
        Capture(method, requestUri, headers, body, remoteEndPoint);
        return Task.CompletedTask;
    }

    private void ThrowIfConfigured(string method)
    {
        if (ThrowOnSendMethod is not null && method.Equals(ThrowOnSendMethod, StringComparison.Ordinal))
            throw new IOException($"Simulated transport send failure for {method}.");
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

    public IReadOnlyList<CapturedSipRequest> SnapshotRequests()
    {
        lock (_sync)
            return _requests.ToArray();
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

        var response = ResponseFactory?.Invoke(request);
        if (response is not null)
            DispatchResponse(remoteEndPoint, response);
    }

    private void DispatchResponse(IPEndPoint remoteEndPoint, SipResponse response)
    {
        Action<IPEndPoint, SipResponse>[] handlers;
        lock (_sync)
            handlers = _responseHandlers.Values.ToArray();

        foreach (var handler in handlers)
            handler(remoteEndPoint, response);
    }

    private void RemoveResponseHandler(int id)
    {
        lock (_sync)
            _responseHandlers.Remove(id);
    }
}
