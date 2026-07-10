using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk;

// ══════════════════════════════════════════════════════════════════════════════
// CalloraVoipSdk — Switchboard
//
// Vermittlungs-/Operator-Beispiel: verwaltet MEHRERE gleichzeitige Gespräche
// (inbound + outbound) und kann zwei davon miteinander verbinden — auf beiden
// Wegen:
//   • t <a> <b>  Attended-Transfer  — die PBX verbindet A und B, das SDK tritt
//                aus dem Ruf aus (REFER).
//   • b <a> <b>  Bridge via MediaConnector — das SDK bleibt im Medienpfad und
//                kreuzverbindet die Audioströme von A und B.
//
// Lokales Audio (Mikro/Lautsprecher) ist ein EINZELner Fokus: 'f <n>' legt das
// Standard-Audiogerät auf genau ein Gespräch (das SDK löst dabei das Audio der
// anderen Gespräche automatisch). Ohne 'f' läuft kein lokales Audio — die
// Vermittlung braucht das für Transfer/Bridge auch nicht.
//
// Standardmäßig leises Logging; -v/--verbose schaltet SDK-Debug-/Trace-Logs ein.
//
// Aufruf:
//   dotnet run -- [-v]
// ══════════════════════════════════════════════════════════════════════════════

var verbose = args.Any(a => a is "-v" or "--verbose");

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning));

Console.WriteLine("=== CalloraVoipSdk Switchboard ===");
Console.WriteLine(verbose ? "(verbose: SDK-Debug-Logs aktiv)\n" : "(leises Logging — SDK-Debug via -v)\n");

var server = Prompt("SIP-Server");
var user = Prompt("Benutzername");
var password = Prompt("Passwort");
var display = Prompt("Anzeigename (optional, Enter = leer)", optional: true);
Console.WriteLine();

VoipClient client;
try
{
    client = new VoipClient(new SdkConfiguration
    {
        LoggerFactory = loggerFactory,
        UserAgent = "CalloraVoipSdk-Switchboard/1.0",
    });
}
catch (Exception ex)
{
    Console.WriteLine($"[Fehler] SDK-Initialisierung fehlgeschlagen: {ex.Message}");
    return 1;
}

using var clientLifetime = client;

Console.Write($"[Line] Registriere bei {server} ...");
var connect = await client.ConnectAsync(
    new SipAccount
    {
        SipServer = server,
        Username = user,
        Password = password,
        DisplayName = display ?? string.Empty
    },
    new ConnectOptions { Timeout = TimeSpan.FromSeconds(15), FailFastOnRegistrationFailed = true });

if (!connect.IsSuccess || connect.Line is null)
{
    Console.WriteLine($"\r[Fehler] Registrierung fehlgeschlagen ({connect.Status}).          ");
    return 1;
}

var line = connect.Line;
Console.WriteLine("\r[Line] Registriert.            ");

// ── Zustand ──────────────────────────────────────────────────────────────────
var calls = new ConcurrentDictionary<int, ICall>();
var nextId = 0;
var bridges = new List<Bridge>();
var bridgeSync = new object();

line.IncomingCall += (_, e) =>
{
    var id = Register(e.Call);
    Console.WriteLine();
    Console.WriteLine($"[{id}] EINGEHEND von {e.Call.RemoteParty}   —   'a {id}' annehmen · 'r {id}' ablehnen");
};

PrintHelp();

