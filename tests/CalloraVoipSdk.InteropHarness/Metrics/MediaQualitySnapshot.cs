using CalloraVoipSdk.Core.Application.Media;

namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Öffentliche Momentaufnahme der Empfangsqualität eines Media-Legs (Interop-/Soak-Auswertung).</summary>
/// <param name="CapturedAtUtc">Erfassungszeitpunkt (UTC).</param>
/// <param name="JitterMs">Geschätzter Inter-Arrival-Jitter in Millisekunden.</param>
/// <param name="RoundTripTimeMs">Geglättete RTT-Schätzung in Millisekunden.</param>
/// <param name="PacketsDelivered">Kumulativ an den Consumer ausgelieferte RTP-Pakete.</param>
/// <param name="PacketsDroppedLate">Kumulativ wegen überschrittener Playout-Deadline verworfen.</param>
/// <param name="PacketsDroppedOverflow">Kumulativ wegen erschöpfter Jitter-Buffer-Kapazität verworfen.</param>
/// <param name="PacketsUnrecoverableLoss">Kumulativ nicht verdeckbarer Verlust.</param>
public readonly record struct MediaQualitySnapshot(
    DateTimeOffset CapturedAtUtc,
    double JitterMs,
    double RoundTripTimeMs,
    long PacketsDelivered,
    long PacketsDroppedLate,
    long PacketsDroppedOverflow,
    long PacketsUnrecoverableLoss)
{
    /// <summary>Kapselt das interne Laufzeit-Metrik-Snapshot in einen öffentlichen Wert.</summary>
    internal static MediaQualitySnapshot From(CallMediaRuntimeMetrics m) =>
        // DECISION: bewusst minimale Feld-Oberfläche für den Phase-2-Soak (Jitter + Loss).
        // Weitere CallMediaRuntimeMetrics-Felder (Concealed, Duplicate, BufferedPackets, AdaptiveDelay)
        // bei Bedarf späterer Soaks hier ergänzen.
        new(
            m.CapturedAtUtc, m.EstimatedJitterMs, m.EstimatedRoundTripTimeMs,
            m.PacketsDelivered, m.PacketsDroppedLate, m.PacketsDroppedOverflow, m.PacketsUnrecoverableLoss);
}
