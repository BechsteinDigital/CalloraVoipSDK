using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
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

        _logger.LogDebug(
            "ICE state={State}: running connectivity checks for call {CallId} with {PairCount} pairs.",
            CallIceNegotiationState.Checking,
            callId,
            localCandidates.Length * remoteCandidates.Length);

        var orderedPairs = BuildCandidatePairs(parameters.LocalEndPoint, localCandidates, remoteCandidates);
        var retries = Math.Max(0, _configuration.ConnectivityCheckRetries);
        var timeout = _configuration.ConnectivityCheckTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : _configuration.ConnectivityCheckTimeout;

        foreach (var pair in orderedPairs)
        {
            for (var attempt = 0; attempt <= retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var ok = await _stunProbe.TryCheckConnectivityAsync(
                        pair.LocalProbeEndPoint,
                        pair.RemoteEndPoint,
                        parameters.LocalIceUfrag!,
                        parameters.RemoteIceUfrag!,
                        parameters.RemoteIcePwd!,
                        timeout,
                        ct)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    _logger.LogDebug(
                        "ICE check failed for call {CallId} pair {LocalType}/{RemoteType} {Local}->{Remote} attempt {Attempt}/{Attempts}.",
                        callId,
                        pair.LocalCandidate.Type,
                        pair.RemoteCandidate.Type,
                        pair.LocalProbeEndPoint,
                        pair.RemoteEndPoint,
                        attempt + 1,
                        retries + 1);
                    continue;
                }

                _logger.LogInformation(
                    "ICE state={State}: nominated pair for call {CallId}: local={LocalCandidateType} {LocalEndPoint}, remote={RemoteCandidateType} {RemoteEndPoint}.",
                    CallIceNegotiationState.Nominating,
                    callId,
                    pair.LocalCandidate.Type,
                    pair.LocalProbeEndPoint,
                    pair.RemoteCandidate.Type,
                    pair.RemoteEndPoint);

                var selected = new CallIceSelectionResult
                {
                    State = CallIceNegotiationState.Connected,
                    HasSelectedPair = true,
                    LocalEndPoint = pair.LocalProbeEndPoint,
                    RemoteEndPoint = pair.RemoteEndPoint,
                    LocalCandidate = pair.LocalCandidate,
                    RemoteCandidate = pair.RemoteCandidate,
                    ReasonCode = "ice_checks_succeeded"
                };
                PublishSelectionEvent(callId, selected, localCandidates.Length, remoteCandidates.Length);
                return selected;
            }
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

    private static IReadOnlyList<OrderedCandidatePair> BuildCandidatePairs(
        IPEndPoint negotiatedLocalEndPoint,
        IReadOnlyList<CallIceCandidate> localCandidates,
        IReadOnlyList<CallIceCandidate> remoteCandidates)
    {
        var pairs = new List<OrderedCandidatePair>(localCandidates.Count * remoteCandidates.Count);
        foreach (var localCandidate in localCandidates)
        {
            var localProbeEndPoint = ResolveLocalProbeEndPoint(negotiatedLocalEndPoint, localCandidate);

            foreach (var remoteCandidate in remoteCandidates)
            {
                if (!IPAddress.TryParse(remoteCandidate.Address, out var remoteAddress)
                    || remoteCandidate.Port <= 0)
                    continue;

                var remoteEndPoint = new IPEndPoint(remoteAddress, remoteCandidate.Port);
                pairs.Add(new OrderedCandidatePair(
                    localCandidate,
                    remoteCandidate,
                    localProbeEndPoint,
                    remoteEndPoint,
                    ComputePairPriority(localCandidate.Priority, remoteCandidate.Priority)));
            }
        }

        return pairs
            .OrderByDescending(static p => p.PairPriority)
            .ToArray();
    }

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

    private static ulong ComputePairPriority(long localPriority, long remotePriority)
    {
        var local = (ulong)Math.Max(0, localPriority);
        var remote = (ulong)Math.Max(0, remotePriority);
        var min = Math.Min(local, remote);
        var max = Math.Max(local, remote);
        var tieBreaker = local > remote ? 1UL : 0UL;
        return (min << 32) + (max << 1) + tieBreaker;
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
