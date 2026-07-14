using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Ice;
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
        IReadOnlyList<CallIceCandidate> remote,
        bool controlling = true) => new()
    {
        LocalEndPoint = LocalRtp,
        RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.20"), 42000),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        PayloadTypeCodecMap = new Dictionary<int, string> { [0] = "PCMU" },
        IceEnabled = true,
        IceControlling = controlling,
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

    [Fact]
    public async Task Gathering_adds_a_video_host_candidate_for_the_video_endpoint()
    {
        var probe = new FakeStunProbe { ReflexiveEndPoint = Srflx };
        var agent = Agent(Config(), probe);
        var videoEndPoint = new IPEndPoint(LocalRtp.Address, 40002);

        var description = await agent.BuildLocalDescriptionAsync(LocalRtp, sharedMediaSocket: null, videoEndPoint);

        Assert.NotNull(description);
        var videoCandidate = Assert.Single(description!.VideoCandidates);
        Assert.Equal("host", videoCandidate.Type);
        Assert.Equal(1, videoCandidate.Component); // RTP component of the video stream
        Assert.Equal(40002, videoCandidate.Port);  // the video port, not the audio port
        Assert.Equal(LocalRtp.Address.ToString(), videoCandidate.Address);
        // The audio candidates stay on their own port.
        Assert.DoesNotContain(description.Candidates, c => c.Port == 40002);
    }

    [Fact]
    public async Task Gathering_yields_no_video_candidates_without_a_video_endpoint()
    {
        var agent = Agent(Config(), new FakeStunProbe { ReflexiveEndPoint = Srflx });

        var description = await agent.BuildLocalDescriptionAsync(LocalRtp);

        Assert.NotNull(description);
        Assert.Empty(description!.VideoCandidates);
    }

    [Fact]
    public async Task Gathering_yields_no_video_candidates_when_ice_disabled()
    {
        // ICE disabled short-circuits before any candidate build — a video endpoint must not leak
        // a candidate through.
        var agent = Agent(new IceConfiguration { Enabled = false }, new FakeStunProbe());
        var videoEndPoint = new IPEndPoint(LocalRtp.Address, 40002);

        Assert.Null(await agent.BuildLocalDescriptionAsync(LocalRtp, sharedMediaSocket: null, videoEndPoint));
    }

    [Fact]
    public async Task Restart_gathering_produces_fresh_ice_credentials()
    {
        // RFC 8445 §9.1.1.1: an ICE restart uses new ufrag/pwd. Re-gathering yields fresh creds,
        // so the peer detects the restart (see IceRestartDetector).
        var agent = Agent(Config(), new FakeStunProbe { ReflexiveEndPoint = Srflx });

        var first = await agent.BuildLocalDescriptionAsync(LocalRtp);
        var second = await agent.BuildLocalDescriptionAsync(LocalRtp);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Ufrag, second!.Ufrag);
        Assert.NotEqual(first.Pwd, second.Pwd);
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
    public async Task Connectivity_check_carries_local_priority_and_controlling_role()
    {
        // RFC 8445 §7.2.2 wiring: the agent hands PRIORITY (the local candidate priority), the
        // role derived from IceControlling, and a tie-breaker derived from the local ICE password
        // (so the inbound handler computes the same value) to the probe.
        var probe = new FakeStunProbe { ConnectivityResults = { [_ => true] = true } };
        var agent = Agent(Config(), probe);
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([Candidate("host", LocalRtp, priority: 123456)], [Candidate("host", remote, priority: 200)]));

        Assert.True(result.HasSelectedPair);
        Assert.Equal(123456u, probe.LastPriority);
        Assert.True(probe.LastIsControlling);
        Assert.Equal(IceTieBreaker.Derive("localP"), probe.LastTieBreaker);
    }

    [Fact]
    public async Task Connectivity_check_uses_controlled_role_when_not_controlling()
    {
        // RFC 8445 §5.1.1: the answerer is controlled. When IceControlling is false the check
        // must carry ICE-CONTROLLED, not ICE-CONTROLLING.
        var probe = new FakeStunProbe { ConnectivityResults = { [_ => true] = true } };
        var agent = Agent(Config(), probe);
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([Candidate("host", LocalRtp)], [Candidate("host", remote)], controlling: false));

        Assert.True(result.HasSelectedPair);
        Assert.False(probe.LastIsControlling);
        Assert.Equal(IceTieBreaker.Derive("localP"), probe.LastTieBreaker);
    }

    [Fact]
    public async Task Controlling_agent_nominates_selected_pair_with_use_candidate()
    {
        // RFC 8445 §8.1.1: after an ordinary check validates the pair, the controlling agent
        // re-checks it with USE-CANDIDATE to nominate it.
        var probe = new FakeStunProbe { ConnectivityResults = { [_ => true] = true } };
        var agent = Agent(Config(retries: 0), probe);
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([Candidate("host", LocalRtp)], [Candidate("host", remote)]));

        Assert.True(result.HasSelectedPair);
        Assert.True(result.Nominated);
        // Ordinary check (no USE-CANDIDATE), then the nomination re-check (USE-CANDIDATE).
        Assert.Equal(new[] { false, true }, probe.UseCandidateHistory);
    }

    [Fact]
    public async Task Pair_stays_selected_but_unnominated_when_nomination_check_fails()
    {
        // The ordinary check validates the pair; the nomination re-check fails. The reachable
        // pair is still selected (Connected), only not marked nominated.
        var probe = new FakeStunProbe { ConnectivityResults = { [_ => true] = true }, FailNomination = true };
        var agent = Agent(Config(retries: 0), probe);
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([Candidate("host", LocalRtp)], [Candidate("host", remote)]));

        Assert.True(result.HasSelectedPair);
        Assert.False(result.Nominated);
        Assert.Equal(CallIceNegotiationState.Connected, result.State);
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
        // 2 ordinary-check attempts (fail, then succeed) + 1 nomination check on the valid pair.
        Assert.Equal(3, probe.ConnectivityAttempts);
    }

    [Fact]
    public async Task Srflx_pair_is_selected_when_host_paths_fail()
    {
        // STUN-only path on selection level: NAT'd peers reach each other via their
        // server-reflexive candidates while direct host-host checks fail.
        var remoteSrflx = new IPEndPoint(IPAddress.Parse("203.0.113.99"), 51000);
        var probe = new FakeStunProbe
        {
            ConnectivityResults = { [remote => remote.Equals(remoteSrflx)] = true },
        };
        var agent = Agent(Config(), probe);

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters(
                [Candidate("host", LocalRtp, priority: 200), Candidate("srflx", Srflx, priority: 100)],
                [
                    Candidate("host", new IPEndPoint(IPAddress.Parse("192.0.2.30"), 41000), priority: 200),
                    Candidate("srflx", remoteSrflx, priority: 100),
                ]));

        Assert.True(result.HasSelectedPair);
        Assert.Equal("srflx", result.RemoteCandidate!.Type);
        Assert.Equal(remoteSrflx, result.RemoteEndPoint);
    }

    [Fact]
    public async Task Gathering_passes_the_shared_media_socket_to_the_stun_probe()
    {
        // Regression: gathering must reuse the reserved RTP socket for the STUN query —
        // binding a second socket to the media port failed with "address already in use".
        var probe = new FakeStunProbe { ReflexiveEndPoint = Srflx };
        var agent = Agent(Config(), probe);
        using var reservation = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);

        await agent.BuildLocalDescriptionAsync(LocalRtp, reservation);

        Assert.Same(reservation, probe.ObservedSharedSocket);
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
    public async Task Missing_credentials_fail_with_dedicated_reason()
    {
        var agent = Agent(Config(), new FakeStunProbe());

        var noCredentials = new CallMediaParameters
        {
            LocalEndPoint = LocalRtp,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.20"), 42000),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            PayloadTypeCodecMap = new Dictionary<int, string> { [0] = "PCMU" },
            IceEnabled = true, // metadata present, but ufrag/pwd missing
        };

        var result = await agent.SelectCandidatePairAsync(CallId.New(), noCredentials);

        Assert.Equal("ice_credentials_missing", result.ReasonCode);
        Assert.Equal(CallIceNegotiationState.Failed, result.State);
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

    [Fact]
    public async Task Address_family_mismatch_yields_no_candidate_pairs()
    {
        // Both sides advertise a usable UDP RTP candidate, but they cannot be paired
        // (RFC 8445 §6.1.2.2: pairing requires the same address family). The check list
        // is empty and selection reports the dedicated reason without running any check.
        var agent = Agent(Config(), new FakeStunProbe());
        var localV4 = Candidate("host", LocalRtp);
        var remoteV6 = Candidate("host", new IPEndPoint(IPAddress.Parse("2001:db8::20"), 41000));

        var result = await agent.SelectCandidatePairAsync(
            CallId.New(),
            Parameters([localV4], [remoteV6]));

        Assert.False(result.HasSelectedPair);
        Assert.Equal(CallIceNegotiationState.Failed, result.State);
        Assert.Equal("ice_no_candidate_pairs", result.ReasonCode);
    }

    // --- Fakes ---

    private sealed class FakeStunProbe : IIceStunProbe
    {
        public IPEndPoint? ReflexiveEndPoint { get; init; }
        public Dictionary<Func<IPEndPoint, bool>, bool> ConnectivityResults { get; } = [];
        public int SucceedOnAttempt { get; init; }
        public bool FailNomination { get; init; }
        public int ConnectivityAttempts;
        public System.Net.Sockets.Socket? ObservedSharedSocket;
        public uint LastPriority;
        public bool LastIsControlling;
        public ulong LastTieBreaker;
        public bool LastUseCandidate;
        public List<bool> UseCandidateHistory { get; } = [];

        public Task<IPEndPoint?> TryGetServerReflexiveEndPointAsync(
            IPEndPoint localEndPoint, IceServerConfiguration server,
            System.Net.Sockets.Socket? sharedUdpSocket = null, CancellationToken ct = default)
        {
            ObservedSharedSocket = sharedUdpSocket;
            return Task.FromResult(ReflexiveEndPoint);
        }

        public Task<bool> TryCheckConnectivityAsync(
            IPEndPoint localEndPoint, IPEndPoint remoteEndPoint,
            string localIceUfrag, string remoteIceUfrag, string remoteIcePassword,
            uint localCandidatePriority, bool isControlling, ulong tieBreaker,
            bool useCandidate, TimeSpan timeout, CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref ConnectivityAttempts);
            LastPriority = localCandidatePriority;
            LastIsControlling = isControlling;
            LastTieBreaker = tieBreaker;
            LastUseCandidate = useCandidate;
            UseCandidateHistory.Add(useCandidate);

            if (useCandidate && FailNomination)
                return Task.FromResult(false);

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
