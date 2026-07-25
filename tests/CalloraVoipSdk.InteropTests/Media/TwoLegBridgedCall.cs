using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Media;

/// <summary>Ergebnis eines bidirektionalen Media-Laufs: die je Seite empfangenen Sequenzmarker.</summary>
public sealed record TwoLegMediaResult(
    IReadOnlyList<uint> CalleeReceivedSequences,
    IReadOnlyList<uint> CallerReceivedSequences);

/// <summary>
/// L4-Fixture: zwei <see cref="VoipClient"/>-Legs über einen echten Asterisk gebrückt (A=6001 wählt
/// Extension 6003 → Asterisk Dial(PJSIP/6003) → B=6003 nimmt inbound an). Kapselt Aufbau, beidseitige
/// Media-Injektion via <see cref="IMediaSender"/> und -Erfassung via <see cref="IMediaReceiver"/>.
/// </summary>
public sealed class TwoLegBridgedCall : IAsyncDisposable
{
    private readonly VoipClient _callerClient;
    private readonly VoipClient _calleeClient;

    public ICall CallerCall { get; }   // A (6001)
    public ICall CalleeCall { get; }   // B (6003)

    private TwoLegBridgedCall(VoipClient callerClient, VoipClient calleeClient, ICall callerCall, ICall calleeCall)
    {
        _callerClient = callerClient;
        _calleeClient = calleeClient;
        CallerCall = callerCall;
        CalleeCall = calleeCall;
    }

    private static VoipClient NewClient() =>
        new(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Disabled,
            PreferredAudioCodecs = new[] { "PCMU" },   // beide Legs PCMU → Same-Codec-Passthrough für die Inhaltsverifikation
        });

    private static async Task<IPhoneLine> RegisterAsync(AsteriskContainer asterisk, VoipClient client, string user, string pass)
    {
        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = user,
                Password = pass,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        if (!reg.IsSuccess)
            throw new InvalidOperationException($"Registrierung {user} fehlgeschlagen: {reg.Status}");
        return reg.Line!;
    }

    /// <summary>Baut den gebrückten Call auf und wartet, bis beide Legs Connected sind.</summary>
    public static async Task<TwoLegBridgedCall> StartAsync(AsteriskContainer asterisk)
    {
        var callerClient = NewClient();
        var calleeClient = NewClient();
        try
        {
            var callerLine = await RegisterAsync(asterisk, callerClient, asterisk.Username, asterisk.Password);
            await RegisterAsync(asterisk, calleeClient, asterisk.BridgeUsername, asterisk.BridgePassword);

            // B nimmt den eingehenden (von Asterisk gebrückten) Call an. Der Handler erfasst nur den Call;
            // A's Dial blockiert bis zum Accept → beides läuft nebenläufig.
            var calleeTcs = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnIncoming(object? _, IncomingCallEventArgs e) => calleeTcs.TrySetResult(e.Call);
            calleeClient.IncomingCall += OnIncoming;

            var dialTask = callerClient.DialAndWaitUntilConnectedAsync(
                callerLine, asterisk.CallTargetUri("6003"),
                new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });

            var calleeCall = await calleeTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
            calleeClient.IncomingCall -= OnIncoming;
            await calleeCall.AcceptAsync();

            var dial = await dialTask;
            if (!dial.IsSuccess)
                throw new InvalidOperationException($"Bridged-Dial fehlgeschlagen: {dial.Status}");

            return new TwoLegBridgedCall(callerClient, calleeClient, dial.Call!, calleeCall);
        }
        catch
        {
            callerClient.Dispose();
            calleeClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sendet <paramref name="duration"/> lang markierte PCMU-Frames in BEIDE Richtungen (Kadenz
    /// <paramref name="frameInterval"/>, Default 20 ms) und sammelt die je Seite empfangenen Marker.
    /// Läuft lang genug, dass RTCP-Reports die Metriken befüllen (Default 8 s).
    /// </summary>
    public async Task<TwoLegMediaResult> RunBidirectionalMediaAsync(
        TimeSpan? duration = null, TimeSpan? frameInterval = null)
    {
        var runFor = duration ?? TimeSpan.FromSeconds(8);
        var interval = frameInterval ?? TimeSpan.FromMilliseconds(20);

        var calleeSeq = new List<uint>();
        var callerSeq = new List<uint>();
        var gate = new object();

        using var recvAtCallee = _calleeClient.Media.CreateReceiver();
        using var recvAtCaller = _callerClient.Media.CreateReceiver();
        recvAtCallee.FrameReceived += (_, e) =>
        {
            if (e.Frame.Payload.Length < 4) return;
            lock (gate) calleeSeq.Add(MarkedPcmuSource.ReadSequence(e.Frame.Payload.Span));
        };
        recvAtCaller.FrameReceived += (_, e) =>
        {
            if (e.Frame.Payload.Length < 4) return;
            lock (gate) callerSeq.Add(MarkedPcmuSource.ReadSequence(e.Frame.Payload.Span));
        };
        recvAtCallee.AttachToCall(CalleeCall);
        recvAtCaller.AttachToCall(CallerCall);

        using var sendFromCaller = _callerClient.Media.CreateSender();
        using var sendFromCallee = _calleeClient.Media.CreateSender();
        sendFromCaller.AttachToCall(CallerCall);
        sendFromCallee.AttachToCall(CalleeCall);

        var srcA = new MarkedPcmuSource();
        var srcB = new MarkedPcmuSource();
        using var cts = new CancellationTokenSource(runFor);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await sendFromCaller.SendAsync(srcA.Next(), cts.Token);
                await sendFromCallee.SendAsync(srcB.Next(), cts.Token);
                await Task.Delay(interval, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* Ende der Laufdauer */ }

        lock (gate)
            return new TwoLegMediaResult(calleeSeq.ToArray(), callerSeq.ToArray());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await CallerCall.HangupAsync(); } catch { /* best effort */ }
        try { await CalleeCall.HangupAsync(); } catch { /* best effort */ }
        try { _callerClient.Dispose(); }
        finally { _calleeClient.Dispose(); }
    }
}
