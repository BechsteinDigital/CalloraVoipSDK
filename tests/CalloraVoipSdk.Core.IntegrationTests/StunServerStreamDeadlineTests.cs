using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #156 STUN P1-3 (TCP/TLS slowloris). The stream transport read its messages only against the server
/// stop token, so a peer that opened a connection and dribbled — or never sent — a STUN message held a
/// connection slot indefinitely; the slot cap alone does not stop that (K4). These tests pin the
/// per-message read deadline that drops such connections while a prompt request still succeeds.
/// </summary>
public sealed class StunServerStreamDeadlineTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(300);

    private static StunServer NewTcpServer(StunMessageCodec codec, StunServerOptions options)
    {
        var server = new StunServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            StunServerTransport.Tcp,
            codec,
            responseIntegrityKey: null,
            tlsServerCertificate: null,
            NullLogger<StunServer>.Instance,
            options);
        server.Start(new StunBindingRequestHandler(codec, NullLogger<StunBindingRequestHandler>.Instance));
        return server;
    }

    private static async Task<int> ReadTolerantAsync(NetworkStream stream)
    {
        try
        {
            return await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (IOException)
        {
            return 0; // A reset is also the server dropping the connection.
        }
    }

    [Fact]
    public async Task An_idle_stream_connection_is_dropped_after_the_read_deadline()
    {
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(codec, new StunServerOptions { StreamReadTimeout = ShortDeadline });

        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
        await using var stream = client.GetStream();

        // Never send a request — a slowloris holding the slot open. The server must close it.
        Assert.Equal(0, await ReadTolerantAsync(stream));
    }

    [Fact]
    public async Task A_partial_header_connection_is_dropped_after_the_read_deadline()
    {
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(codec, new StunServerOptions { StreamReadTimeout = ShortDeadline });

        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
        await using var stream = client.GetStream();

        // Send half of the 20-byte STUN header, then stall — the framer blocks on the rest.
        await stream.WriteAsync(new byte[10]);
        await stream.FlushAsync();

        Assert.Equal(0, await ReadTolerantAsync(stream));
    }

    [Fact]
    public async Task A_prompt_binding_request_over_tcp_still_succeeds()
    {
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(codec, new StunServerOptions { StreamReadTimeout = TimeSpan.FromSeconds(5) });

        var client = new StunClient(codec, NullLogger<StunClient>.Instance);
        var result = await client
            .QueryBindingAsync(server.LocalEndPoint, transport: StunTransport.Tcp)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result.MappedEndPoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Construction_rejects_a_stream_timeout_beyond_the_timer_limit(bool handshake)
    {
        var codec = new StunMessageCodec();
        var overflow = TimeSpan.FromDays(60); // beyond CancelAfter's ~49.7-day limit
        var options = handshake
            ? new StunServerOptions { StreamHandshakeTimeout = overflow }
            : new StunServerOptions { StreamReadTimeout = overflow };

        Assert.Throws<ArgumentOutOfRangeException>(() => new StunServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            StunServerTransport.Tcp,
            codec,
            responseIntegrityKey: null,
            tlsServerCertificate: null,
            NullLogger<StunServer>.Instance,
            options));
    }

    [Fact]
    public async Task An_infinite_read_timeout_keeps_an_idle_connection_open()
    {
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(
            codec, new StunServerOptions { StreamReadTimeout = Timeout.InfiniteTimeSpan });

        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
        await using var stream = client.GetStream();

        var readTask = stream.ReadAsync(new byte[1]).AsTask();
        await Task.Delay(500);

        // With the deadline disabled the server does not drop an idle connection.
        Assert.False(readTask.IsCompleted);
    }
}
