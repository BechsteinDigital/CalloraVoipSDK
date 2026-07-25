using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media;

namespace CalloraVoipSdk.InteropTests.Media;

/// <summary>
/// Erzeugt fortlaufend markierte 20-ms-PCMU-<see cref="MediaFrame"/>s (PT 0, 160 Bytes). Die ersten
/// 4 Payload-Bytes tragen einen monoton steigenden uint32-Sequenzzähler (Big-Endian); der Rest ist
/// PCMU-Stille (0xFF). Empfangsseitig lässt sich daraus die gesendete Sequenz rekonstruieren.
/// </summary>
public sealed class MarkedPcmuSource
{
    public const int PayloadType = 0;
    public const int FrameBytes = 160;
    public const uint DurationRtpUnits = 160;

    private uint _next;

    /// <summary>Nächster markierter Frame; der Sequenzzähler beginnt bei 0 und steigt je Aufruf um 1.</summary>
    public MediaFrame Next()
    {
        var payload = new byte[FrameBytes];
        BinaryPrimitives.WriteUInt32BigEndian(payload, _next++);
        payload.AsSpan(4).Fill(0xFF);
        return new MediaFrame(payload, PayloadType, DurationRtpUnits);
    }

    /// <summary>Liest den Sequenzmarker aus einem empfangenen Payload (≥ 4 Bytes).</summary>
    public static uint ReadSequence(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadUInt32BigEndian(payload);
}
