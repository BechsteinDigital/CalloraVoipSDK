using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk;

// ── Logging (leise; -v/--verbose für SDK-Debug/Trace) ────────────────────────
var verbose = args.Any(a => a is "-v" or "--verbose");
using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning));

// ── SIP-Zugangsdaten abfragen ─────────────────────────────────────────────────
Console.WriteLine("=== CalloraVoipSdk Demo ===");
Console.WriteLine();

var server = Prompt("SIP-Server (z.B. sip.example.com)");
var user = Prompt("Benutzername");
var password = PromptPassword("Passwort");
var display = Prompt("Anzeigename (optional, Enter = leer)", optional: true);

Console.WriteLine();

// ── SDK aufbauen ──────────────────────────────────────────────────────────────
VoipClient client;
try
{
    client = new VoipClient(new SdkConfiguration
    {
        LoggerFactory = loggerFactory,
        UserAgent = "CalloraVoipSdk-Demo/1.0",
    });
}
catch (Exception ex)
{
    Console.WriteLine($"[Fehler] SDK-Initialisierung fehlgeschlagen: {ex.Message}");
    return 1;
}

using var clientLifetime = client;

var account = new SipAccount
{
    SipServer = server,
    Username = user,
    Password = password,
    DisplayName = display ?? string.Empty,
};

// ── Registrierung (Convenience) ──────────────────────────────────────────────
Console.Write($"[Line] Registriere bei {server}...");
var connectResult = await client.ConnectAsync(
    account,
    new ConnectOptions
    {
        Timeout = TimeSpan.FromSeconds(15),
        FailFastOnRegistrationFailed = true
    });

if (!connectResult.IsSuccess || connectResult.Line is null)
{
    Console.WriteLine($"\r[Fehler] Registrierung fehlgeschlagen ({connectResult.Status}, State={connectResult.FinalLineState}).");
    if (connectResult.Error is not null)
        Console.WriteLine($"[Fehler] Ursache: {connectResult.Error.Message}");
    return 1;
}

var line = connectResult.Line;
Console.WriteLine("\r[Line] Registered          ");
Console.WriteLine("[Audio] Verwende SDK Default-Audio-Routing (AttachDefaultAudioAsync).\n");

// ── Zustand ───────────────────────────────────────────────────────────────────
ICall? activeCall = null;
ICall? pendingInbound = null;
var appCts = new CancellationTokenSource();

line.StateChanged += (_, e) =>
{
    Console.WriteLine($"[Line] {e.OldState} → {e.NewState}");
};

// ── Call-Handler ──────────────────────────────────────────────────────────────
client.Calls.CallAdded += (_, e) =>
{
    e.Call.StateChanged += (_, se) =>
    {
        Console.WriteLine($"[Call] {se.OldState} → {se.NewState}");

        if (se.NewState == CallState.Connected)
        {
            activeCall = e.Call;
            _ = TryAttachDefaultAudioAsync(e.Call);
            PrintHelp();
        }

        if (se.NewState == CallState.Terminated)
        {
            if (ReferenceEquals(activeCall, e.Call))
                activeCall = null;

            if (ReferenceEquals(pendingInbound, e.Call))
                pendingInbound = null;

            Console.WriteLine("[Call] Gespräch beendet.");
            PrintHelp();
        }
    };
};

// ── Eingehende Anrufe ─────────────────────────────────────────────────────────
line.IncomingCall += (_, e) =>
{
    if (activeCall is not null || pendingInbound is not null)
    {
        Console.WriteLine($"[Eingehend] Von: {e.Call.RemoteParty} — abgelehnt (besetzt).");
        _ = e.Call.HangupAsync();
        return;
    }

    pendingInbound = e.Call;
    Console.WriteLine();
    Console.WriteLine($"[Eingehend] Von: {e.Call.RemoteParty}");
    Console.WriteLine("  a = annehmen   r = ablehnen");
};

// ── Hilfeanzeige und Eingabeschleife ─────────────────────────────────────────
PrintHelp();
await InputLoopAsync(appCts.Token);

// ── Aufräumen ─────────────────────────────────────────────────────────────────
if (activeCall?.State != CallState.Terminated)
    await (activeCall?.HangupAsync() ?? Task.CompletedTask);

