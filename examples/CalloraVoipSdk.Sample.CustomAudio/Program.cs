using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk;

// ══════════════════════════════════════════════════════════════════════════════
// CalloraVoipSdk — CustomAudio-Beispiel (Media-Tap)
//
// Zeigt den frame-basierten Medienpfad ohne Audio-Hardware:
//   • IMediaReceiver — zählt/misst eingehende Frames (codec-unabhängig)
//   • IMediaSender   — injiziert einen selbst erzeugten 440-Hz-Ton (PCMU)
//
// Kein AttachDefaultAudioAsync — reiner Tap-Pfad (Bot-/Headless-Szenario).
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
    Console.WriteLine("=== CalloraVoipSdk CustomAudio (Media-Tap) ===\n");
    server = Prompt("SIP-Server");
    user = Prompt("Benutzername");
    password = Prompt("Passwort");
    target = Prompt("Ziel, z.B. sip:100@pbx");
}

using var client = new VoipClient(new VoipConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent = "CalloraVoipSdk-CustomAudio/1.0",
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

// ── Eingehende Frames zählen ──────────────────────────────────────────────────
long frameCount = 0;
long byteCount = 0;
var lastPayloadType = -1;

using var receiver = client.Media.CreateReceiver();
receiver.FrameReceived += (_, e) =>
{
    // Läuft synchron auf dem Medienpfad: nur zählen, nichts blockieren.
    Interlocked.Increment(ref frameCount);
    Interlocked.Add(ref byteCount, e.Frame.Payload.Length);
    Volatile.Write(ref lastPayloadType, e.Frame.PayloadType);
};
receiver.AttachToCall(call);

// ── Ausgehenden Ton injizieren (nur wenn PCMU verhandelt wurde) ────────────────
using var sender = client.Media.CreateSender();
sender.AttachToCall(call);

var mp = call.MediaParameters;
using var toneCts = new CancellationTokenSource();
Task toneTask = Task.CompletedTask;

if (mp is not null && mp.PayloadType == 0) // 0 = PCMU
{
    Console.WriteLine($"[Tap] Sende 440-Hz-PCMU-Ton (clock={mp.ClockRate} Hz, {mp.SamplesPerPacket} Samples/Paket).");
    toneTask = ToneLoopAsync(sender, mp.ClockRate, mp.SamplesPerPacket, toneCts.Token);
}
else
{
    Console.WriteLine($"[Tap] Ton-Generator unterstützt nur PCMU; verhandelt wurde " +
                      $"'{mp?.CodecName}' (PT {mp?.PayloadType}) — Ton wird übersprungen.");
}

// ── Statistik für 15 s ausgeben, dann auflegen ────────────────────────────────
for (var second = 0; second < 15 && call.State != CallState.Terminated; second++)
{
    await Task.Delay(TimeSpan.FromSeconds(1));
    Console.WriteLine($"[Tap] eingehend: {Interlocked.Read(ref frameCount)} Frames, " +
                      $"{Interlocked.Read(ref byteCount)} Bytes, letzter PT={Volatile.Read(ref lastPayloadType)}");
}

toneCts.Cancel();
try { await toneTask; } catch (OperationCanceledException) { /* erwartet */ }

receiver.Detach();
sender.Detach();

if (call.State != CallState.Terminated)
    await call.HangupAsync();
await line.UnregisterAsync();
Console.WriteLine("[Line] Abgemeldet.");
return 0;

// ── Ton-Erzeugung ─────────────────────────────────────────────────────────────
static async Task ToneLoopAsync(IMediaSender sender, int clockRate, int samplesPerPacket, CancellationToken ct)
{
    var sampleRate = clockRate > 0 ? clockRate : 8000;
    var samples = samplesPerPacket > 0 ? samplesPerPacket : 160;
    var increment = 2.0 * Math.PI * 440.0 / sampleRate;
    var phase = 0.0;

    while (!ct.IsCancellationRequested)
    {
        var payload = new byte[samples];
        for (var n = 0; n < samples; n++)
        {
            var s = (short)(Math.Sin(phase) * 8000.0); // ~ -12 dBFS
            payload[n] = LinearToMuLaw(s);
            phase += increment;
            if (phase > 2.0 * Math.PI)
                phase -= 2.0 * Math.PI;
        }

        try
        {
            await sender.SendAsync(new MediaFrame(payload, 0, (uint)samples), ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception)
        {
            break; // Call weg o.ä. — Ton-Schleife beenden
        }

        await Task.Delay(20, ct); // ~20 ms Paketrate
    }
}

// G.711 µ-law-Kodierung eines 16-Bit-PCM-Samples.
static byte LinearToMuLaw(short pcm16)
{
    const int bias = 0x84;
    const int clip = 32635;

    int sample = pcm16;
    var sign = (sample >> 8) & 0x80;
    if (sign != 0)
        sample = -sample;
    if (sample > clip)
        sample = clip;
    sample += bias;

    var exponent = 7;
    for (var mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1)
        exponent--;

    var mantissa = (sample >> (exponent + 3)) & 0x0F;
    return (byte)~(sign | (exponent << 4) | mantissa);
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
