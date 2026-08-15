using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Registration;

/// <summary>
/// #104 — registrierungsloser, IP-authentifizierter Trunk gegen echten Asterisk. Der Endpoint
/// <c>trunk-ip</c> hat weder <c>auth=</c> noch <c>aors=</c>; Asterisk ordnet Requests allein über
/// <c>type=identify</c> der Quell-IP zu — der Static-IP-Trunk, wie ihn ein Provider betreibt.
///
/// <para>
/// Bewiesen wird damit das, was vorher nicht ging: eine Line, die <b>nie</b> REGISTER sendet, erreicht
/// <see cref="LineState.Ready"/> und telefoniert. Ein Registrierungsversuch wäre hier nicht nur unnötig,
/// sondern falsch — der Endpoint hat keine Credentials, gegen die er authentifizieren könnte.
/// </para>
///
/// Media: Plain RTP (<see cref="SrtpPolicy.Disabled"/>), wie bei den anderen Asterisk-Call-Tests, da der
/// Endpoint kein <c>media_encryption</c> führt (Audit-Fund F007).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskIpAuthTrunkInteropTests
{
    private static VoipClient NewClient(int localSipPort) =>
        new(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Disabled,
            LocalSipPort = localSipPort,
        });

    private static SipAccount TrunkAccount(AsteriskContainer asterisk) => new()
    {
        SipServer = asterisk.ContainerIpAddress,
        Port = 5060,
        // Der User-part der AOR; beim IP-Trunk trägt er typischerweise die Hauptnummer und dient nicht
        // der Authentifizierung — es gibt keine.
        Username = "trunk",
        Transport = DomainSipTransport.Udp,
        Register = false,
    };

    [DockerRequiredFact]
    public async Task RegistrationFreeTrunk_ReachesReadyWithoutSendingRegister()
    {
        await using var asterisk = new AsteriskContainer(withIpAuthTrunk: true);
        await asterisk.StartAsync();
        using var client = NewClient(FreeUdpPort());

        var connect = await client.ConnectAsync(
            TrunkAccount(asterisk),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(connect.IsSuccess, $"Connect fehlgeschlagen: Status={connect.Status}");
        Assert.Equal(LineState.Ready, connect.Line!.State);

        // Gegenprobe am Peer: Asterisk kennt für diesen Endpoint keinen Contact, weil nie registriert
        // wurde. Stünde hier eine Registrierung, wäre der ganze Modus nicht bewiesen.
        var contacts = await asterisk.ExecAsync("asterisk", "-rx", "pjsip show contacts");
        Assert.DoesNotContain("trunk-ip", contacts, StringComparison.OrdinalIgnoreCase);
    }

    [DockerRequiredFact]
    public async Task RegistrationFreeTrunk_PlacesAnOutboundCallWithMedia()
    {
        await using var asterisk = new AsteriskContainer(withIpAuthTrunk: true);
        await asterisk.StartAsync();
        using var client = NewClient(FreeUdpPort());

        var connect = await client.ConnectAsync(
            TrunkAccount(asterisk),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(connect.IsSuccess, $"Connect fehlgeschlagen: Status={connect.Status}");

        var result = await client.DialAndWaitUntilConnectedAsync(
            connect.Line!,
            asterisk.CallTargetUri("answer"),
            new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(15) });

        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;
        Assert.Equal(CallState.Connected, call.State);
        Assert.NotNull(call.MediaParameters);

        // Der Dialog steht — jetzt fließt Media. Ohne diese Prüfung wäre nur die Signalisierung belegt.
        uint received = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (call.RtpStatistics is { PacketsReceived: > 0 } rtp) { received = rtp.PacketsReceived; break; }
            await Task.Delay(250);
        }

        Assert.True(received > 0, "Kein RTP vom Trunk-Peer empfangen.");
        await call.HangupAsync();
    }

    [DockerRequiredFact]
    public async Task RegistrationFreeTrunk_ReceivesAnInboundCallOnTheConfiguredPort()
    {
        // Die zweite Hälfte des Features und der eigentliche Grund für den festen Port: ohne REGISTER
        // sagt dem Provider niemand, wohin er zustellen soll. Er kennt die Adresse aus dem Vertrag —
        // hier nachgestellt, indem Asterisk direkt an <host>:<port> originiert. Mit einem ephemeren Port
        // wäre dieser Test nicht formulierbar, weil die Adresse bei jedem Start eine andere wäre.
        var port = FreeUdpPort();
        await using var asterisk = new AsteriskContainer(withIpAuthTrunk: true);
        await asterisk.StartAsync();
        using var client = NewClient(port);

        var incoming = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.IncomingCall += (_, e) => incoming.TrySetResult(e.Call);

        var connect = await client.ConnectAsync(
            TrunkAccount(asterisk),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(connect.IsSuccess, $"Connect fehlgeschlagen: Status={connect.Status}");

        var host = await HostAddressAsSeenFromContainerAsync(asterisk);
        var originate = await asterisk.ExecAsync(
            "asterisk", "-rx",
            $"channel originate PJSIP/trunk-ip/sip:trunk@{host}:{port} application Milliwatt");

        var call = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(CallDirection.Inbound, call.Direction);

        await call.AcceptAsync();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (call.State != CallState.Connected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(CallState.Connected, call.State);

        uint received = 0;
        deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (call.RtpStatistics is { PacketsReceived: > 0 } rtp) { received = rtp.PacketsReceived; break; }
            await Task.Delay(250);
        }

        Assert.True(received > 0, $"Kein RTP im eingehenden Trunk-Call empfangen. originate: {originate}");
        await call.HangupAsync();
    }

    [DockerRequiredFact]
    public async Task TrunkWithoutAnAccountUser_PlacesAnOutboundCall()
    {
        // Der eigentliche Static-IP-Fall: gar kein Account-User. From/Contact werden dann host-only
        // ("sip:host", RFC 3261 §19.1.1) statt "sip:@host" — dass ein echter Peer das annimmt, lässt sich
        // nur so zeigen. InboundNumbers ist Pflicht, weil ohne User-part die 1:1-Zuordnung entfällt.
        await using var asterisk = new AsteriskContainer(withIpAuthTrunk: true);
        await asterisk.StartAsync();
        using var client = NewClient(FreeUdpPort());

        var connect = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Transport = DomainSipTransport.Udp,
                Register = false,
                InboundNumbers = ["4930123456"],
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(connect.IsSuccess, $"Connect fehlgeschlagen: Status={connect.Status}");
        Assert.Equal(LineState.Ready, connect.Line!.State);

        var result = await client.DialAndWaitUntilConnectedAsync(
            connect.Line!,
            asterisk.CallTargetUri("answer"),
            new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(15) });

        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        Assert.Equal(CallState.Connected, result.Call!.State);
        await result.Call.HangupAsync();
    }

    /// <summary>
    /// Adresse, unter der der Container die Testmaschine erreicht — das Default-Gateway seines
    /// Bridge-Netzes. Aus <c>/proc/net/route</c>, weil das Asterisk-Image kein <c>ip</c> mitbringt: die
    /// Zeile mit Ziel <c>00000000</c> trägt das Gateway als Little-Endian-Hex.
    /// </summary>
    private static async Task<string> HostAddressAsSeenFromContainerAsync(AsteriskContainer asterisk)
    {
        var routes = await asterisk.ExecAsync("cat", "/proc/net/route");
        foreach (var line in routes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[1] != "00000000")
                continue;

            var raw = uint.Parse(fields[2], System.Globalization.NumberStyles.HexNumber);
            return new System.Net.IPAddress(BitConverter.GetBytes(raw)).ToString();
        }

        throw new InvalidOperationException($"Kein Default-Gateway in /proc/net/route gefunden:\n{routes}");
    }

    /// <summary>Reserviert einen freien UDP-Port und gibt ihn wieder frei.</summary>
    private static int FreeUdpPort()
    {
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
