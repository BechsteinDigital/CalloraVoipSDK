using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// TURN stream slowloris hardening (companion to #156 STUN P1-3): the TURN TCP/TLS stream path read its
/// frames only against the server stop token, so a peer that opened a connection and never delivered — or
/// dribbled — a control message held a connection slot indefinitely (K4). These tests pin the per-frame
/// read deadline that drops such connections. TURN connections are long-lived, so the deadline resets per
/// frame (a client refreshing on schedule never trips it) and disabling it keeps a connection open.
/// </summary>
public sealed class TurnServerStreamDeadlineTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(300);

    private static TurnServer NewTcpServer(StunMessageCodec codec, TurnServerOptions options)
    {
        var server = new TurnServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            TurnServerTransport.Tcp,
            codec,
            NullLogger<TurnServer>.Instance,
            authOptions: null,
            tlsServerCertificate: null,
            options: options);
        server.Start();
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
        await using var server = NewTcpServer(
            codec, new TurnServerOptions { RequireAuthentication = false, StreamReadTimeout = ShortDeadline });

        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
        await using var stream = client.GetStream();

        // Never send a request — a slowloris holding the slot open. The server must close it.
        Assert.Equal(0, await ReadTolerantAsync(stream));
    }

    [Fact]
    public async Task A_partial_frame_connection_is_dropped_after_the_read_deadline()
    {
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(
            codec, new TurnServerOptions { RequireAuthentication = false, StreamReadTimeout = ShortDeadline });

        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
        await using var stream = client.GetStream();

        // Start a STUN-shaped frame (first byte 0x00) then stall — the framer blocks on the rest.
        await stream.WriteAsync(new byte[] { 0x00, 0x01, 0x00, 0x08 });
        await stream.FlushAsync();

        Assert.Equal(0, await ReadTolerantAsync(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Construction_rejects_a_stream_timeout_beyond_the_timer_limit(bool handshake)
    {
        var codec = new StunMessageCodec();
        var overflow = TimeSpan.FromDays(60); // beyond CancelAfter's ~49.7-day limit
        var options = handshake
            ? new TurnServerOptions { RequireAuthentication = false, StreamHandshakeTimeout = overflow }
            : new TurnServerOptions { RequireAuthentication = false, StreamReadTimeout = overflow };

        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            TurnServerTransport.Tcp,
            codec,
            NullLogger<TurnServer>.Instance,
            authOptions: null,
            tlsServerCertificate: null,
            options: options));
    }

    [Fact]
    public async Task An_infinite_read_timeout_keeps_an_idle_connection_open()
    {
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(
            codec, new TurnServerOptions { RequireAuthentication = false, StreamReadTimeout = Timeout.InfiniteTimeSpan });

        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
        await using var stream = client.GetStream();

        var readTask = stream.ReadAsync(new byte[1]).AsTask();
        await Task.Delay(500);

        // With the deadline disabled the server does not drop an idle connection.
        Assert.False(readTask.IsCompleted);
    }

    [Fact]
    public async Task Construction_warns_when_read_timeout_is_shorter_than_the_allocation_lifetime()
    {
        var capturing = new CapturingLogger();

        await using var server = new TurnServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            TurnServerTransport.Tcp,
            new StunMessageCodec(),
            new TypedLogger<TurnServer>(capturing),
            authOptions: null,
            tlsServerCertificate: null,
            options: new TurnServerOptions
            {
                RequireAuthentication = false,
                DefaultAllocationLifetimeSeconds = 600,
                StreamReadTimeout = TimeSpan.FromSeconds(60), // shorter than the 600s allocation lifetime
            });

        Assert.Contains(
            capturing.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("StreamReadTimeout", StringComparison.OrdinalIgnoreCase));
    }

    // Adapts the shared non-generic CapturingLogger to the ILogger<T> the server requires.
    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
