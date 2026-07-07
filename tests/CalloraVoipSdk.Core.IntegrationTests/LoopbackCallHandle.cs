using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Test-side view over one leg of a loopback call. Wraps the real
/// <see cref="SipCoreCallChannel"/> and exposes event-driven awaitables for connection,
/// termination, inbound DTMF, and inbound audio so tests never rely on fixed sleeps.
/// Forwards call termination into the real <c>CallMediaOrchestrator</c> exactly like the
/// production <c>CallManager</c> would, so the media session is torn down on BYE.
/// </summary>
internal sealed class LoopbackCallHandle
{
    private readonly SipCoreCallChannel _channel;
    private readonly CallMediaOrchestrator _orchestrator;
    private readonly ICall _call;

    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte> _firstDtmf = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile CallMediaParameters? _negotiatedMedia;
    private CallState _lastState = CallState.Idle;

    internal LoopbackCallHandle(SipCoreCallChannel channel, CallMediaOrchestrator orchestrator, ICall call)
    {
        _channel = channel;
        _orchestrator = orchestrator;
        _call = call;
    }

    /// <summary>The underlying real SIP core call channel.</summary>
    public SipCoreCallChannel Channel => _channel;

    /// <summary>Completes when the call reaches <see cref="CallState.Connected"/>.</summary>
    public Task Connected => _connected.Task;

    /// <summary>Completes when the call reaches <see cref="CallState.Terminated"/>.</summary>
    public Task Terminated => _terminated.Task;

    /// <summary>Completes with the tone code of the first inbound RFC 4733 DTMF event.</summary>
    public Task<byte> FirstDtmf => _firstDtmf.Task;

    /// <summary>The media parameters this leg negotiated over SDP (null before negotiation).</summary>
    public CallMediaParameters? NegotiatedMedia => _negotiatedMedia;

    /// <summary>
    /// Binds the domain callbacks so state transitions and inbound DTMF drive the awaitables,
    /// captures the negotiated media parameters, and terminated calls tear down the
    /// orchestrated media session.
    /// </summary>
    internal void Bind()
    {
        _channel.MediaParametersNegotiated += (_, parameters) => _negotiatedMedia = parameters;
        _channel.BindCallbacks(new CallChannelCallbacks(
            OnStateChange: OnState,
            OnDtmf: (tone, _) => _firstDtmf.TrySetResult(tone)));
    }

    private void OnState(CallState state)
    {
        var previous = _lastState;
        _lastState = state;

        if (state == CallState.Connected)
            _connected.TrySetResult();

        if (state == CallState.Terminated)
        {
            // Mirror production: CallManager forwards terminal state to the orchestrator,
            // which disposes the RTP/SRTP media session and unwires the channel.
            _orchestrator.OnCallStateChanged(
                this,
                new CallStateChangedEventArgs(previous, CallState.Terminated, _call));
            _terminated.TrySetResult();
        }
    }

    /// <summary>
    /// Adds an inbound-audio listener and returns a task that completes once a frame whose
    /// payload exactly equals <paramref name="expected"/> is delivered. Concealment/other
    /// frames never false-match because the whole payload must be byte-identical.
    /// </summary>
    public async Task<byte[]> ExpectAudioAsync(byte[] expected, TimeSpan timeout)
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Listener(CallAudioFrame frame)
        {
            if (frame.Payload.Length == expected.Length
                && frame.Payload.AsSpan().SequenceEqual(expected))
            {
                received.TrySetResult(frame.Payload);
            }
        }

        _channel.AddAudioFrameListener(Listener);
        try
        {
            var winner = await Task.WhenAny(received.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (winner != received.Task)
                throw new TimeoutException("Expected audio frame was not delivered within the timeout.");
            return await received.Task.ConfigureAwait(false);
        }
        finally
        {
            _channel.RemoveAudioFrameListener(Listener);
        }
    }

    /// <summary>Sends one PCMU audio frame (20 ms / 160 samples) over the real RTP path.</summary>
    public Task SendAudioAsync(byte[] payload) =>
        _channel.SendAudioFrameAsync(new CallAudioFrame(payload, PayloadType: 0, DurationRtpUnits: 160));

    /// <summary>Sends one RFC 4733 DTMF tone over the real RTP path.</summary>
    public Task SendDtmfAsync(byte toneCode) => _channel.SendDtmfAsync(toneCode);

    /// <summary>Terminates the dialog (sends BYE) on the real signaling path.</summary>
    public Task HangupAsync() => _channel.HangupAsync();
}
