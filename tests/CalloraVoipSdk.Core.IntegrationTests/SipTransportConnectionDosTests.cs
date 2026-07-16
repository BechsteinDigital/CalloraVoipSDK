using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behavioural gate for the SIP stream/WebSocket receive loops against resource-exhaustion
/// abuse (HARD-A1). Locks in two properties an attacker must not be able to violate: a stalled
/// peer is reaped after the read-idle window instead of pinning a connection forever, and an
/// oversized WebSocket message is refused before the aggregation buffer can grow without bound.
/// </summary>
public sealed class SipTransportConnectionDosTests
{
    private static readonly TimeSpan ShortIdle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

    private static async Task<(TcpClient Server, TcpClient Client)> ConnectedPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            var server = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await connect.ConfigureAwait(false);
            return (server, client);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Stream_connection_closes_after_read_idle_timeout()
    {
        var (server, client) = await ConnectedPairAsync();
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var connection = new SipStreamConnection(
            SipTransportProtocol.Tcp,
            server,
            server.GetStream(),
            NullLogger.Instance,
            (_, _, _) => Task.CompletedTask,
            () => closed.TrySetResult(true),
            ShortIdle);

        try
        {
            // Client stays silent: the server side must reap the connection once the idle window elapses.
            var done = await Task.WhenAny(closed.Task, Task.Delay(CloseWait)).ConfigureAwait(false);
            Assert.True(done == closed.Task, "idle stream connection was not closed within the timeout window");
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Stream_connection_still_delivers_a_complete_message()
    {
        var (server, client) = await ConnectedPairAsync();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var connection = new SipStreamConnection(
            SipTransportProtocol.Tcp,
            server,
            server.GetStream(),
            NullLogger.Instance,
            (_, _, frame) =>
            {
                received.TrySetResult(frame.ToArray());
                return Task.CompletedTask;
            },
            () => { },
            TimeSpan.FromSeconds(30));

        try
        {
            var message = Encoding.UTF8.GetBytes(
                "MESSAGE sip:bob@example.org SIP/2.0\r\n" +
                "Call-ID: c1\r\n" +
                "Content-Length: 5\r\n" +
                "\r\n" +
                "hello");
            await client.GetStream().WriteAsync(message).ConfigureAwait(false);
            await client.GetStream().FlushAsync().ConfigureAwait(false);

            var done = await Task.WhenAny(received.Task, Task.Delay(CloseWait)).ConfigureAwait(false);
            Assert.True(done == received.Task, "complete SIP message was not framed and delivered");
            Assert.Equal(message.Length, (await received.Task).Length);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task WebSocket_connection_closes_after_read_idle_timeout()
    {
        var (server, client) = await ConnectedPairAsync();
        using var serverWs = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        using var clientWs = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var connection = new SipWebSocketConnection(
            SipTransportProtocol.Ws,
            serverWs,
            new IPEndPoint(IPAddress.Loopback, 5060),
            NullLogger.Instance,
            (_, _, _) => Task.CompletedTask,
            () => closed.TrySetResult(true),
            ShortIdle);

        try
        {
            var done = await Task.WhenAny(closed.Task, Task.Delay(CloseWait)).ConfigureAwait(false);
            Assert.True(done == closed.Task, "idle WebSocket connection was not closed within the timeout window");
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task WebSocket_connection_refuses_oversized_message()
    {
        var (server, client) = await ConnectedPairAsync();
        using var serverWs = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        using var clientWs = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var framed = false;

        using var connection = new SipWebSocketConnection(
            SipTransportProtocol.Ws,
            serverWs,
            new IPEndPoint(IPAddress.Loopback, 5060),
            NullLogger.Instance,
            (_, _, _) =>
            {
                framed = true;
                return Task.CompletedTask;
            },
            () => closed.TrySetResult(true),
            TimeSpan.FromSeconds(30));

        try
        {
            // One WS message larger than the header+body ceiling: the loop must refuse it and close,
            // never letting the aggregation buffer grow to the full attacker-chosen size.
            var oversized = new byte[(384 * 1024) + 1];
            await clientWs.SendAsync(oversized, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);

            var done = await Task.WhenAny(closed.Task, Task.Delay(CloseWait)).ConfigureAwait(false);
            Assert.True(done == closed.Task, "oversized WebSocket message did not trigger a connection close");
            Assert.False(framed, "oversized WebSocket message must never be dispatched as a SIP frame");
        }
        finally
        {
            client.Dispose();
        }
    }
}
