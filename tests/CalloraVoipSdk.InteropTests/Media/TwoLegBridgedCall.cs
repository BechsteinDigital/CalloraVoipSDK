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

/// <summary>Profil eines Zwei-Bein-Bridged-Calls: Endpunkte, Bridge-Extension, SRTP-Policy und Codec-Präferenzen.</summary>
public sealed record TwoLegProfile(
    string CallerUser, string CallerPass,
    string CalleeUser, string CalleePass,
    string BridgeExtension,
    SrtpPolicy SrtpPolicy,
    IReadOnlyList<string> CallerCodecs,
    IReadOnlyList<string> CalleeCodecs)
{
    private static readonly string[] Pcmu = { "PCMU" };

    /// <summary>Plain RTP / PCMU über die Endpunkte 6001 (Caller) und 6003 (Callee).</summary>
    public static TwoLegProfile Plain(AsteriskContainer a) =>
        new(a.Username, a.Password, a.BridgeUsername, a.BridgePassword, "6003", SrtpPolicy.Disabled, Pcmu, Pcmu);

    /// <summary>SRTP-SDES / PCMU über die Endpunkte 6002 (Caller) und 6004 (Callee).</summary>
    public static TwoLegProfile Sdes(AsteriskContainer a) =>
        new(a.SdesUsername, a.SdesPassword, a.SdesBridgeUsername, a.SdesBridgePassword, "6004", SrtpPolicy.Required, Pcmu, Pcmu);

    /// <summary>
    /// Codec-Mismatch: Caller pinnt G.722 (Endpoint 6001 kann g722), Callee ist PCMU-only (6003) →
    /// Asterisk muss zwischen den Legs transcodieren.
    /// </summary>
    public static TwoLegProfile CodecMismatch(AsteriskContainer a) =>
        new(a.Username, a.Password, a.BridgeUsername, a.BridgePassword, "6003", SrtpPolicy.Disabled, new[] { "G722" }, Pcmu);
}

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

    private static VoipClient NewClient(SrtpPolicy srtpPolicy, IReadOnlyList<string> codecs) =>
        new(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = srtpPolicy,
            PreferredAudioCodecs = codecs,
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
    public static async Task<TwoLegBridgedCall> StartAsync(AsteriskContainer asterisk, TwoLegProfile? profile = null)
    {
        var p = profile ?? TwoLegProfile.Plain(asterisk);
        var callerClient = NewClient(p.SrtpPolicy, p.CallerCodecs);
        var calleeClient = NewClient(p.SrtpPolicy, p.CalleeCodecs);
        try
        {
            var callerLine = await RegisterAsync(asterisk, callerClient, p.CallerUser, p.CallerPass);
            await RegisterAsync(asterisk, calleeClient, p.CalleeUser, p.CalleePass);

            // B nimmt den eingehenden (von Asterisk gebrückten) Call an. Der Handler erfasst nur den Call;
            // A's Dial blockiert bis zum Accept → beides läuft nebenläufig.
            var calleeTcs = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnIncoming(object? _, IncomingCallEventArgs e) => calleeTcs.TrySetResult(e.Call);
            calleeClient.IncomingCall += OnIncoming;

            var dialTask = callerClient.DialAndWaitUntilConnectedAsync(
                callerLine, asterisk.CallTargetUri(p.BridgeExtension),
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

        var srcA = new MarkedPcmuSource(CallerCall.MediaParameters!.PayloadType);
        var srcB = new MarkedPcmuSource(CalleeCall.MediaParameters!.PayloadType);
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

    /// <summary>
    /// Startet einen kontinuierlichen bidirektionalen Sende-Loop (markiertes PCMU, Default 20 ms) im
    /// Hintergrund und gibt ein Handle zurück, das den Loop beim Dispose stoppt. Für Tests, die auf
    /// zeitabhängige RTCP-Metriken (z. B. RTT über ≥2 SR/RR-Zyklen) mit Deadline pollen müssen.
    /// </summary>
    public MediaFlow StartBidirectionalMedia(TimeSpan? frameInterval = null) =>
        new(_callerClient, _calleeClient, CallerCall, CalleeCall, frameInterval ?? TimeSpan.FromMilliseconds(20));

    /// <summary>Laufendes bidirektionales Media-Handle; stoppt den Sende-Loop beim Dispose.</summary>
    public sealed class MediaFlow : IAsyncDisposable
    {
        private readonly IMediaSender _sendA;
        private readonly IMediaSender _sendB;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        internal MediaFlow(VoipClient a, VoipClient b, ICall callA, ICall callB, TimeSpan interval)
        {
            _sendA = a.Media.CreateSender();
            _sendB = b.Media.CreateSender();
            _sendA.AttachToCall(callA);
            _sendB.AttachToCall(callB);
            _loop = RunAsync(interval, _cts.Token);
        }

        private async Task RunAsync(TimeSpan interval, CancellationToken ct)
        {
            var srcA = new MarkedPcmuSource();
            var srcB = new MarkedPcmuSource();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _sendA.SendAsync(srcA.Next(), ct);
                    await _sendB.SendAsync(srcB.Next(), ct);
                    await Task.Delay(interval, ct);
                }
            }
            catch (OperationCanceledException) { /* Ende bei Dispose */ }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _loop.ConfigureAwait(false); } catch { /* best effort */ }
            _cts.Dispose();
            _sendA.Dispose();
            _sendB.Dispose();
        }
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
