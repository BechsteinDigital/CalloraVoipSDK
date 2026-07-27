using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Turn;

/// <summary>
/// End-to-end-Beweis, dass der SDK-TURN-Client gegen einen ECHTEN coturn-Server (Docker) das reale
/// TURN-Wire-Protokoll spricht — nicht nur gegen den in-process-<c>TurnServer</c>-Fake, den alle anderen
/// TURN-Relay-Tests verwenden.
/// <list type="bullet">
/// <item><b>Test A (Basis):</b> Der SDK-Client allokiert authentifiziert (Long-Term-Credentials,
/// 401-Challenge → MESSAGE-INTEGRITY, RFC 5389 §10.2 / RFC 8656 §7) eine Relay-Adresse und erhält eine
/// gültige XOR-RELAYED-ADDRESS von coturn.</item>
/// <item><b>Test B (Composite / Answerer-Relay F2):</b> Zwei Allocations (Answerer + Offerer). Der Answerer
/// installiert proaktiv eine TURN-Permission (RFC 8656 §9) für die Offerer-IP; der Offerer sendet ein
/// Datagramm über coturn an die Relay-Adresse des Answerers; der Answerer empfängt es als Data-Indication
/// und demuxt es. Belegt den Inbound-Relay-Empfangspfad (replyVia-Voraussetzung) end-to-end gegen echten
/// coturn — die Answerer-Relay-Slices, real validiert.</item>
/// </list>
/// </summary>
[Trait("Category", "Interop")]
public sealed class CoturnRelayE2eTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private static StunCredentials Credentials() => new()
    {
        Username = CoturnContainer.TurnUsername,
        Password = CoturnContainer.TurnPassword,
        Realm = CoturnContainer.TurnRealm,
    };

    /// <summary>
    /// Test A: Der SDK-TURN-Client allokiert gegen echten coturn und erhält eine geroutete Relay-Adresse.
    /// Beweist den authentifizierten Allocate-Kernpfad (Allocate → 401 → MESSAGE-INTEGRITY →
    /// XOR-RELAYED-ADDRESS) gegen einen realen Server.
    /// </summary>
    [DockerRequiredFact]
    public async Task SdkClient_allocates_a_relay_address_against_real_coturn()
    {
        await using var coturn = new CoturnContainer();
        await coturn.StartAsync();

        var codec = new StunMessageCodec();
        using var client = new RawTurnUdpClient(coturn.ServerEndPoint, codec);

        var allocation = await client.AllocateAuthenticatedAsync(Credentials())
            .WaitAsync(StepTimeout);

        // coturn muss eine geroutete Relay-Adresse mit einem Port aus dem konfigurierten Relay-Bereich
        // annoncieren und eine positive Lifetime vergeben.
        Assert.Equal(coturn.HostIp, allocation.RelayedEndPoint.Address.ToString());
        Assert.InRange(allocation.RelayedEndPoint.Port, 49160, 49200);
        Assert.True(allocation.LifetimeSeconds > 0,
            "coturn muss eine positive Allocation-Lifetime vergeben haben.");
    }

    /// <summary>
    /// Test B (Composite / F2): Answerer-Relay gegen echten coturn. Der Answerer allokiert und permissioniert
    /// proaktiv die Offerer-Relay-IP; der Offerer sendet über coturn an die Answerer-Relay-Adresse; der
    /// Answerer empfängt das Datagramm als Data-Indication. Belegt den Inbound-Relay-Empfangspfad real.
    /// </summary>
    [DockerRequiredFact]
    public async Task Answerer_receives_an_inbound_relayed_datagram_from_the_offerer_via_real_coturn()
    {
        await using var coturn = new CoturnContainer();
        await coturn.StartAsync();

        var codec = new StunMessageCodec();

        // Zwei getrennte Allocations, jede mit ihrem eigenen stabilen Socket (5-Tuple), gegen denselben
        // echten coturn: der Answerer (SDK-Client unter Test) und der Offerer (der sendende Peer).
        using var answerer = new RawTurnUdpClient(coturn.ServerEndPoint, codec);
        using var offerer = new RawTurnUdpClient(coturn.ServerEndPoint, codec);

        var answererAllocation = await answerer.AllocateAuthenticatedAsync(Credentials()).WaitAsync(StepTimeout);
        var offererAllocation = await offerer.AllocateAuthenticatedAsync(Credentials()).WaitAsync(StepTimeout);

        // Answerer-Relay-Slice: der controlled Answerer installiert PROAKTIV eine Permission (RFC 8656 §9)
        // für die Quell-IP, aus der die Offerer-Datagramme bei coturn eintreffen — die Offerer-Relay-IP. Das
        // ist die Voraussetzung dafür, dass coturn das Inbound-Datagramm an die Answerer-Allocation weiterleitet
        // (ohne Permission verwirft coturn es still).
        await answerer.CreatePermissionAuthenticatedAsync(Credentials(), offererAllocation.RelayedEndPoint)
            .WaitAsync(StepTimeout);

        // Der Offerer muss die Answerer-Relay-IP ebenfalls permissionieren, damit coturn seine Send-Indication
        // an die Answerer-Relay-Adresse überhaupt annimmt und relayed.
        await offerer.CreatePermissionAuthenticatedAsync(Credentials(), answererAllocation.RelayedEndPoint)
            .WaitAsync(StepTimeout);

        // Der Offerer relayed einen Inbound-Check an die Answerer-Relay-Adresse (der Pfad, auf dem ein echter
        // ICE-Connectivity-Check beim relay-empfangenden Answerer ankäme).
        var inboundCheck = "INBOUND-CHECK-FROM-OFFERER"u8.ToArray();
        await offerer.SendIndicationAsync(answererAllocation.RelayedEndPoint, inboundCheck).WaitAsync(StepTimeout);

        // Der Answerer empfängt das relayed Datagramm als TURN-Data-Indication und demuxt es (Payload + Peer).
        var inbound = await answerer.ReceiveRelayAsync().WaitAsync(StepTimeout);

        Assert.False(inbound.IsChannelData, "Permission-only-Relay liefert eine Data-Indication, kein ChannelData.");
        Assert.Equal(inboundCheck, inbound.Data);
        // Die Peer-Adresse in der Data-Indication ist die Offerer-Relay-Adresse (die Quelle, wie coturn sie sieht).
        Assert.NotNull(inbound.PeerEndPoint);
        Assert.Equal(offererAllocation.RelayedEndPoint.Address, inbound.PeerEndPoint!.Address);
    }
}
