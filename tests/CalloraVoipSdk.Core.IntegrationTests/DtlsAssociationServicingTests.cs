using System.Collections.Concurrent;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Servicing the DTLS-SRTP association after key export (#190): a control-receive loop must notice a
/// peer <c>close_notify</c>, surface it to the session owner, and discard stray DTLS application_data
/// in pure-SRTP mode. These first tests pin down the BouncyCastle post-handshake semantics our loop
/// relies on (the issue recommends nailing that empirically first).
/// </summary>
public sealed class DtlsAssociationServicingTests
{
    private static readonly byte[] LoopbackServerCookieClientId = { 127, 0, 0, 1, 0, 0 };

    [Fact]
    public async Task Probe_PeerCloseNotify_SurfacesAsTlsFatalAlert_AndClosesTheTransport()
    {
        var pair = await RunLoopbackHandshakeAsync();
        using var clientResult = pair.ClientResult;

        // Server closes its association: DtlsTransport.Close() sends close_notify, which the loopback
        // bridge hands synchronously to the client's inbound queue.
        pair.ServerResult.Dispose();

        // Empirically pinned: BouncyCastle processes the close_notify, closes the underlying
        // transport, then keeps reading — which hits our closed QueueDatagramTransport (it throws
        // ObjectDisposedException to fail fast) and BC surfaces that as TlsFatalAlert(internal_error).
        // So a peer close is NOT a clean -1; the control-receive loop must key off IsClosed instead.
        var buffer = new byte[1500];
        var ex = Assert.Throws<TlsFatalAlert>(() => clientResult.Transport.Receive(buffer, 2000));

        Assert.Equal(AlertDescription.internal_error, ex.AlertDescription);
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
        Assert.True(
            pair.ClientTransport.IsClosed,
            "BouncyCastle closes the underlying transport after processing a peer close_notify");
    }

    [Fact]
    public async Task Probe_PeerApplicationData_SurfacesAsPositiveReceiveLength_TransportStaysOpen()
    {
        var pair = await RunLoopbackHandshakeAsync();
        using var clientResult = pair.ClientResult;
        using var serverResult = pair.ServerResult;

        // A peer that sends DTLS application_data after the handshake (not something our stack does,
        // but a misbehaving/renegotiating peer might): it arrives as a positive-length Receive.
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        serverResult.Transport.Send(payload, 0, payload.Length);

        var buffer = new byte[1500];
        var received = clientResult.Transport.Receive(buffer, 2000);

        Assert.Equal(payload.Length, received);
        Assert.False(pair.ClientTransport.IsClosed); // application_data does not close the association
    }

    // -------------------------------------------------------------------------
    // BouncyCastle control-channel adapter (over a real loopback handshake)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ControlChannel_MapsPeerCloseNotify_ToClosed()
    {
        var pair = await RunLoopbackHandshakeAsync();
        using var clientResult = pair.ClientResult;
        var channel = new BouncyCastleDtlsControlChannel(clientResult.Transport, pair.ClientTransport);

        pair.ServerResult.Dispose(); // close_notify

        var buffer = new byte[1500];
        Assert.Equal(DtlsControlSignal.Closed, channel.Receive(buffer, 2000).Signal);
    }

    [Fact]
    public async Task ControlChannel_MapsPeerApplicationData_ToApplicationDataWithLength()
    {
        var pair = await RunLoopbackHandshakeAsync();
        using var clientResult = pair.ClientResult;
        using var serverResult = pair.ServerResult;
        var channel = new BouncyCastleDtlsControlChannel(clientResult.Transport, pair.ClientTransport);

        var payload = new byte[] { 9, 8, 7 };
        serverResult.Transport.Send(payload, 0, payload.Length);

        var buffer = new byte[1500];
        var result = channel.Receive(buffer, 2000);
        Assert.Equal(DtlsControlSignal.ApplicationData, result.Signal);
        Assert.Equal(payload.Length, result.Length);
    }

    [Fact]
    public async Task ControlChannel_MapsNoTraffic_ToTimeout()
    {
        var pair = await RunLoopbackHandshakeAsync();
        using var clientResult = pair.ClientResult;
        using var serverResult = pair.ServerResult;
        var channel = new BouncyCastleDtlsControlChannel(clientResult.Transport, pair.ClientTransport);

        var buffer = new byte[1500];
        Assert.Equal(DtlsControlSignal.Timeout, channel.Receive(buffer, 100).Signal);
    }

    // -------------------------------------------------------------------------
    // Control-receive loop (over a scripted fake channel — no live handshake needed)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReceiveLoop_DiscardsApplicationData_AndCountsIt_WithoutClosing()
    {
        var channel = new FakeControlChannel();
        var peerClosed = 0;
        await using var receiver = new DtlsAssociationReceiver(
            channel, 1500, () => Interlocked.Increment(ref peerClosed),
            NullLogger.Instance, CancellationToken.None);
        receiver.Start();

        channel.Script(new DtlsControlReceiveResult(DtlsControlSignal.ApplicationData, 5));
        channel.Script(new DtlsControlReceiveResult(DtlsControlSignal.ApplicationData, 7));

        Assert.True(await WaitUntilAsync(() => receiver.DiscardedApplicationDataRecords == 2));
        Assert.Equal(0, Volatile.Read(ref peerClosed)); // application_data must not close the association
    }

    [Fact]
    public async Task ReceiveLoop_OnPeerClose_NotifiesTheOwner()
    {
        var channel = new FakeControlChannel();
        var peerClosed = 0;
        await using var receiver = new DtlsAssociationReceiver(
            channel, 1500, () => Interlocked.Increment(ref peerClosed),
            NullLogger.Instance, CancellationToken.None);
        receiver.Start();

        channel.Script(new DtlsControlReceiveResult(DtlsControlSignal.Closed, 0));

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref peerClosed) == 1));
    }

    [Fact]
    public async Task ReceiveLoop_OnOurTeardown_DoesNotNotifyPeerClosed()
    {
        var channel = new FakeControlChannel(); // only ever yields Timeout
        var peerClosed = 0;
        using var cts = new CancellationTokenSource();
        var receiver = new DtlsAssociationReceiver(
            channel, 1500, () => Interlocked.Increment(ref peerClosed),
            NullLogger.Instance, cts.Token);
        receiver.Start();

        await Task.Delay(50); // let it spin on timeouts
        cts.Cancel();
        await receiver.DisposeAsync();

        Assert.Equal(0, Volatile.Read(ref peerClosed)); // our own teardown is not a peer close
    }

    [Fact]
    public async Task ReceiveLoop_OnControlChannelFault_FailsClosed_AndNotifiesPeerClosed()
    {
        var channel = new FakeControlChannel();
        var peerClosed = 0;
        await using var receiver = new DtlsAssociationReceiver(
            channel, 1500, () => Interlocked.Increment(ref peerClosed),
            NullLogger.Instance, CancellationToken.None);
        receiver.Start();

        channel.ThrowOnNextReceive();

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref peerClosed) == 1));
    }

    [Fact]
    public async Task ReceiveLoop_WhenTransportReportsClosedDuringOurTeardown_DoesNotNotifyPeerClosed()
    {
        // The real teardown race: we cancel, and only then does the transport report Closed — because
        // WE closed it. That must NOT be surfaced as a peer close (else every normal hangup would).
        var channel = new GatedControlChannel();
        var peerClosed = 0;
        using var cts = new CancellationTokenSource();
        var receiver = new DtlsAssociationReceiver(
            channel, 1500, () => Interlocked.Increment(ref peerClosed),
            NullLogger.Instance, cts.Token);
        receiver.Start();

        await channel.FirstReceiveEntered;                                       // loop is inside Receive
        cts.Cancel();                                                            // our teardown begins
        channel.ReleaseWith(new DtlsControlReceiveResult(DtlsControlSignal.Closed, 0)); // transport now reports closed
        await receiver.DisposeAsync();

        Assert.Equal(0, Volatile.Read(ref peerClosed));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(15);
        }

        return condition();
    }

    private sealed class FakeControlChannel : IDtlsControlChannel
    {
        private readonly BlockingCollection<DtlsControlReceiveResult> _scripted = new();
        private volatile bool _throwOnReceive;

        public void Script(DtlsControlReceiveResult result) => _scripted.Add(result);

        public void ThrowOnNextReceive() => _throwOnReceive = true;

        public DtlsControlReceiveResult Receive(Span<byte> buffer, int waitMillis)
        {
            if (_throwOnReceive)
                throw new InvalidOperationException("simulated control-channel fault");

            return _scripted.TryTake(out var result, waitMillis)
                ? result
                : new DtlsControlReceiveResult(DtlsControlSignal.Timeout, 0);
        }
    }

    private sealed class GatedControlChannel : IDtlsControlChannel
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<DtlsControlReceiveResult> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstReceiveEntered => _entered.Task;

        public void ReleaseWith(DtlsControlReceiveResult result) => _release.TrySetResult(result);

        public DtlsControlReceiveResult Receive(Span<byte> buffer, int waitMillis)
        {
            _entered.TrySetResult();
            return _release.Task.GetAwaiter().GetResult(); // block the loop until the test releases it
        }
    }

    // -------------------------------------------------------------------------
    // End-to-end: two attachments in loopback, one closes -> the other's owner is notified
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Attachment_WhenPeerClosesTheAssociation_NotifiesTheOwner()
    {
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var clientEndpoint = new IPEndPoint(IPAddress.Loopback, 5000);
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 5001);

        var clientReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientPeerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        DtlsMediaAttachment client = null!;
        DtlsMediaAttachment server = null!;
        client = DtlsMediaAttachment.Create(
            isClient: true, serverEndpoint, serverCertificate.Fingerprint,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), clientCertificate,
            sendRaw: (datagram, _, _) =>
            {
                server.OnDtlsPacketReceived(datagram.ToArray(), clientEndpoint);
                return ValueTask.CompletedTask;
            },
            onContextsReady: (_, _, _, _) => clientReady.TrySetResult(),
            onHandshakeFailed: () => clientReady.TrySetException(new InvalidOperationException("client handshake failed")),
            NullLoggerFactory.Instance,
            onPeerClosed: () => clientPeerClosed.TrySetResult());
        server = DtlsMediaAttachment.Create(
            isClient: false, clientEndpoint, clientCertificate.Fingerprint,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), serverCertificate,
            sendRaw: (datagram, _, _) =>
            {
                client.OnDtlsPacketReceived(datagram.ToArray(), serverEndpoint);
                return ValueTask.CompletedTask;
            },
            onContextsReady: (_, _, _, _) => serverReady.TrySetResult(),
            onHandshakeFailed: () => serverReady.TrySetException(new InvalidOperationException("server handshake failed")),
            NullLoggerFactory.Instance);

        await using (client)
        await using (server)
        {
            client.Start(default);
            server.Start(default);
            await Task.WhenAll(clientReady.Task, serverReady.Task).WaitAsync(TimeSpan.FromSeconds(30));

            // The peer (server) closes its DTLS association; its close_notify reaches the client, whose
            // association receiver notices it and notifies the owner — media must not keep flowing.
            await server.DisposeAsync();

            await clientPeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<LoopbackPair> RunLoopbackHandshakeAsync()
    {
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();

        QueueDatagramTransport clientTransport = null!;
        QueueDatagramTransport serverTransport = null!;
        clientTransport = new QueueDatagramTransport(datagram => serverTransport.Enqueue(datagram));
        serverTransport = new QueueDatagramTransport(datagram => clientTransport.Enqueue(datagram));

        var handshaker = new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var clientTask = handshaker.HandshakeAsync(
            DtlsRole.Client, clientTransport, clientCertificate, serverCertificate.Fingerprint, timeout.Token);
        var serverTask = handshaker.HandshakeAsync(
            DtlsRole.Server, serverTransport, serverCertificate, clientCertificate.Fingerprint, timeout.Token,
            LoopbackServerCookieClientId);

        await Task.WhenAll(clientTask, serverTask);

        return new LoopbackPair(clientTask.Result, clientTransport, serverTask.Result, serverTransport);
    }

    private sealed record LoopbackPair(
        DtlsSrtpHandshakeResult ClientResult,
        QueueDatagramTransport ClientTransport,
        DtlsSrtpHandshakeResult ServerResult,
        QueueDatagramTransport ServerTransport);
}
