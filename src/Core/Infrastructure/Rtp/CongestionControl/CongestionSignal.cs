namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Coarse congestion state derived from the transport-cc delay trend (the classic delay-based
/// signal: overuse / normal / underuse). It says which way the network queue is trending, not by how
/// much — a rate-control policy turns it into a target bitrate.
/// </summary>
internal enum CongestionSignal
{
    /// <summary>The one-way delay is stable: no congestion signal.</summary>
    Normal,

    /// <summary>The one-way delay is rising past the threshold — a queue is building (slow down).</summary>
    Overusing,

    /// <summary>The one-way delay is falling past the threshold — the queue is draining (room to grow).</summary>
    Underusing,
}
