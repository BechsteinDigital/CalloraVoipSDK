using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #185 (test-evidence remainder of #156 P1-3): slot exhaustion → recovery. The sibling
/// <see cref="StunServerStreamDeadlineTests"/> proves the two halves separately — a stalling connection
/// is reaped, and a prompt request succeeds — but not the property the finding is actually about:
/// with every connection slot held by silent or dribbling peers, a legitimate client must still be
/// served once the read deadline reclaims those slots. These tests pin the combined scenario, and pin
/// that the recovery is <em>caused</em> by the deadline rather than by the cap being generous.
/// </summary>
public sealed class StunServerSlowlorisRecoveryTests
{
    // Small enough that a handful of sockets saturates the server, large enough that the accept loop
    // has to genuinely queue rather than trivially reject.
    private const int SlotCap = 2;
    private static readonly TimeSpan ReapDeadline = TimeSpan.FromMilliseconds(400);

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

    // Opens `count` connections that never complete a STUN message: half stay completely silent, half
    // dribble a partial 20-byte header (RFC 5389 §7.2.2 framing) and then stall. Both shapes hold a
    // connection slot until the read deadline reclaims it.
    private static async Task<List<TcpClient>> SaturateAsync(StunServer server, int count)
    {
        var attackers = new List<TcpClient>(count);
        for (var i = 0; i < count; i++)
        {
            var attacker = new TcpClient();
            await attacker.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
            if (i % 2 == 1)
            {
                var stream = attacker.GetStream();
                await stream.WriteAsync(new byte[10]);   // half a STUN header, then nothing
                await stream.FlushAsync();
            }
            attackers.Add(attacker);
        }

        // Let the accept loop take the slots before the legitimate client arrives; otherwise the test
        // would pass without the server ever having been saturated.
        await Task.Delay(150);
        return attackers;
    }

    private static void DisposeAll(List<TcpClient> clients)
    {
        foreach (var client in clients)
            client.Dispose();
    }

    [Fact]
    public async Task A_legitimate_client_is_served_after_slowloris_peers_saturate_every_slot()
    {
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(codec, new StunServerOptions
        {
            MaxConcurrentStreamConnections = SlotCap,
            ConnectionCapPolicy = StunConnectionCapPolicy.Backpressure,
            StreamReadTimeout = ReapDeadline,
        });

        var attackers = await SaturateAsync(server, SlotCap);
        try
        {
            // Every slot is held by a peer that will never send a request. Under backpressure the accept
            // loop is parked waiting for a slot, so this connect sits in the listen backlog until the
            // deadline reaps an attacker — the whole point of the deadline existing.
            var client = new StunClient(codec, NullLogger<StunClient>.Instance);
            var result = await client
                .QueryBindingAsync(server.LocalEndPoint, transport: StunTransport.Tcp)
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.NotNull(result.MappedEndPoint);
        }
        finally
        {
            DisposeAll(attackers);
        }
    }

    [Fact]
    public async Task Without_the_read_deadline_the_saturated_server_never_recovers()
    {
        // The counterpart: same saturation, deadline disabled. If this ever starts passing quickly, the
        // recovery above is coming from somewhere other than the reaper and the test above is vacuous.
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(codec, new StunServerOptions
        {
            MaxConcurrentStreamConnections = SlotCap,
            ConnectionCapPolicy = StunConnectionCapPolicy.Backpressure,
            StreamReadTimeout = Timeout.InfiniteTimeSpan,
        });

        var attackers = await SaturateAsync(server, SlotCap);
        try
        {
            var client = new StunClient(codec, NullLogger<StunClient>.Instance);
            var query = client.QueryBindingAsync(server.LocalEndPoint, transport: StunTransport.Tcp);

            // Well past the deadline the other test recovers within: the slots are held forever.
            var finished = await Task.WhenAny(query, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.NotSame(query, finished);

            // The query is still parked in the backlog; it faults when the server is torn down at the end
            // of the test. Observe that fault so it never surfaces as an unobserved task exception.
            _ = query.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        finally
        {
            DisposeAll(attackers);
        }
    }

    [Fact]
    public async Task Reclaimed_slots_serve_more_than_one_client_in_a_row()
    {
        // Recovery must return the server to service, not grant one lucky request: the reaper releases a
        // slot per reaped connection, and a released slot has to be reusable by the next client.
        var codec = new StunMessageCodec();
        await using var server = NewTcpServer(codec, new StunServerOptions
        {
            MaxConcurrentStreamConnections = SlotCap,
            ConnectionCapPolicy = StunConnectionCapPolicy.Backpressure,
            StreamReadTimeout = ReapDeadline,
        });

        var attackers = await SaturateAsync(server, SlotCap);
        try
        {
            var client = new StunClient(codec, NullLogger<StunClient>.Instance);
            for (var i = 0; i < 3; i++)
            {
                var result = await client
                    .QueryBindingAsync(server.LocalEndPoint, transport: StunTransport.Tcp)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.NotNull(result.MappedEndPoint);
            }
        }
        finally
        {
            DisposeAll(attackers);
        }
    }
}