// ── Eingabeschleife: liest immer weiter; Befehle laufen off-thread, damit eine
//    langsame Operation die Konsole nie einfriert. ─────────────────────────────
while (true)
{
    var input = Console.ReadLine()?.Trim();
    if (input is null || input.Equals("q", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.Length == 0)
    {
        PrintHelp();
        continue;
    }

    _ = Task.Run(() => ExecuteAsync(input));
}

// ── Aufräumen ─────────────────────────────────────────────────────────────────
lock (bridgeSync)
{
    foreach (var br in bridges)
        DisposeAll(br.Resources);
    bridges.Clear();
}

foreach (var entry in calls.ToArray())
{
    try { await entry.Value.HangupAsync(); }
    catch { /* best effort */ }
}

await line.UnregisterAsync();
Console.WriteLine("[Line] Abgemeldet.");
return 0;

// ── Befehlsausführung (läuft auf einem Thread-Pool-Thread) ───────────────────
async Task ExecuteAsync(string input)
{
    var tok = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var cmd = tok[0].ToLowerInvariant();

    switch (cmd)
    {
        case "l":
            ListCalls();
            break;

        case "d" when tok.Length >= 2:
            await DialAsync(tok[1]);
            break;

        case "a" when tok.Length >= 2 && TryResolve(tok[1], out var aId, out var aCall):
            await AcceptCallAsync(aId, aCall);
            break;

        case "r" when tok.Length >= 2 && TryResolve(tok[1], out var rId, out var rCall):
            await RejectCallAsync(rId, rCall);
            break;

        case "h" when tok.Length >= 2 && TryResolve(tok[1], out var hId, out var hCall):
            await HangupCallAsync(hId, hCall);
            break;

        case "f" when tok.Length >= 2 && TryResolve(tok[1], out var fId, out var fCall):
            await FocusAudioAsync(fId, fCall);
            break;

        case "t" when tok.Length >= 3
                      && TryResolve(tok[1], out var tA, out var tCallA)
                      && TryResolve(tok[2], out var tB, out var tCallB):
            await AttendedAsync(tA, tCallA, tB, tCallB);
            break;

        case "b" when tok.Length >= 3
                      && TryResolve(tok[1], out var bA, out var bCallA)
                      && TryResolve(tok[2], out var bB, out var bCallB):
            await BridgeAsync(bA, bCallA, bB, bCallB);
            break;

        default:
            Console.WriteLine("[?] Unbekannter Befehl oder unbekannte ID.");
            PrintHelp();
            break;
    }
}

// ── Registrierung + Zustandsverfolgung ───────────────────────────────────────
// Der StateChanged-Handler läuft auf dem Signaling-Thread: hier NICHTS blockieren
// und kein Audiogerät öffnen — nur Buchhaltung und Konsolenausgabe.
int Register(ICall call)
{
    var id = Interlocked.Increment(ref nextId);
    calls[id] = call;

    call.StateChanged += (_, e) =>
    {
        switch (e.NewState)
        {
            case CallState.Connected:
                Console.WriteLine($"[{id}] verbunden ({call.RemoteParty}) — 'f {id}' für Audio, 't'/'b' zum Verbinden");
                break;

            case CallState.Terminated:
                calls.TryRemove(id, out var _);
                TeardownBridgesFor(id);
                Console.WriteLine($"[{id}] beendet ({call.RemoteParty})");
                break;
        }
    };

    return id;
}

async Task DialAsync(string uri)
{
    try
    {
        var call = await line.DialAsync(uri);
        var id = Register(call);
        Console.WriteLine($"[{id}] wähle {uri} ...");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Fehler] Wählen fehlgeschlagen: {ex.Message}");
    }
}

async Task AcceptCallAsync(int id, ICall call)
{
    try
    {
        await call.AcceptAsync();
        Console.WriteLine($"[{id}] angenommen.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{id}] Annehmen nicht möglich (State={call.State}): {ex.Message}");
    }
}

async Task RejectCallAsync(int id, ICall call)
{
    var result = await call.RejectAsync();
    Console.WriteLine($"[{id}] abgelehnt ({result.Status}).");
}

async Task HangupCallAsync(int id, ICall call)
{
    try
    {
        await call.HangupAsync();
        Console.WriteLine($"[{id}] aufgelegt.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{id}] Auflegen nicht möglich (State={call.State}): {ex.Message}");
    }
}

// Lokales Audio (Mikro/Lautsprecher) auf genau EIN Gespräch legen. Das SDK löst
// dabei automatisch das Standard-Audio anderer Gespräche (Single-Focus-Gerät).
async Task FocusAudioAsync(int id, ICall call)
{
    try
    {
        await client.AttachDefaultAudioAsync(call);
        Console.WriteLine($"[{id}] Audio-Fokus aktiv — anderes Call-Audio wurde gelöst.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{id}] Audio-Fokus fehlgeschlagen: {ex.Message}");
    }
}

// Weg 1: Attended-Transfer — die PBX verbindet A und B; das SDK tritt aus dem Ruf aus.
async Task AttendedAsync(int idA, ICall a, int idB, ICall b)
{
    if (idA == idB)
    {
        Console.WriteLine("[?] Zwei verschiedene IDs nötig.");
        return;
    }

    try
    {
        var ok = await a.AttendedTransferAsync(b);
        Console.WriteLine(ok
            ? $"[{idA}] <-> [{idB}] Attended-Transfer erfolgreich — beide verbunden (SDK tritt aus)."
            : $"[{idA}] <-> [{idB}] Attended-Transfer abgelehnt.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[?] Attended-Transfer fehlgeschlagen: {ex.Message}");
    }
}

