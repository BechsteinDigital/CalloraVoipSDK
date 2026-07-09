using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Application-layer ICE agent that gathers local candidates and executes
/// candidate-pair connectivity checks for one call media leg.
/// </summary>
internal sealed class CallIceAgent : ICallIceAgent
{
    private const int RtpComponent = 1;
    private readonly IceConfiguration _configuration;
    private readonly IIceStunProbe _stunProbe;
    private readonly IIceTurnRelayAllocator? _turnRelayAllocator;
    private readonly IIceTelemetrySink? _telemetry;
    private readonly ILogger<CallIceAgent> _logger;

    // RFC 8445 §5.2: the tie-breaker is chosen once per agent and carried in the
    // ICE-CONTROLLING / ICE-CONTROLLED attribute of every connectivity check.
    private readonly ulong _tieBreaker = IceTieBreaker.Generate();

    /// <summary>
    /// Creates an ICE agent with runtime configuration and the STUN probe port.
    /// </summary>
    internal CallIceAgent(
        IceConfiguration configuration,
        IIceStunProbe stunProbe,
        ILoggerFactory loggerFactory,
        IIceTurnRelayAllocator? turnRelayAllocator = null,
        IIceTelemetrySink? telemetry = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _stunProbe = stunProbe ?? throw new ArgumentNullException(nameof(stunProbe));
        _turnRelayAllocator = turnRelayAllocator;
        _telemetry = telemetry;
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<CallIceAgent>();
    }

    /// <inheritdoc />
    public async Task<CallIceLocalDescription?> BuildLocalDescriptionAsync(
        IPEndPoint localEndPoint,
        System.Net.Sockets.Socket? sharedMediaSocket = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);

        if (!_configuration.Enabled)
        {
            _logger.LogDebug("ICE gathering skipped because ICE is disabled.");
            PublishGatheringEvent(localEndPoint, candidateCount: 0, hostCount: 0, srflxCount: 0, relayCount: 0);
            return null;
        }

        _logger.LogDebug("ICE state={State}: gathering local candidates for {LocalEndPoint}.", CallIceNegotiationState.Gathering, localEndPoint);

        var candidates = new List<CallIceCandidate>
        {
            BuildHostCandidate(localEndPoint)
        };

        var stunServers = _configuration.Servers
            .Where(static s => s.Type == IceServerType.Stun)
            .ToArray();

        for (var i = 0; i < stunServers.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var server = stunServers[i];
            var mapped = await _stunProbe
                .TryGetServerReflexiveEndPointAsync(localEndPoint, server, sharedMediaSocket, ct)
                .ConfigureAwait(false);
            if (mapped is null)
                continue;

            candidates.Add(BuildServerReflexiveCandidate(localEndPoint, mapped, foundationIndex: i + 1));
        }

        var turnServers = _configuration.Servers
            .Where(static s => s.Type == IceServerType.Turn)
            .ToArray();
        if (_turnRelayAllocator is null && turnServers.Length > 0)
        {
            _logger.LogWarning(
                "ICE state={State}: TURN servers are configured but no relay allocator is available.",
                CallIceNegotiationState.Gathering);
            _telemetry?.PublishEvent(new IceTelemetryEvent
            {
                EventType = "sip.media.ice.gathering.turn_allocator_missing",
                CorrelationId = $"ICE:GATHER:{localEndPoint}",
                Attributes = new Dictionary<string, string>
                {
                    ["local_endpoint"] = localEndPoint.ToString(),
                    ["turn_server_count"] = turnServers.Length.ToString()
                }
            });
        }

        if (_turnRelayAllocator is not null)
        {
            for (var i = 0; i < turnServers.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var server = turnServers[i];
                var allocation = await _turnRelayAllocator
                    .TryAllocateRelayAsync(localEndPoint, server, ct)
                    .ConfigureAwait(false);
                if (allocation is null)
                    continue;

                var relatedEndPoint = allocation.MappedEndPoint ?? localEndPoint;
                candidates.Add(BuildRelayCandidate(allocation.RelayedEndPoint, relatedEndPoint, foundationIndex: i + 1));
                if (allocation.MappedEndPoint is not null)
                    candidates.Add(BuildServerReflexiveCandidate(localEndPoint, allocation.MappedEndPoint, foundationIndex: stunServers.Length + i + 1));
            }
        }

