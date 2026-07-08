using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Deterministic verification of the application-layer ICE agent (CORE-006 true-up):
/// candidate gathering (host/srflx/relay), connectivity checks with retry, pair
/// selection and the failure paths — all through the injectable STUN/TURN ports.
/// The original ICE test suite did not survive the public repository cut, so the
/// tracked "test-covered" claim is re-established here.
/// </summary>
public sealed class CallIceAgentTests
{
    private static readonly IPEndPoint LocalRtp = new(IPAddress.Parse("10.0.0.10"), 40000);
    private static readonly IPEndPoint Srflx = new(IPAddress.Parse("203.0.113.10"), 50000);
    private static readonly IPEndPoint Relay = new(IPAddress.Parse("198.51.100.5"), 60000);

    private static IceConfiguration Config(bool withTurn = false, int retries = 1) => new()
    {
        Enabled = true,
        ConnectivityCheckRetries = retries,
        ConnectivityCheckTimeout = TimeSpan.FromMilliseconds(50),
        Servers = withTurn
            ?
            [
                new IceServerConfiguration { Host = "stun.example.com", Type = IceServerType.Stun },
                new IceServerConfiguration { Host = "turn.example.com", Type = IceServerType.Turn, Username = "u", Password = "p" },
            ]
            : [new IceServerConfiguration { Host = "stun.example.com", Type = IceServerType.Stun }],
    };

    private static CallIceAgent Agent(
        IceConfiguration config,
        FakeStunProbe probe,
        FakeTurnAllocator? turn = null)
        => new(config, probe, NullLoggerFactory.Instance, turn);

    private static CallIceCandidate Candidate(string type, IPEndPoint endpoint, long priority = 100) => new()
    {
        Foundation = type,
        Component = 1,
        Transport = "UDP",
        Priority = priority,
        Address = endpoint.Address.ToString(),
        Port = endpoint.Port,
        Type = type,
    };

    private static CallMediaParameters Parameters(
        IReadOnlyList<CallIceCandidate> local,
        IReadOnlyList<CallIceCandidate> remote) => new()
    {
        LocalEndPoint = LocalRtp,
        RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.20"), 42000),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        PayloadTypeCodecMap = new Dictionary<int, string> { [0] = "PCMU" },
        IceEnabled = true,
        LocalIceUfrag = "localU",
        LocalIcePwd = "localP",
        RemoteIceUfrag = "remoteU",
        RemoteIcePwd = "remotePassword123",
        LocalIceCandidates = local,
        RemoteIceCandidates = remote,
    };

    // --- Gathering ---

    [Fact]
    public async Task Gathering_produces_host_and_srflx_candidates_with_stun_only()
    {
        var probe = new FakeStunProbe { ReflexiveEndPoint = Srflx };
        var agent = Agent(Config(), probe);

        var description = await agent.BuildLocalDescriptionAsync(LocalRtp);

        Assert.NotNull(description);
        Assert.False(string.IsNullOrWhiteSpace(description!.Ufrag));
        Assert.False(string.IsNullOrWhiteSpace(description.Pwd));
        Assert.Equal(["host", "srflx"], description.Candidates.Select(c => c.Type).ToArray());
        var srflx = description.Candidates.Single(c => c.Type == "srflx");
        Assert.Equal(Srflx.Address.ToString(), srflx.Address);
        Assert.Equal(Srflx.Port, srflx.Port);
    }

    [Fact]
    public async Task Gathering_includes_relay_candidate_when_turn_allocation_succeeds()
    {
        var probe = new FakeStunProbe { ReflexiveEndPoint = Srflx };
        var turn = new FakeTurnAllocator { Allocation = new IceRelayAllocation { RelayedEndPoint = Relay } };
        var agent = Agent(Config(withTurn: true), probe, turn);

        var description = await agent.BuildLocalDescriptionAsync(LocalRtp);

        Assert.NotNull(description);
        Assert.Contains(description!.Candidates, c => c.Type == "relay" && c.Port == Relay.Port);
    }

    [Fact]
    public async Task Gathering_falls_back_to_host_only_when_stun_fails()
    {
        var probe = new FakeStunProbe { ReflexiveEndPoint = null };
        var agent = Agent(Config(), probe);

        var description = await agent.BuildLocalDescriptionAsync(LocalRtp);

        Assert.NotNull(description);
        var candidate = Assert.Single(description!.Candidates);
        Assert.Equal("host", candidate.Type);
    }

    [Fact]
    public async Task Gathering_skipped_when_ice_disabled()
    {
        var agent = Agent(new IceConfiguration { Enabled = false }, new FakeStunProbe());

        Assert.Null(await agent.BuildLocalDescriptionAsync(LocalRtp));
    }

    // --- Selection: success paths ---

    [Fact]
    public async Task Host_to_host_pair_is_selected_when_check_succeeds()
    {
        var probe = new FakeStunProbe { ConnectivityResults = { [_ => true] = true } };
        var agent = Agent(Config(), probe);
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([Candidate("host", LocalRtp)], [Candidate("host", remote)]));