// Weg 2: MediaConnector-Bridge — das SDK bleibt im Medienpfad und kreuzverbindet die
// Audioströme. Lokales Standard-Audio wird von beiden Beinen gelöst.
async Task BridgeAsync(int idA, ICall a, int idB, ICall b)
{
    if (idA == idB)
    {
        Console.WriteLine("[?] Zwei verschiedene IDs nötig.");
        return;
    }

    if (a.State != CallState.Connected || b.State != CallState.Connected)
    {
        Console.WriteLine($"[?] Beide Gespräche müssen verbunden sein (a={a.State}, b={b.State}).");
        return;
    }

    try { await client.DetachDefaultAudioAsync(a); } catch { /* egal */ }
    try { await client.DetachDefaultAudioAsync(b); } catch { /* egal */ }

    var recvA = client.Media.CreateReceiver();
    recvA.AttachToCall(a);
    var sendA = client.Media.CreateSender();
    sendA.AttachToCall(a);
    var recvB = client.Media.CreateReceiver();
    recvB.AttachToCall(b);
    var sendB = client.Media.CreateSender();
    sendB.AttachToCall(b);

    var connector = client.Media.CreateConnector();
    var link = connector.CrossConnect(recvA, sendA, recvB, sendB);

    var resources = new IDisposable[] { link, recvA, sendA, recvB, sendB };
    lock (bridgeSync)
        bridges.Add(new Bridge(idA, idB, resources));

    Console.WriteLine($"[{idA}] <-> [{idB}] gebrückt (MediaConnector) — beide sprechen; Operator ist aus dem Medienpfad.");
}

void TeardownBridgesFor(int id)
{
    List<Bridge> affected;
    lock (bridgeSync)
    {
        affected = bridges.Where(br => br.A == id || br.B == id).ToList();
        foreach (var br in affected)
            bridges.Remove(br);
    }

    foreach (var br in affected)
    {
        DisposeAll(br.Resources);
        Console.WriteLine($"[{br.A}] <-> [{br.B}] Bridge aufgelöst.");
    }
}

static void DisposeAll(IEnumerable<IDisposable> resources)
{
    foreach (var d in resources)
    {
        try { d.Dispose(); }
        catch { /* best effort */ }
    }
}

bool TryResolve(string? arg, out int id, out ICall call)
{
    id = 0;
    call = null!;
    if (int.TryParse(arg, out id) && calls.TryGetValue(id, out var resolved))
    {
        call = resolved;
        return true;
    }
    return false;
}

void ListCalls()
{
    var snapshot = calls.ToArray();
    if (snapshot.Length == 0)
    {
        Console.WriteLine("  (keine aktiven Gespräche)");
    }
    else
    {
        Console.WriteLine("  ── Gespräche ──");
        foreach (var entry in snapshot.OrderBy(k => k.Key))
        {
            var c = entry.Value;
            var dir = c.Direction == CallDirection.Inbound ? "ein" : "aus";
            Console.WriteLine($"  [{entry.Key}] {dir}  {c.RemoteParty,-28}  {c.State}");
        }
    }

    Bridge[] active;
    lock (bridgeSync)
        active = bridges.ToArray();
    foreach (var br in active)
        Console.WriteLine($"  Bridge: [{br.A}] <-> [{br.B}]");
}

void PrintHelp()
{
    Console.WriteLine();
    Console.WriteLine("  d <uri>   = wählen (outbound)      a <n>     = annehmen");
    Console.WriteLine("  r <n>     = ablehnen               h <n>     = auflegen");
    Console.WriteLine("  f <n>     = lokales Audio-Fokus    l         = Liste");
    Console.WriteLine("  t <a> <b> = Attended-Transfer      b <a> <b> = Bridge (MediaConnector)");
    Console.WriteLine("  q         = beenden");
    Console.WriteLine("  (t: PBX verbindet, SDK tritt aus  ·  b: SDK bleibt im Medienpfad)");
    Console.WriteLine();
}

static string Prompt(string label, bool optional = false)
{
    while (true)
    {
        Console.Write($"{label}: ");
        var value = Console.ReadLine()?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(value) || optional)
            return value;
        Console.WriteLine("  Bitte einen Wert eingeben.");
    }
}

// Aktive MediaConnector-Bridge zwischen zwei Gesprächen samt Ressourcen.
record Bridge(int A, int B, IReadOnlyList<IDisposable> Resources);