if (pendingInbound?.State != CallState.Terminated)
    await (pendingInbound?.HangupAsync() ?? Task.CompletedTask);

await line.UnregisterAsync();
Console.WriteLine("[Line] Abgemeldet.");
return 0;

// ── Eingabeschleife ───────────────────────────────────────────────────────────
async Task InputLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        string? input;
        try
        {
            input = await Task.Run(Console.ReadLine, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        if (input is null)
            break;

        var trimmed = input.Trim();

        // Beenden
        if (trimmed.Equals("q", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            appCts.Cancel();
            break;
        }

        // Eingehenden Anruf annehmen
        if (trimmed.Equals("a", StringComparison.OrdinalIgnoreCase) && pendingInbound is { } toAnswer)
        {
            Console.WriteLine("[Call] Nehme an...");
            try { await toAnswer.AcceptAsync(); }
            catch (Exception ex) { Console.WriteLine($"[Fehler] Annehmen fehlgeschlagen: {ex.Message}"); }
            pendingInbound = null;
            continue;
        }

        // Eingehenden Anruf ablehnen
        if (trimmed.Equals("r", StringComparison.OrdinalIgnoreCase) && pendingInbound is { } toReject)
        {
            Console.WriteLine("[Call] Lehne ab...");
            await toReject.HangupAsync();
            pendingInbound = null;
            continue;
        }

        // Aktiven Anruf auflegen (leere Eingabe oder 'h')
        if ((trimmed == string.Empty || trimmed.Equals("h", StringComparison.OrdinalIgnoreCase))
            && activeCall is { } toHangup)
        {
            Console.WriteLine("[Call] Lege auf...");
            await toHangup.HangupAsync();
            continue;
        }

        // Wählen: "d <nummer>"
        if (trimmed.StartsWith("d ", StringComparison.OrdinalIgnoreCase) && activeCall is null && pendingInbound is null)
        {
            var target = trimmed[2..].Trim();
            if (string.IsNullOrEmpty(target))
            {
                Console.WriteLine("[Fehler] Bitte Zielrufnummer angeben, z.B.: d sip:100@192.168.1.1");
                continue;
            }

            Console.WriteLine($"[Call] Wähle {target} ...");
            try
            {
                var dialResult = await client.DialAndWaitUntilConnectedAsync(
                    line,
                    target,
                    new DialWaitOptions
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(30),
                        HangupOnTimeout = true,
                        HangupOnCancellation = true
                    },
                    ct);

                if (!dialResult.IsSuccess)
                {
                    Console.WriteLine($"[Fehler] Dial nicht erfolgreich: {dialResult.Status} (State={dialResult.FinalCallState}).");
                    if (dialResult.Error is not null)
                        Console.WriteLine($"[Fehler] Ursache: {dialResult.Error.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fehler] Anruf konnte nicht gestartet werden: {ex.Message}");
            }
            continue;
        }

        PrintHelp();
    }
}

async Task TryAttachDefaultAudioAsync(ICall call)
{
    try
    {
        await client.AttachDefaultAudioAsync(call);
        Console.WriteLine("[Audio] Standard-Audio aktiv.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Audio] Attach fehlgeschlagen: {ex.Message}");
    }
}

void PrintHelp()
{
    Console.WriteLine();
    if (pendingInbound is not null)
    {
        Console.WriteLine($"  [Eingehend] Von: {pendingInbound.RemoteParty}");
        Console.WriteLine("  a = annehmen   r = ablehnen   q = beenden");
    }
    else if (activeCall is not null)
    {
        Console.WriteLine($"  [Aktiv] {activeCall.RemoteParty}  ({activeCall.State})");
        Console.WriteLine("  Enter/h = auflegen   q = beenden");
    }
    else
    {
        Console.WriteLine("  Warte auf eingehenden Anruf oder:");
        Console.WriteLine("  d <nummer> = wählen   q = beenden");
    }
    Console.WriteLine();
}

// ── Hilfsmethoden ─────────────────────────────────────────────────────────────
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

static string PromptPassword(string label)
{
    Console.Write($"{label}: ");
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? string.Empty;
    }

    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;

        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
                Console.Write("\b \b");
            }
        }
        else
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }

    Console.WriteLine();
    return sb.ToString();
}
