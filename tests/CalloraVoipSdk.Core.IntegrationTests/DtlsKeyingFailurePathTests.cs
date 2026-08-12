using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #157 P2-6: keying does not end when the handshake returns. The SRTP/SRTCP contexts are still to be
/// constructed and handed to the owner, and a failure there used to escape both typed catches in
/// <c>DtlsMediaAttachment.RunHandshakeAsync</c> — the handshake task faulted unobserved, the master keys
/// were never wiped, and the owner was never told that keying failed, so it sat waiting for media that
/// could never be authenticated. Two attachments in loopback, so a real handshake runs.
/// </summary>
public sealed class DtlsKeyingFailurePathTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_throwing_context_callback_fails_the_leg_closed_instead_of_faulting_silently()
    {
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var clientEndpoint = new IPEndPoint(IPAddress.Loopback, 5100);
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 5101);

        var clientFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            // The owner throws while adopting the freshly derived contexts — a media session that fails
            // to install them is the realistic shape of this (and the handshake itself has succeeded).
            onContextsReady: (_, _, _, _) => throw new InvalidOperationException("owner refused the contexts"),
            onHandshakeFailed: () => clientFailed.TrySetResult(),
            NullLoggerFactory.Instance);
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

            // The owner is told keying failed, so it keeps media blocked (fail closed) rather than
            // waiting forever on contexts that will never arrive.
            await clientFailed.Task.WaitAsync(Patience);
            await serverReady.Task.WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task A_repeated_start_does_not_run_a_second_handshake()
    {
        // #157 P2-7: Start used to overwrite _handshakeTask with a fresh handshake on every call. Two
        // handshakes consume the same datagram queue and race to install SRTP contexts through the owner
        // callback, while the first task is orphaned — nobody awaits it, so its failure goes unobserved.
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var clientEndpoint = new IPEndPoint(IPAddress.Loopback, 5104);
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 5105);

        var clientKeyings = 0;
        var clientReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            onContextsReady: (_, _, _, _) =>
            {
                Interlocked.Increment(ref clientKeyings);
                clientReady.TrySetResult();
            },
            onHandshakeFailed: () => clientReady.TrySetException(new InvalidOperationException("client handshake failed")),
            NullLoggerFactory.Instance);
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
            client.Start(default);   // ignored — the second handshake must never begin
            server.Start(default);

            await Task.WhenAll(clientReady.Task, serverReady.Task).WaitAsync(Patience);

            // Let a second handshake, had one started, get far enough to key again before asserting.
            await Task.Delay(500);
            Assert.Equal(1, Volatile.Read(ref clientKeyings));
        }
    }

    [Fact]
    public async Task A_throwing_rtx_callback_still_wipes_the_exported_master_keys()
    {
        // The RTX contexts (RFC 4588 §9) are derived after the primary ones, so a throw there lands
        // between "keys used" and "keys wiped" — the exact window the wipe has to survive.
        var clientCertificate = DtlsCertificate.GenerateEcdsaP256();
        var serverCertificate = DtlsCertificate.GenerateEcdsaP256();
        var clientEndpoint = new IPEndPoint(IPAddress.Loopback, 5102);
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 5103);

        var clientFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            onContextsReady: (_, _, _, _) => { },
            onHandshakeFailed: () => clientFailed.TrySetResult(),
            NullLoggerFactory.Instance,
            onSecondaryContextsReady: (_, _) => throw new InvalidOperationException("owner refused the RTX contexts"));
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

            await clientFailed.Task.WaitAsync(Patience);
            await serverReady.Task.WaitAsync(Patience);
        }
    }
}
