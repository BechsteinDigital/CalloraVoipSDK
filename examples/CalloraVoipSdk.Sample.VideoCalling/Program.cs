using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk;

// ══════════════════════════════════════════════════════════════════════════════
// CalloraVoipSdk — VideoCalling-Beispiel (öffentliche Video-API, transport-only)
//
// Zeigt, wie wenig Code ein Video-Call über die öffentliche API kostet:
//   • IVideoReceiver — beobachtet eingehende (bereits encodierte) Video-Frames
//   • IVideoSender   — schickt encodierte Video-Frames in den Call
//   • RecommendedBitrateChanged — der DX-Payoff: EINE Zeile verbindet die
//     fertige SDK-Bitrate-Empfehlung mit dem eigenen Encoder.
//
// WICHTIG — das SDK ist transport-only:
//   Das SDK encodiert/decodiert NIE. Es bewegt fertige Codec-Bytes (VP8/H.264/…)
//   und liefert eine fertige empfohlene Bitrate + grobe Netzqualität. Kamera,
//   Encoder und Decoder bringt die App mit. Der `StubVideoEncoder` unten ist ein
//   Platzhalter, der genau diese Stelle markiert.
//
// Hinweis: Solange die Gegenstelle keinen Video-Pfad aushandelt, verwirft der
//   Sender die Frames still (das ist beabsichtigt) — das Beispiel demonstriert
//   die API-Verdrahtung, nicht einen konkreten Video-Codec.
//
// Aufruf:
//   dotnet run -- <server> <user> <password> <ziel>
// ══════════════════════════════════════════════════════════════════════════════

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning));

string server, user, password, target;

if (args.Length >= 4)
{
    server = args[0]; user = args[1]; password = args[2]; target = args[3];
}
else
{
    Console.WriteLine("=== CalloraVoipSdk VideoCalling (öffentliche Video-API) ===\n");
    server = Prompt("SIP-Server");
    user = Prompt("Benutzername");
    password = Prompt("Passwort");
    target = Prompt("Ziel, z.B. sip:100@pbx");
}

using var client = new VoipClient(new SdkConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent = "CalloraVoipSdk-VideoCalling/1.0",
});

Console.Write($"[Line] Registriere bei {server} ...");
var connect = await client.ConnectAsync(
    new SipAccount { SipServer = server, Username = user, Password = password },
    new ConnectOptions { Timeout = TimeSpan.FromSeconds(15), FailFastOnRegistrationFailed = true });

if (!connect.IsSuccess || connect.Line is null)
{
    Console.WriteLine($"\r[Fehler] Registrierung fehlgeschlagen ({connect.Status}).          ");
    return 1;
}

var line = connect.Line;
Console.WriteLine("\r[Line] Registriert.            \n");

Console.WriteLine($"[Call] Wähle {target} ...");
var dial = await client.DialAndWaitUntilConnectedAsync(
    line,
    target,
    new DialWaitOptions
    {
        ConnectTimeout = TimeSpan.FromSeconds(30),
        HangupOnTimeout = true,
        HangupOnCancellation = true
    });

if (!dial.IsSuccess || dial.Call is null)
{
    Console.WriteLine($"[Fehler] Nicht verbunden: {dial.Status}.");
    await line.UnregisterAsync();
    return 1;
}

var call = dial.Call;
Console.WriteLine("[Call] Verbunden.\n");

// ── Eingehende Video-Frames beobachten ────────────────────────────────────────
long videoFrames = 0, videoBytes = 0, keyFrames = 0;

using var receiver = client.Media.CreateVideoReceiver();
receiver.FrameReceived += (_, e) =>
{
    // Läuft synchron auf dem Medienpfad: nur zählen/puffern, niemals blockieren.
    // Echte App: hier die encodierten Bytes an den eigenen Decoder + Anzeige geben.
    Interlocked.Increment(ref videoFrames);
    Interlocked.Add(ref videoBytes, e.Frame.Payload.Length);
    if (e.Frame.IsKeyFrame)
        Interlocked.Increment(ref keyFrames);
};
receiver.AttachToCall(call);

// ── Ausgehende Video-Frames + adaptive Bitrate ────────────────────────────────
var encoder = new StubVideoEncoder();

using var sender = client.Media.CreateVideoSender();
sender.AttachToCall(call);

// ★ DER DX-PAYOFF: eine Zeile koppelt die SDK-Empfehlung an den Encoder.
sender.RecommendedBitrateChanged += (_, e) =>
{
    if (e.RecommendedBitrateBps is long bps)
        encoder.SetBitrate(bps);
};

