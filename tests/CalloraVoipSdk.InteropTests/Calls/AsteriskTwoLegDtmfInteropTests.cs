using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.InteropTests.Media;
using CalloraVoipSdk.InteropTests.Pbx;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Abstrakte Basis: DTMF-End-to-End-Roundtrip über den gebrückten Zwei-Bein-Call (RFC 4733): der Caller
/// sendet die Ziffern 1-2-3-4 als telephone-event RTP-Pakete, der PBX relayiert sie über die Bridge
/// und der Callee empfängt sie über <see cref="ICall.DtmfReceived"/>. Beweist erstmals, dass DTMF
/// nicht nur SDK-seitig gesendet, sondern auch am Gegenpeer einer echten Bridge vollständig ankommt.
/// </summary>
public abstract class TwoLegDtmfMatrix
{
    protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);

    [DockerRequiredFact]
    public async Task Dtmf_TraversesBridge_CallerToCallee()
    {
        await using var pbx = CreatePbx();
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(pbx);

        // RFC 4733 telephone-event muss auf beiden Legs verhandelt sein.
        Assert.NotNull(bridged.CallerCall.MediaParameters!.TelephoneEventPayloadType);
        Assert.NotNull(bridged.CalleeCall.MediaParameters!.TelephoneEventPayloadType);

        var received = new ConcurrentQueue<string>();
        bridged.CalleeCall.DtmfReceived += (_, e) => received.Enqueue(e.Tone.Symbol.ToString());

        // RTP-Pfad warm halten, während die telephone-events fließen.
        await using var flow = bridged.StartBidirectionalMedia();

        // Ziffern 1-2-3-4 vom Caller senden (250 ms Abstand, damit der PBX sie einzeln relayt).
        var tones = new[] { new DtmfTone('1'), new DtmfTone('2'), new DtmfTone('3'), new DtmfTone('4') };
        foreach (var tone in tones)
        {
            await bridged.CallerCall.SendDtmfAsync(tone);
            await Task.Delay(250);
        }

        // Auf den Empfang der 4 Ziffern beim Callee pollen (Deadline gegen RTCP/Relay-Latenz).
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (received.Count < 4 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(200);

        var digits = string.Concat(received);
        Assert.Equal("1234", digits);
    }
}

/// <summary>Fährt die DTMF-Matrix gegen einen echten Asterisk.</summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegDtmfMatrix : TwoLegDtmfMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}
