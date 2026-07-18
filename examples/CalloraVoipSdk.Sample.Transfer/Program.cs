using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk;

// ══════════════════════════════════════════════════════════════════════════════
// CalloraVoipSdk — Transfer-Beispiel
//
// Baut einen Anruf A auf und demonstriert beide Transferarten:
//   • Blind-Transfer   — A wird ohne Rücksprache an ein Ziel übergeben (REFER)
//   • Attended-Transfer — erst Rückfrage-Anruf B, dann A und B verbinden
//
// Aufruf:
//   dotnet run -- <server> <user> <password> [ziel-A]
// ══════════════════════════════════════════════════════════════════════════════

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning));

string server, user, password;
string? firstTarget = null;

if (args.Length >= 3)
{
    server = args[0];
    user = args[1];
    password = args[2];
    firstTarget = args.Length >= 4 ? args[3] : null;
}
else
{
    Console.WriteLine("=== CalloraVoipSdk Transfer ===\n");
    server = Prompt("SIP-Server");
    user = Prompt("Benutzername");
    password = Prompt("Passwort");
}

using var client = new VoipClient(new VoipConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent = "CalloraVoipSdk-Transfer/1.0",
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

// ── Primären Anruf A aufbauen ─────────────────────────────────────────────────
firstTarget ??= Prompt("Ziel für den ersten Anruf (A), z.B. sip:100@pbx");
var primary = await DialAsync(firstTarget);
if (primary is null)
{
    await line.UnregisterAsync();
    return 1;
}

PrintHelp();

// ── Eingabeschleife ───────────────────────────────────────────────────────────
while (true)
{
    var input = Console.ReadLine()?.Trim();
    if (input is null || input.Equals("q", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.StartsWith("b ", StringComparison.OrdinalIgnoreCase))
    {
        var uri = input[2..].Trim();
        Console.WriteLine($"[Transfer] Blind-Transfer von A an {uri} ...");
        try
        {
            await primary.BlindTransferAsync(uri);
            Console.WriteLine("[Transfer] REFER gesendet — A wird übergeben.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fehler] Blind-Transfer fehlgeschlagen: {ex.Message}");
        }
        continue;
    }

    if (input.StartsWith("x ", StringComparison.OrdinalIgnoreCase))
    {
        var uri = input[2..].Trim();
        Console.WriteLine($"[Transfer] Rückfrage-Anruf B an {uri} ...");
        var consult = await DialAsync(uri);
        if (consult is null)
            continue;

        Console.WriteLine("[Transfer] Attended-Transfer: verbinde A und B ...");
        try
        {
            var ok = await primary.AttendedTransferAsync(consult);
            Console.WriteLine(ok
                ? "[Transfer] Attended-Transfer erfolgreich — A und B verbunden."
                : "[Transfer] Attended-Transfer wurde abgelehnt.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fehler] Attended-Transfer fehlgeschlagen: {ex.Message}");
        }
        continue;
    }

    if (input.Equals("h", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[Call] Lege A auf ...");
        await primary.HangupAsync();
        continue;
    }

    PrintHelp();
}

// ── Aufräumen ─────────────────────────────────────────────────────────────────
if (primary.State != CallState.Terminated)
    await primary.HangupAsync();
await line.UnregisterAsync();
Console.WriteLine("[Line] Abgemeldet.");
return 0;

// ── Hilfsmethoden ─────────────────────────────────────────────────────────────
async Task<ICall?> DialAsync(string target)
{
    Console.WriteLine($"[Call] Wähle {target} ...");
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
            Console.WriteLine("[Call] Verbunden.");
            return dial.Call;
        }

        Console.WriteLine($"[Fehler] Nicht verbunden: {dial.Status}.");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Fehler] Anruf fehlgeschlagen: {ex.Message}");
        return null;
    }
}

static void PrintHelp()
{
    Console.WriteLine();
    Console.WriteLine("  b <uri> = Blind-Transfer     x <uri> = Attended-Transfer (mit Rückfrage)");
    Console.WriteLine("  h = A auflegen               q = beenden");
    Console.WriteLine();
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