// Falls schon eine Empfehlung vorliegt, direkt übernehmen.
if (sender.RecommendedBitrateBps is long initial)
    encoder.SetBitrate(initial);

using var sendCts = new CancellationTokenSource();
var sendTask = VideoSendLoopAsync(sender, encoder, sendCts.Token);

// ── Statistik für 15 s ausgeben, dann auflegen ────────────────────────────────
for (var second = 0; second < 15 && call.State != CallState.Terminated; second++)
{
    await Task.Delay(TimeSpan.FromSeconds(1));
    var quality = sender.NetworkQuality?.ToString() ?? "n/a";
    var recommended = sender.RecommendedBitrateBps is long r ? $"{r / 1000} kbps" : "n/a";
    Console.WriteLine(
        $"[Video] eingehend: {Interlocked.Read(ref videoFrames)} Frames " +
        $"({Interlocked.Read(ref keyFrames)} Keyframes, {Interlocked.Read(ref videoBytes)} Bytes) | " +
        $"Empfehlung: {recommended}, Qualität: {quality} | " +
        $"Encoder läuft mit {encoder.CurrentBitrateBps / 1000} kbps");
}

sendCts.Cancel();
try { await sendTask; } catch (OperationCanceledException) { /* erwartet */ }

receiver.Detach();
sender.Detach();

if (call.State != CallState.Terminated)
    await call.HangupAsync();
await line.UnregisterAsync();
Console.WriteLine("[Line] Abgemeldet.");
return 0;

// ── Sende-Schleife (~30 fps) ──────────────────────────────────────────────────
static async Task VideoSendLoopAsync(IVideoSender sender, StubVideoEncoder encoder, CancellationToken ct)
{
    var frameIndex = 0;
    while (!ct.IsCancellationRequested)
    {
        // Alle ~1 s ein Keyframe — in der Realität steuert das der echte Encoder
        // (u. a. als Antwort auf KeyFrameRequested).
        var keyFrame = frameIndex % 30 == 0;
        var frame = encoder.EncodeNext(keyFrame);

        try
        {
            await sender.SendAsync(frame, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception)
        {
            break; // Call weg o. ä. — Sende-Schleife beenden
        }

        frameIndex++;
        await Task.Delay(33, ct); // ~30 fps
    }
}

static string Prompt(string label)
{
    while (true)
    {
        Console.Write($"{label}: ");
        var value = Console.ReadLine()?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(value))
            return value;
        Console.WriteLine("  Bitte einen Wert eingeben.");
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// Platzhalter für einen echten Video-Encoder (VP8/H.264/…).
//
// Das SDK encodiert NIE — diese Klasse steht für den Encoder, den die App
// mitbringt. Sie zeigt nur die zwei relevanten Berührungspunkte:
//   • SetBitrate(...)  ← von der SDK-Empfehlung getrieben
//   • EncodeNext(...)  → liefert die encodierten Bytes einer Frame-Quelle
// Der „Payload" hier ist bedeutungsloser Platzhalter, kein gültiger Video-Bitstrom.
// ══════════════════════════════════════════════════════════════════════════════
sealed class StubVideoEncoder
{
    private const int FramesPerSecond = 30;
    private const int VideoClockRate = 90_000;               // RTP-Videouhr
    private const int MaxSampleBytes = 4096;                 // Beispiel-Deckel

    private long _bitrateBps = 1_000_000;                    // Startwert, bis die Empfehlung greift
    private uint _rtpTimestamp;

    public long CurrentBitrateBps => Volatile.Read(ref _bitrateBps);

    public void SetBitrate(long bitrateBps) => Volatile.Write(ref _bitrateBps, Math.Max(0, bitrateBps));

    public VideoFrame EncodeNext(bool keyFrame)
    {
        _rtpTimestamp += VideoClockRate / FramesPerSecond;   // 3000 Ticks/Frame bei 30 fps

        var bytesPerFrame = Math.Clamp((int)(CurrentBitrateBps / 8 / FramesPerSecond), 1, MaxSampleBytes);
        var payload = new byte[bytesPerFrame];               // realer Encoder: echte Codec-Bytes

        // PayloadType 96 ist hier nur ein Beispielwert — real kommt die verhandelte
        // Video-Payload-Type aus der Call-Aushandlung.
        return new VideoFrame(payload, PayloadType: 96, _rtpTimestamp, keyFrame);
    }
}
