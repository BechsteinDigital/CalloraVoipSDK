using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Turn;

/// <summary>
/// End-to-end-Beweis, dass das <b>Produktions</b>-Relay-Gathering-Primitiv des SDK —
/// <see cref="TurnAllocationProbe"/>, genau der Code, den <c>WebRtcPeerConnection.GatherCandidatesAsync</c>
/// treibt, um einen Relay-ICE-Kandidaten zu gathern — gegen einen ECHTEN coturn-Server (Docker) allokiert.
/// Ergänzt <see cref="CoturnRelayE2eTests"/> (das den test-eigenen <c>RawTurnUdpClient</c> nutzt): hier läuft
/// der ausgelieferte Gathering-Pfad selbst gegen echten coturn, nicht eine parallele Raw-Implementierung —
/// schließt die Lücke „SDK-Relay-Pfad nur gegen den in-process-<c>TurnServer</c>-Fake getestet".
/// </summary>
[Trait("Category", "Interop")]
public sealed class CoturnGatheringProbeE2eTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Der Produktions-Probe allokiert authentifiziert (Long-Term-Credentials, 401-Challenge →
    /// MESSAGE-INTEGRITY, RFC 5389 §10.2 / RFC 8656 §7) auf einem gebundenen Media-Socket gegen echten coturn
    /// und liefert eine geroutete Relay-Adresse — den Relay-ICE-Kandidaten, den das SDK dem Peer annonciert.
    /// </summary>
    [DockerRequiredFact]
    public async Task Production_probe_gathers_a_relay_candidate_against_real_coturn()
    {
        await using var coturn = new CoturnContainer();
        await coturn.StartAsync();

        // Der bereits gebundene Media-Socket, auf dem WebRtcPeerConnection auch gathert (5-Tuple-stabil).
        using var media = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var probe = new TurnAllocationProbe(new StunMessageCodec(), NullLoggerFactory.Instance);

        var credentials = new StunCredentials
        {
            Username = CoturnContainer.TurnUsername,
            Password = CoturnContainer.TurnPassword,
            Realm = CoturnContainer.TurnRealm,
        };

        var allocation = await probe
            .TryAllocateAsync(media.Client, coturn.ServerEndPoint, credentials, lifetimeSeconds: null, CancellationToken.None)
            .WaitAsync(StepTimeout);

        // Der Produktions-Probe trieb den vollen authentifizierten Allocate (Allocate → 401 →
        // MESSAGE-INTEGRITY → XOR-RELAYED-ADDRESS) gegen echten coturn und lieferte einen gerouteten
        // Relay-Kandidaten — nicht null (null hieße: kein Relay angeboten).
        Assert.NotNull(allocation);
        Assert.Equal(coturn.HostIp, allocation!.RelayedEndPoint.Address.ToString());
        Assert.InRange(allocation.RelayedEndPoint.Port, 49160, 49200);
        Assert.True(allocation.LifetimeSeconds > 0, "coturn muss eine positive Allocation-Lifetime vergeben haben.");
        // Realm/Nonce für Refresh/Permission-Folgeanfragen durchgereicht (RFC 8656 §3.9 / §9).
        Assert.NotNull(allocation.EffectiveCredentials);
    }
}
