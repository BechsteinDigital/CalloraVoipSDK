using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// One in-process SDK endpoint on 127.0.0.1: a real UDP <see cref="SipTransportRuntime"/>,
/// a real <see cref="SipCallSignalingService"/>, and a real <see cref="CallMediaOrchestrator"/>
/// backed by the real <see cref="RtpCallMediaSessionFactory"/>. No transport or media mocks.
///
/// Registrar-less by construction: the UAC sends INVITE straight to the UAS address:port via
/// <see cref="SipCallSignalingService.InviteAsync"/>, and the UAS answers directly from its
/// <c>IncomingInvite</c> handler. ICE is disabled (no ICE agent) so the loopback host candidate
/// is used directly and candidate gathering cannot perturb the direct connection.
/// </summary>
internal sealed class LoopbackSdkEndpoint : IDisposable
{
    private static readonly NullLoggerFactory Logs = NullLoggerFactory.Instance;

    private readonly string _localUser;
    private readonly SrtpPolicy _answerPolicy;
    private readonly SdpNegotiator _negotiator = new();
    private readonly SipTransportRuntime _transport;
    private readonly SipCallSignalingService _signaling;
    private readonly CallMediaOrchestrator _orchestrator;
    private readonly List<SipCoreCallChannel> _channels = new();
    private readonly object _sync = new();
    private TaskCompletionSource<LoopbackCallHandle> _inbound =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    internal LoopbackSdkEndpoint(string localUser, SrtpPolicy answerPolicy)
    {
        _localUser = localUser;
        _answerPolicy = answerPolicy;

        var sdpProvider = new SipSessionSdpProvider
        {
            BuildOffer = (ep, hold) => _negotiator.BuildDefaultSdp(ep, hold, null),
            TryNegotiateAnswer = (offer, ep, hold) =>
                offer is null ? null : _negotiator.TryBuildNegotiatedAnswer(offer, ep, hold, null),
            TryParseMediaParameters = (sdp, ep) => _negotiator.TryParseMediaParameters(sdp, ep),
            IsRemoteHold = _negotiator.IsRemoteHoldSdp,
        };

        _transport = new SipTransportRuntime(Logs);
        _signaling = new SipCallSignalingService(
            _transport,
            new SipDigestAuthentication(),
            Logs,
            sdpProvider,
            NullSipTelemetrySink.Instance);
        _orchestrator = new CallMediaOrchestrator(
            new RtpCallMediaSessionFactory(Logs),
            Logs,
            new RtcpPacketCodec(),
            iceAgent: null);

        _signaling.IncomingInvite += OnIncomingInvite;
    }

    /// <summary>Bound SIP UDP port on 127.0.0.1 (dynamically allocated).</summary>
    public int SipPort => _transport.LocalEndPoint.Port;

    /// <summary>Arms and returns a task that completes when the next inbound call is answered.</summary>
    public Task<LoopbackCallHandle> NextInboundCallAsync()
    {
        lock (_sync)
        {
            _inbound = new TaskCompletionSource<LoopbackCallHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _inbound.Task;
        }
    }

    /// <summary>
    /// Places a registrar-less direct-IP call to <c>sip:{remoteUser}@127.0.0.1:{remotePort}</c>.
    /// Blocks until the INVITE transaction completes (200 OK), then returns the connected leg.
    /// </summary>
    public async Task<LoopbackCallHandle> DialAsync(
        int remotePort,
        string remoteUser,
        SrtpPolicy policy,
        CancellationToken ct)
    {
        var channel = CreateChannel(policy, "outbound");
        var call = new LoopbackTestCall();
        _orchestrator.AttachCall(call, channel);
        var handle = new LoopbackCallHandle(channel, _orchestrator, call);
        handle.Bind();

        var localMediaEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        var offer = await channel.BuildOfferSdpAsync(localMediaEndPoint, hold: false, ct).ConfigureAwait(false);

        var request = new SipInviteRequest
        {
            LocalUsername = _localUser,
            LocalDomain = "127.0.0.1",
            AuthUsername = _localUser,
            RemoteUri = $"sip:{remoteUser}@127.0.0.1:{remotePort}",
            RemotePort = remotePort,
            SessionDescription = offer,
            Transport = SipTransportProtocol.Udp,
            Timeout = TimeSpan.FromSeconds(15),
            UserAgent = "CalloraVoipSdk-LoopbackTest/1.0",
        };

        var session = await _signaling.InviteAsync(request, ct).ConfigureAwait(false);
        channel.AttachSession(session);
        return handle;
    }

    private void OnIncomingInvite(object? sender, SipIncomingInviteEventArgs args)
    {
        var session = args.Session;
        var channel = CreateChannel(_answerPolicy, "inbound");
        channel.AttachSession(session);

        var call = new LoopbackTestCall();
        _orchestrator.AttachCall(call, channel);
        var handle = new LoopbackCallHandle(channel, _orchestrator, call);
        handle.Bind();

        lock (_sync)
            _inbound.TrySetResult(handle);

        _ = AnswerSafeAsync(channel);
    }

    private static async Task AnswerSafeAsync(SipCoreCallChannel channel)
    {
        try
        {
            await channel.AnswerAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A failed answer surfaces to the dialer as an INVITE transaction failure, which the
            // test asserts against; swallowing here only avoids an unobserved background task.
        }
    }

    private SipCoreCallChannel CreateChannel(SrtpPolicy policy, string source)
    {
        var channel = new SipCoreCallChannel(
            Logs.CreateLogger<SipCoreCallChannel>(),
            _negotiator,
            NullSipTelemetrySink.Instance,
            policy,
            source,
            iceAgent: null);

        lock (_sync)
            _channels.Add(channel);

        return channel;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _signaling.IncomingInvite -= OnIncomingInvite;
        _orchestrator.Dispose();

        foreach (var channel in _channels)
        {
            try { channel.Dispose(); }
            catch { /* idempotent teardown; ignore */ }
        }

        _signaling.Dispose();
        _transport.Dispose();
    }
}
