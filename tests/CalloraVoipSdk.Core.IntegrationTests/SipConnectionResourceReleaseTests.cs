using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #158 P2-9 — when the peer closed a connection, the receive loop's <c>finally</c> only invoked the
/// owner's <c>onClosed</c> callback, which removes the entry from its dictionary. Socket, stream, send gate
/// and cancellation source were left to the finaliser — and because the entry was already gone, shutdown
/// could no longer reach the instance to dispose it either. Under connection churn that is an unbounded
/// file-descriptor leak.
///
/// <para>
/// The release cannot simply call <c>Dispose()</c>: that joins the receive loop, so calling it from inside
/// the loop would wait on itself until the join times out. Hence a separate idempotent release, called from
/// whichever of the two paths gets there first.
/// </para>
/// </summary>
public sealed class SipConnectionResourceReleaseTests
{
    private static async Task<(SipStreamConnection Connection, TcpClient Server, TcpListener Listener)> ConnectedPairAsync(
        Action onClosed)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var clientSide = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await clientSide.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverSide = await acceptTask;

        var connection = new SipStreamConnection(
            SipTransportProtocol.Tcp,
            clientSide,
            clientSide.GetStream(),
            NullLogger.Instance,
            (_, _, _) => Task.CompletedTask,
            onClosed);

        return (connection, serverSide, listener);
    }

    [Fact]
    public async Task A_remote_close_releases_the_connection_resources()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (connection, server, listener) = await ConnectedPairAsync(() => closed.TrySetResult());

        try
        {
            // The peer goes away, which is the ordinary end of a connection — not a shutdown from our side.
            server.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The send gate is disposed along with the rest, so the send path now fails loudly instead of
            // queueing onto a dead stream. Before the fix these resources stayed alive with no owner.
            await AssertReleasedAsync(connection);
        }
        finally
        {
            connection.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disposing_after_a_remote_close_is_a_no_op_rather_than_a_double_dispose()
    {
        // Both paths free the same objects, so the second one must find them already gone. Shutdown
        // routinely disposes connections the peer has just closed.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (connection, server, listener) = await ConnectedPairAsync(() => closed.TrySetResult());

        try
        {
            server.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            connection.Dispose();
            connection.Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disposing_a_live_connection_still_works()
    {
        // The other direction: we close first, the loop has not run its finally yet, and Dispose must both
        // join the loop and free everything.
        var (connection, server, listener) = await ConnectedPairAsync(() => { });

        try
        {
            connection.Dispose();
            await AssertReleasedAsync(connection);
        }
        finally
        {
            server.Close();
            listener.Stop();
        }
    }

    /// <summary>
    /// Sending must fail once the resources are gone — the observable consequence of the release, since the
    /// disposal flags themselves are private.
    /// </summary>
    private static async Task AssertReleasedAsync(SipStreamConnection connection)
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => connection.SendAsync("OPTIONS sip:x SIP/2.0\r\n\r\n"u8.ToArray(), CancellationToken.None));
    }
}