        var uniqueCandidates = DeduplicateCandidates(candidates);
        _logger.LogDebug(
            "ICE state={State}: gathered {CandidateCount} candidates for {LocalEndPoint}.",
            CallIceNegotiationState.Gathered,
            uniqueCandidates.Count,
            localEndPoint);
        PublishGatheringEvent(
            localEndPoint,
            uniqueCandidates.Count,
            uniqueCandidates.Count(static c => c.Type.Equals("host", StringComparison.OrdinalIgnoreCase)),
            uniqueCandidates.Count(static c => c.Type.Equals("srflx", StringComparison.OrdinalIgnoreCase)),
            uniqueCandidates.Count(static c => c.Type.Equals("relay", StringComparison.OrdinalIgnoreCase)));

        return new CallIceLocalDescription
        {
            Ufrag = GenerateUfrag(),
            Pwd = GeneratePassword(),
            Candidates = uniqueCandidates
        };
    }

    /// <inheritdoc />
    public async Task<CallIceSelectionResult> SelectCandidatePairAsync(
        CallId callId,
        CallMediaParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (!_configuration.Enabled)
        {
            var disabled = new CallIceSelectionResult
            {
                State = CallIceNegotiationState.Disabled,
                ReasonCode = "ice_disabled"
            };
            PublishSelectionEvent(callId, disabled, localCandidateCount: 0, remoteCandidateCount: 0);
            return disabled;
        }

        if (!parameters.IceEnabled)
        {
            var metadataMissing = new CallIceSelectionResult
            {
                State = CallIceNegotiationState.Disabled,
                ReasonCode = "ice_metadata_missing"
            };
            PublishSelectionEvent(callId, metadataMissing, localCandidateCount: 0, remoteCandidateCount: 0);
            return metadataMissing;
        }

        if (string.IsNullOrWhiteSpace(parameters.LocalIceUfrag)
            || string.IsNullOrWhiteSpace(parameters.RemoteIceUfrag)
            || string.IsNullOrWhiteSpace(parameters.RemoteIcePwd))
        {
            _logger.LogWarning(
                "ICE state={State}: missing ICE credentials for call {CallId}.",
                CallIceNegotiationState.Failed,
                callId);
            var credentialsMissing = new CallIceSelectionResult
            {
                State = CallIceNegotiationState.Failed,
                ReasonCode = "ice_credentials_missing"
            };
            PublishSelectionEvent(callId, credentialsMissing, localCandidateCount: 0, remoteCandidateCount: 0);
            return credentialsMissing;
        }

        var localCandidates = parameters.LocalIceCandidates
            .Where(IsSupportedUdpRtpCandidate)
            .ToArray();
        var remoteCandidates = parameters.RemoteIceCandidates
            .Where(IsSupportedUdpRtpCandidate)
            .ToArray();

        if (localCandidates.Length == 0 || remoteCandidates.Length == 0)
        {
            _logger.LogWarning(
                "ICE state={State}: no supported local/remote UDP RTP candidates for call {CallId} (local={LocalCount}, remote={RemoteCount}).",
                CallIceNegotiationState.Failed,
                callId,
                localCandidates.Length,
                remoteCandidates.Length);
            var candidatesMissing = new CallIceSelectionResult
            {
                State = CallIceNegotiationState.Failed,
                ReasonCode = "ice_candidates_missing"
            };
            PublishSelectionEvent(callId, candidatesMissing, localCandidates.Length, remoteCandidates.Length);
            return candidatesMissing;
        }

        // I3: the local agent defaults to the controlling role. Deriving the role from the SDP
        // offer/answer direction (offerer = controlling) and resolving inbound role conflicts
        // (RFC 8445 §7.3.1.1) is a later package; the pair-priority formula (§6.1.2.3) still
        // needs a role, and the controlling assignment matches the common outbound-INVITE case.
        const IceRole role = IceRole.Controlling;

        var checkList = IceCheckList.Create(localCandidates, remoteCandidates, role);
        if (checkList.Count == 0)
        {
            _logger.LogWarning(
                "ICE state={State}: local/remote candidates for call {CallId} could not be paired "
                + "(transport or address-family mismatch).",
                CallIceNegotiationState.Failed,
                callId);
            var unpairable = new CallIceSelectionResult
            {
                State = CallIceNegotiationState.Failed,
                ReasonCode = "ice_no_candidate_pairs"
            };
            PublishSelectionEvent(callId, unpairable, localCandidates.Length, remoteCandidates.Length);
            return unpairable;
        }

        _logger.LogDebug(
            "ICE state={State}: running connectivity checks for call {CallId} with {PairCount} pairs.",
            CallIceNegotiationState.Checking,
            callId,
            checkList.Count);

        var retries = Math.Max(0, _configuration.ConnectivityCheckRetries);
        var timeout = _configuration.ConnectivityCheckTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : _configuration.ConnectivityCheckTimeout;

        var isControlling = role == IceRole.Controlling;
        var selectedPair = await IceConnectivityScheduler.RunAsync(
                checkList,
                (pair, token) => CheckPairAsync(callId, parameters, pair, isControlling, useCandidate: false, timeout, retries, token),
                ct)
            .ConfigureAwait(false);

        if (selectedPair is not null)
        {
            var localProbeEndPoint = ResolveLocalProbeEndPoint(parameters.LocalEndPoint, selectedPair.Local);
            var remoteEndPoint = new IPEndPoint(
                IPAddress.Parse(selectedPair.Remote.Address),
                selectedPair.Remote.Port);

            // Regular nomination (RFC 8445 §8.1.1): the controlling agent re-checks the selected
            // valid pair with USE-CANDIDATE to nominate it. The pair stays usable even if the
            // nomination check is not confirmed — it already passed an ordinary check.
            if (isControlling)
            {
                _logger.LogDebug(
                    "ICE state={State}: nominating pair for call {CallId}: local={LocalCandidateType} {LocalEndPoint}, remote={RemoteCandidateType} {RemoteEndPoint}.",
                    CallIceNegotiationState.Nominating,
                    callId,
                    selectedPair.Local.Type,
                    localProbeEndPoint,
                    selectedPair.Remote.Type,
                    remoteEndPoint);

                selectedPair.Nominated = await CheckPairAsync(
                        callId, parameters, selectedPair, isControlling, useCandidate: true, timeout, retries, ct)
                    .ConfigureAwait(false);

                if (!selectedPair.Nominated)
                    _logger.LogWarning(
                        "ICE state={State}: nomination check for call {CallId} was not confirmed; using the valid pair unnominated.",
                        CallIceNegotiationState.Nominating,
                        callId);
            }

            _logger.LogInformation(
                "ICE state={State}: selected pair for call {CallId} (nominated={Nominated}): local={LocalCandidateType} {LocalEndPoint}, remote={RemoteCandidateType} {RemoteEndPoint}.",
                CallIceNegotiationState.Connected,
                callId,
                selectedPair.Nominated,
                selectedPair.Local.Type,
                localProbeEndPoint,
                selectedPair.Remote.Type,
                remoteEndPoint);

            var selected = new CallIceSelectionResult
            {
                State = CallIceNegotiationState.Connected,
                HasSelectedPair = true,
                Nominated = selectedPair.Nominated,
                LocalEndPoint = localProbeEndPoint,
                RemoteEndPoint = remoteEndPoint,
                LocalCandidate = selectedPair.Local,
                RemoteCandidate = selectedPair.Remote,
                ReasonCode = "ice_checks_succeeded"
            };
            PublishSelectionEvent(callId, selected, localCandidates.Length, remoteCandidates.Length);
            return selected;
        }

        _logger.LogWarning(
            "ICE state={State}: no reachable candidate pair for call {CallId}; keeping negotiated SDP endpoints.",
            CallIceNegotiationState.Failed,
            callId);

        var failed = new CallIceSelectionResult
        {
            State = CallIceNegotiationState.Failed,
            ReasonCode = "ice_checks_failed"
        };
        PublishSelectionEvent(callId, failed, localCandidates.Length, remoteCandidates.Length);
        return failed;
    }

    /// <summary>
    /// Runs one connectivity check for <paramref name="pair"/>, carrying the PRIORITY and role
    /// attributes (and USE-CANDIDATE when <paramref name="useCandidate"/> nominates the pair) and
    /// retrying up to <paramref name="retries"/> additional times before reporting failure.
    /// Resolves the local probe source and remote target endpoints from the pair's candidates.
    /// </summary>
    private async Task<bool> CheckPairAsync(
        CallId callId,
        CallMediaParameters parameters,
        IceCandidatePair pair,
        bool isControlling,
        bool useCandidate,
        TimeSpan timeout,
        int retries,
        CancellationToken ct)
    {
        if (!IPAddress.TryParse(pair.Remote.Address, out var remoteAddress) || pair.Remote.Port <= 0)
            return false;

        var localProbeEndPoint = ResolveLocalProbeEndPoint(parameters.LocalEndPoint, pair.Local);
        var remoteEndPoint = new IPEndPoint(remoteAddress, pair.Remote.Port);

        // PRIORITY carried in the check (RFC 8445 §7.2.2). Simplification: the candidate's own
        // priority is sent; strictly it should be the priority the peer would assign a
        // peer-reflexive candidate learned from this check — unobservable until prflx (a later
        // package), so the approximation is harmless here.
        var priority = (uint)Math.Clamp(pair.Local.Priority, 0L, uint.MaxValue);

        for (var attempt = 0; attempt <= retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var ok = await _stunProbe.TryCheckConnectivityAsync(
                    localProbeEndPoint,
                    remoteEndPoint,
                    parameters.LocalIceUfrag!,
                    parameters.RemoteIceUfrag!,
                    parameters.RemoteIcePwd!,
                    priority,
                    isControlling,
                    _tieBreaker,
                    useCandidate,
                    timeout,
                    ct)
                .ConfigureAwait(false);

            if (ok)
                return true;

            _logger.LogDebug(
                "ICE check failed for call {CallId} pair {LocalType}/{RemoteType} {Local}->{Remote} attempt {Attempt}/{Attempts}.",
                callId,
                pair.Local.Type,
                pair.Remote.Type,
                localProbeEndPoint,
                remoteEndPoint,
                attempt + 1,
                retries + 1);
        }

        return false;
    }

    private static CallIceCandidate BuildHostCandidate(IPEndPoint localEndPoint)
        => new()
        {
            Foundation = "host",
            Component = RtpComponent,
            Transport = "UDP",
            Priority = BuildCandidatePriority(typePreference: 126, component: RtpComponent),
            Address = localEndPoint.Address.ToString(),
            Port = localEndPoint.Port,
            Type = "host"
        };

    private static CallIceCandidate BuildServerReflexiveCandidate(
        IPEndPoint localEndPoint,
        IPEndPoint mappedEndPoint,
        int foundationIndex)
        => new()
        {
            Foundation = $"srflx-{foundationIndex}",
            Component = RtpComponent,
            Transport = "UDP",
            Priority = BuildCandidatePriority(typePreference: 100, component: RtpComponent),
            Address = mappedEndPoint.Address.ToString(),
            Port = mappedEndPoint.Port,
            Type = "srflx",
            RelatedAddress = localEndPoint.Address.ToString(),
            RelatedPort = localEndPoint.Port
        };

    private static CallIceCandidate BuildRelayCandidate(
        IPEndPoint relayedEndPoint,
        IPEndPoint relatedEndPoint,
        int foundationIndex)
        => new()
        {
            Foundation = $"relay-{foundationIndex}",
            Component = RtpComponent,
            Transport = "UDP",
            Priority = BuildCandidatePriority(typePreference: 0, component: RtpComponent),
            Address = relayedEndPoint.Address.ToString(),
            Port = relayedEndPoint.Port,
            Type = "relay",
            RelatedAddress = relatedEndPoint.Address.ToString(),
            RelatedPort = relatedEndPoint.Port
        };

    private static long BuildCandidatePriority(int typePreference, int component)
    {
        var localPreference = 65_535;
        var componentPreference = 256 - component;
        return ((long)typePreference << 24)
               + ((long)localPreference << 8)
               + componentPreference;
    }

    private static string GenerateUfrag() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

    private static string GeneratePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static IReadOnlyList<CallIceCandidate> DeduplicateCandidates(IEnumerable<CallIceCandidate> candidates)
    {
        var unique = new List<CallIceCandidate>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var key = $"{candidate.Component}|{candidate.Transport}|{candidate.Type}|{candidate.Address}|{candidate.Port}";

            if (keys.Add(key))
                unique.Add(candidate);
        }

        return unique;
    }

    private static bool IsSupportedUdpRtpCandidate(CallIceCandidate candidate)
        => candidate.Component == RtpComponent
           && candidate.Port > 0
           && candidate.Transport.Equals("UDP", StringComparison.OrdinalIgnoreCase);

    private static IPEndPoint ResolveLocalProbeEndPoint(
        IPEndPoint negotiatedLocalEndPoint,
        CallIceCandidate localCandidate)
    {
        if (localCandidate.Type.Equals("host", StringComparison.OrdinalIgnoreCase)
            && localCandidate.Port > 0
            && IPAddress.TryParse(localCandidate.Address, out var hostAddress))
        {
            return new IPEndPoint(hostAddress, localCandidate.Port);
        }

        if (localCandidate.RelatedPort is > 0
            && !string.IsNullOrWhiteSpace(localCandidate.RelatedAddress)
            && IPAddress.TryParse(localCandidate.RelatedAddress, out var relatedAddress))
        {
            return new IPEndPoint(relatedAddress, localCandidate.RelatedPort.Value);
        }

        if (localCandidate.Port > 0)
            return new IPEndPoint(negotiatedLocalEndPoint.Address, localCandidate.Port);

        return negotiatedLocalEndPoint;
    }

    private void PublishGatheringEvent(
        IPEndPoint localEndPoint,
        int candidateCount,
        int hostCount,
        int srflxCount,
        int relayCount)
    {
        _telemetry?.PublishEvent(new IceTelemetryEvent
        {
            EventType = "sip.media.ice.gathering.completed",
            CorrelationId = $"ICE:GATHER:{localEndPoint}",
            Attributes = new Dictionary<string, string>
            {
                ["local_endpoint"] = localEndPoint.ToString(),
                ["candidate_count"] = candidateCount.ToString(),
                ["host_count"] = hostCount.ToString(),
                ["srflx_count"] = srflxCount.ToString(),
                ["relay_count"] = relayCount.ToString()
            }
        });
    }

    private void PublishSelectionEvent(
        CallId callId,
        CallIceSelectionResult result,
        int localCandidateCount,
        int remoteCandidateCount)
    {
        var attributes = new Dictionary<string, string>
        {
            ["state"] = result.State.ToString(),
            ["selected_pair"] = result.HasSelectedPair ? "true" : "false",
            ["nominated"] = result.Nominated ? "true" : "false",
            ["reason_code"] = result.ReasonCode,
            ["local_candidate_count"] = localCandidateCount.ToString(),
            ["remote_candidate_count"] = remoteCandidateCount.ToString()
        };

        if (result.LocalCandidate is not null)
            attributes["selected_local_type"] = result.LocalCandidate.Type;
        if (result.RemoteCandidate is not null)
            attributes["selected_remote_type"] = result.RemoteCandidate.Type;
        if (result.LocalEndPoint is not null)
            attributes["selected_local_endpoint"] = result.LocalEndPoint.ToString();
        if (result.RemoteEndPoint is not null)
            attributes["selected_remote_endpoint"] = result.RemoteEndPoint.ToString();

        _telemetry?.PublishEvent(new IceTelemetryEvent
        {
            EventType = "sip.media.ice.selection.completed",
            CallId = callId.ToString(),
            CorrelationId = $"{callId}:ICE:SELECTION",
            Attributes = attributes
        });
    }

}
