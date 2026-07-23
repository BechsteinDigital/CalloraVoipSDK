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
    private readonly Dictionary<int, Action<IPEndPoint, SipRequest>> _requestHandlers = new();
    private readonly object _sync = new();
    private TaskCompletionSource<CapturedSipRequest> _nextRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _responseHandlerId;
    private int _requestHandlerId;

    public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 5060);

    public Func<CapturedSipRequest, SipResponse?>? ResponseFactory { get; set; }

    /// <summary>
    /// When set, <see cref="SendRequestAsync"/> throws for requests of this method — used to
    /// exercise send-failure paths (e.g. a failed forked-INVITE ACK).
    /// </summary>
    public string? ThrowOnSendMethod { get; set; }

    /// <summary>
    /// When set, <see cref="SendRequestAsync"/> throws for any request the predicate matches — a finer-grained
    /// alternative to <see cref="ThrowOnSendMethod"/> (e.g. fail only the first PRACK via a captured counter).
    /// </summary>
    public Func<CapturedSipRequest, bool>? ThrowOnSendPredicate { get; set; }

    /// <summary>
    /// When set, the provisional (1xx) responses it returns for a request are dispatched — in order — BEFORE the
    /// final response from <see cref="ResponseFactory"/>. Lets a test deliver reliable provisionals (Require:
    /// 100rel + RSeq) that drive the UAC's PRACK path, then a final response for the same transaction.
    /// </summary>
    public Func<CapturedSipRequest, IReadOnlyList<SipResponse>>? ProvisionalResponsesFactory { get; set; }

    public int ResponseSubscriptionsCreated { get; private set; }

    public void Dispose()
    {
    }

    public IDisposable SubscribeRequests(Action<IPEndPoint, SipRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            var id = ++_requestHandlerId;
            _requestHandlers[id] = handler;
            return new DelegateDisposable(() => RemoveRequestHandler(id));
        }
    }

    /// <summary>
    /// Simulates an inbound datagram by delivering <paramref name="request"/> to every subscribed request
    /// handler (as the real transport would on receiving a request). Responses the handler emits are captured
    /// via <see cref="SnapshotResponses"/>.
    /// </summary>
    public void DeliverInboundRequest(IPEndPoint remoteEndPoint, SipRequest request)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(request);

        Action<IPEndPoint, SipRequest>[] handlers;
        lock (_sync)
            handlers = _requestHandlers.Values.ToArray();

        foreach (var handler in handlers)
            handler(remoteEndPoint, request);
    }

    private void RemoveRequestHandler(int id)
    {
        lock (_sync)
            _requestHandlers.Remove(id);
    }

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
        var request = new CapturedSipRequest(method, requestUri, headers, body, remoteEndPoint);
        ThrowIfConfigured(request);
        Capture(request);
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
        var request = new CapturedSipRequest(method, requestUri, headers, body, remoteEndPoint);
        ThrowIfConfigured(request);
        Capture(request);
        return Task.CompletedTask;
    }

    private void ThrowIfConfigured(CapturedSipRequest request)
    {
        if (ThrowOnSendMethod is not null && request.Method.Equals(ThrowOnSendMethod, StringComparison.Ordinal))
            throw new IOException($"Simulated transport send failure for {request.Method}.");
        if (ThrowOnSendPredicate is not null && ThrowOnSendPredicate(request))
            throw new IOException($"Simulated transport send failure for {request.Method}.");
    }

    private readonly List<(int StatusCode, IReadOnlyDictionary<string, string> Headers, IPEndPoint RemoteEndPoint)> _responses = new();

    /// <summary>Snapshot of the responses sent through this transport (status, headers, destination).</summary>
    public IReadOnlyList<(int StatusCode, IReadOnlyDictionary<string, string> Headers, IPEndPoint RemoteEndPoint)> SnapshotResponses()
    {
        lock (_sync)
            return _responses.ToArray();
    }

    public Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default)
    {
        lock (_sync)
            _responses.Add((statusCode, headers, remoteEndPoint));
        return Task.CompletedTask;
    }

    public Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct = default) =>
        SendResponseAsync(statusCode, reasonPhrase, headers, body, remoteEndPoint, ct);

    public Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        CancellationToken ct = default) =>
        Task.FromResult(new IPEndPoint(IPAddress.Loopback, port));

    public Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        CancellationToken ct = default) =>
        ResolveRemoteEndPointAsync(host, port, ct);

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

    private void Capture(CapturedSipRequest request)
    {
        TaskCompletionSource<CapturedSipRequest> signal;
        lock (_sync)
        {
            _requests.Add(request);
            signal = _nextRequest;
            _nextRequest = new TaskCompletionSource<CapturedSipRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult(request);

        // Reliable provisionals (if any) are delivered in order before the final response, so the UAC's
        // OnProvisionalResponse/PRACK path runs against the same transaction.
        if (ProvisionalResponsesFactory?.Invoke(request) is { } provisionals)
        {
            foreach (var provisional in provisionals)
                DispatchResponse(request.RemoteEndPoint, provisional);
        }

        var response = ResponseFactory?.Invoke(request);
        if (response is not null)
            DispatchResponse(request.RemoteEndPoint, response);
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
