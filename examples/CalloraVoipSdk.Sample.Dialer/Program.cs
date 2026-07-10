using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk;

// ══════════════════════════════════════════════════════════════════════════════
// CalloraVoipSdk — Dialer-Beispiel
//
// Sequenzieller Kampagnen-Dialer: registriert eine Line und ruft eine Liste von
// Zielen nacheinander an. Jeder Anruf wird bis zum Verbindungsaufbau abgewartet,
// kurz gehalten und wieder aufgelegt; am Ende gibt es eine Ergebnisübersicht.
//
// Aufruf:
//   dotnet run -- <server> <user> <password> <ziel1> [ziel2 ...]
// oder ohne Argumente interaktiv (Zugangsdaten + Ziele werden abgefragt).
// ══════════════════════════════════════════════════════════════════════════════

// Wie lange jeder verbundene Anruf gehalten wird, bevor aufgelegt wird.
var callHold = TimeSpan.FromSeconds(5);

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning)); // SDK-Logs leise halten, eigene Statuszeilen unten

string server, user, password;
List<string> targets;

if (args.Length >= 4)
{
    server = args[0];
    user = args[1];
    password = args[2];
    targets = args[3..].ToList();
}
else
{
    Console.WriteLine("=== CalloraVoipSdk Dialer ===\n");
    server = Prompt("SIP-Server");
    user = Prompt("Benutzername");
    password = Prompt("Passwort");
    var list = Prompt("Ziele (durch Leerzeichen getrennt), z.B. sip:100@pbx sip:101@pbx");
    targets = list.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

if (targets.Count == 0)
{
    Console.WriteLine("[Fehler] Keine Zielrufnummern angegeben.");
    return 1;
}

using var client = new VoipClient(new SdkConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent = "CalloraVoipSdk-Dialer/1.0",
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

// ── Kampagne ──────────────────────────────────────────────────────────────────
var results = new List<(string Target, bool Success, string Detail)>();

for (var i = 0; i < targets.Count; i++)
{
    var target = targets[i];
    Console.WriteLine($"[{i + 1}/{targets.Count}] Wähle {target} ...");

    try
    {
        var dial = await client.DialAndWaitUntilConnectedAsync(
            line,
            target,
            new DialWaitOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                HangupOnTimeout = true,
                HangupOnCancellation = true
            });

        if (dial.IsSuccess && dial.Call is not null)
        {
            await client.AttachDefaultAudioAsync(dial.Call);
            Console.WriteLine($"        verbunden — halte {callHold.TotalSeconds:0}s ...");
            await Task.Delay(callHold);
            await dial.Call.HangupAsync();
            results.Add((target, true, "verbunden"));
        }
        else
        {
            results.Add((target, false, dial.Status.ToString()));
            Console.WriteLine($"        nicht verbunden: {dial.Status}");
        }
    }
    catch (Exception ex)
    {
        results.Add((target, false, ex.Message));
        Console.WriteLine($"        Fehler: {ex.Message}");
    }

    if (i < targets.Count - 1)
        await Task.Delay(TimeSpan.FromSeconds(1)); // kurze Pause zwischen Anrufen
}

// ── Übersicht ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n── Ergebnis ──");
foreach (var (target, success, detail) in results)
    Console.WriteLine($"  {(success ? "OK  " : "FAIL")}  {target}  ({detail})");

var ok = results.Count(r => r.Success);
Console.WriteLine($"  → {ok}/{results.Count} verbunden.");

await line.UnregisterAsync();
Console.WriteLine("[Line] Abgemeldet.");
return ok == results.Count ? 0 : 2;

// ── Hilfsmethoden ─────────────────────────────────────────────────────────────
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
