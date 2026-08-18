using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The stream gathering probe (ADR-073 slice 2, #240): a TURN relay allocation gathered over a connected TCP
/// stream to a real hosted <see cref="TurnServerHost"/>, yielding the relayed endpoint a stream relay candidate
/// advertises. A silent server yields no candidate within the gathering timeout rather than hanging, and the
/// stream is left open for the caller to hand on (success) or dispose (failure).
/// </summary>
public sealed class TurnStreamAllocationProbeTests
{
    [Fact]
    public async Task A_relay_allocation_is_gathered_over_a_tcp_stream()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = IceTransport.Tcp,
            RequireAuthentication = false,
        });
        host.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.LocalEndPoint);
        var stream = tcp.GetStream();

        var probe = new TurnStreamAllocationProbe(new StunMessageCodec(), NullLoggerFactory.Instance);
        var allocation = await probe.TryAllocateAsync(
            stream, host.LocalEndPoint, credentials: null, lifetimeSeconds: 600, CancellationToken.None);

        Assert.NotNull(allocation);
        Assert.NotNull(allocation!.RelayedEndPoint);
        Assert.NotEqual(0, allocation.RelayedEndPoint.Port);
        Assert.True(allocation.LifetimeSeconds > 0, "the allocation must grant a positive lifetime");

        // The stream is left open for hand-off — the probe does not dispose it and cancelling its receive
        // loop does not tear it down. CanWrite is false only on a disposed stream, so this is the exact
        // "still usable, not disposed" check (TcpClient.Connected only reflects the last I/O and gives a false
        // negative after a cancelled read; verified separately that a real write still succeeds here).
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task A_silent_server_yields_no_candidate_within_the_gathering_timeout()
    {
        // A listener that accepts the connection but never answers — the probe must give up and return null
        // within its own timeout, not hang through the transactor's full RTO schedule.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverEndPoint = (IPEndPoint)listener.LocalEndpoint;
        var accept = listener.AcceptTcpClientAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(serverEndPoint);
        using var accepted = await accept;   // hold the connection open, answer nothing

        var probe = new TurnStreamAllocationProbe(
            new StunMessageCodec(), NullLoggerFactory.Instance, gatheringTimeout: TimeSpan.FromMilliseconds(400));

        var allocation = await probe
            .TryAllocateAsync(tcp.GetStream(), serverEndPoint, credentials: null, lifetimeSeconds: 600, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(allocation);
    }
}