        Assert.True(result.HasSelectedPair);
        Assert.Equal(CallIceNegotiationState.Connected, result.State);
        Assert.Equal("ice_checks_succeeded", result.ReasonCode);
        Assert.Equal(remote, result.RemoteEndPoint);
    }

    [Fact]
    public async Task Relay_pair_wins_when_direct_paths_fail()
    {
        // Only checks towards the TURN-relayed remote candidate succeed.
        var relayRemote = new IPEndPoint(IPAddress.Parse("198.51.100.99"), 61000);
        var probe = new FakeStunProbe
        {
            ConnectivityResults = { [remote => remote.Equals(relayRemote)] = true },
        };
        var agent = Agent(Config(withTurn: true), probe);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters(
                [Candidate("host", LocalRtp, priority: 200), Candidate("relay", Relay, priority: 50)],
                [
                    Candidate("host", new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000), priority: 200),
                    Candidate("relay", relayRemote, priority: 50),
                ]));

        Assert.True(result.HasSelectedPair);
        Assert.Equal(relayRemote, result.RemoteEndPoint);
        Assert.Equal("relay", result.RemoteCandidate!.Type);
    }

    [Fact]
    public async Task Failing_check_is_retried_before_pair_is_abandoned()
    {
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000);
        var probe = new FakeStunProbe { SucceedOnAttempt = 2 };
        var agent = Agent(Config(retries: 1), probe);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([Candidate("host", LocalRtp)], [Candidate("host", remote)]));

        Assert.True(result.HasSelectedPair);
        Assert.Equal(2, probe.ConnectivityAttempts);
    }

    // --- Selection: failure paths ---

    [Fact]
    public async Task All_pairs_failing_yields_failed_result_and_keeps_sdp_endpoints()
    {
        var probe = new FakeStunProbe(); // every check fails
        var agent = Agent(Config(retries: 0), probe);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters(
                [Candidate("host", LocalRtp)],
                [Candidate("host", new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000))]));

        Assert.False(result.HasSelectedPair);
        Assert.Equal(CallIceNegotiationState.Failed, result.State);
        Assert.Equal("ice_checks_failed", result.ReasonCode);
    }

    [Fact]
    public async Task Missing_remote_metadata_reports_disabled_not_failure()
    {
        var agent = Agent(Config(), new FakeStunProbe());

        var noMetadata = new CallMediaParameters
        {
            LocalEndPoint = LocalRtp,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.20"), 42000),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            PayloadTypeCodecMap = new Dictionary<int, string> { [0] = "PCMU" },
            IceEnabled = false,
        };

        var result = await agent.SelectCandidatePairAsync(CallId.New(), noMetadata);

        Assert.Equal("ice_metadata_missing", result.ReasonCode);
        Assert.Equal(CallIceNegotiationState.Disabled, result.State);
    }

    [Fact]
    public async Task Missing_candidates_fail_with_dedicated_reason()
    {
        var agent = Agent(Config(), new FakeStunProbe());

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([Candidate("host", LocalRtp)], remote: []));

        Assert.Equal("ice_candidates_missing", result.ReasonCode);
        Assert.Equal(CallIceNegotiationState.Failed, result.State);
    }

    // --- Fakes ---

    private sealed class FakeStunProbe : IIceStunProbe
    {
        public IPEndPoint? ReflexiveEndPoint { get; init; }
        public Dictionary<Func<IPEndPoint, bool>, bool> ConnectivityResults { get; } = [];
        public int SucceedOnAttempt { get; init; }
        public int ConnectivityAttempts;

        public Task<IPEndPoint?> TryGetServerReflexiveEndPointAsync(
            IPEndPoint localEndPoint, IceServerConfiguration server, CancellationToken ct = default)
            => Task.FromResult(ReflexiveEndPoint);

        public Task<bool> TryCheckConnectivityAsync(
            IPEndPoint localEndPoint, IPEndPoint remoteEndPoint,
            string localIceUfrag, string remoteIceUfrag, string remoteIcePassword,
            TimeSpan timeout, CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref ConnectivityAttempts);
            if (SucceedOnAttempt > 0)
                return Task.FromResult(attempt >= SucceedOnAttempt);

            foreach (var (predicate, outcome) in ConnectivityResults)
            {
                if (predicate(remoteEndPoint))
                    return Task.FromResult(outcome);
            }

            return Task.FromResult(false);
        }
    }

    private sealed class FakeTurnAllocator : IIceTurnRelayAllocator
    {
        public IceRelayAllocation? Allocation { get; init; }

        public Task<IceRelayAllocation?> TryAllocateRelayAsync(
            IPEndPoint localEndPoint, IceServerConfiguration server, CancellationToken ct = default)
            => Task.FromResult(Allocation);
    }
}
