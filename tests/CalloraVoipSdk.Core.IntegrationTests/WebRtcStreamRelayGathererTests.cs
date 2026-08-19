using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The gathering-time stream relay producer (ADR-073 slice 4c-i, #240): <see cref="WebRtcStreamRelayGatherer"/>
/// ties the earlier slices together — it allocates over a connected TCP stream to a real hosted
/// <see cref="TurnServerHost"/> and produces a <see cref="StreamRelayCandidate"/> whose relay binding actually
/// drives a permission and a Send indication over that same stream. A silent (or failed) allocation yields no
/// candidate and the gatherer disposes the stream it was handed.
/// </summary>
/// <remarks>
/// This proves the producer wires transport ↔ binding correctly end to end (gather → transport → binding →
/// control plumbing): after <see cref="StreamRelayCandidate.Activate"/> the encapsulated relay send path
/// completes a real permission round-trip over the stream against the server, with no manual assembly. Handing
/// the candidate into a live ICE agent's consent/nomination is the next slice (4c-ii) and is not exercised here.
/// </remarks>
public sealed class WebRtcStreamRelayGathererTests
{
    [Fact]
    public async Task Gathers_a_candidate_whose_relay_binding_works_over_the_stream()
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

        var gatherer = new WebRtcStreamRelayGatherer(new StunMessageCodec(), NullLoggerFactory.Instance);
        await using var candidate = await gatherer.GatherAsync(
            tcp.GetStream(), host.LocalEndPoint, credentials: null, lifetimeSeconds: 600,
            onInboundMedia: _ => { }, CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(host.LocalEndPoint, candidate!.ServerEndPoint);
        Assert.NotEqual(0, candidate.RelayedEndPoint.Port);
        Assert.NotNull(candidate.Binding.EnsurePermission);

        // Go live: wire the (unused-here) inbound route and start the receive loop, then drive the encapsulated
        // relay send path. It installs a TURN permission (RFC 8656 §9) and frames the check as a Send indication
        // (§10) — the control response rides back through the producer's control plumbing into the transactor.
        candidate.Activate(onInboundIndication: (_, _) => { });

        var peer = new IPEndPoint(IPAddress.Parse("198.51.100.30"), 50000);
        var check = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        // Completes without throwing → the whole gathered chain functions over the stream against the real server.
        await candidate.Binding.RelaySend(check, peer, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_silent_server_yields_no_candidate_and_disposes_the_stream()
    {
        // A listener that accepts but never answers — the gather must give up within the timeout and return null.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverEndPoint = (IPEndPoint)listener.LocalEndpoint;
        var accept = listener.AcceptTcpClientAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(serverEndPoint);
        using var accepted = await accept;   // hold the connection open, answer nothing
        var stream = tcp.GetStream();

        var gatherer = new WebRtcStreamRelayGatherer(
            new StunMessageCodec(), NullLoggerFactory.Instance, gatheringTimeout: TimeSpan.FromMilliseconds(400));

        var candidate = await gatherer
            .GatherAsync(stream, serverEndPoint, credentials: null, lifetimeSeconds: 600, onInboundMedia: _ => { }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(candidate);
        // The gatherer owns the stream on failure and disposes it — CanWrite is false only on a disposed stream.
        Assert.False(stream.CanWrite);
    }
}
