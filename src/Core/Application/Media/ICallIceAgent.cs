using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Application-level ICE orchestration port.
/// Responsible for local candidate gathering and candidate-pair selection.
/// </summary>
internal interface ICallIceAgent
{
    /// <summary>
    /// Builds local ICE credentials and candidates for one SDP offer/answer exchange.
    /// <paramref name="sharedMediaSocket"/> is the already-bound RTP reservation socket;
    /// STUN gathering sends through it so the srflx candidate reflects the real media
    /// port (binding a second socket to that port would fail). When
    /// <paramref name="videoLocalEndPoint"/> is provided, a host candidate for the video
    /// 5-tuple is gathered too (RFC 8839), returned in
    /// <see cref="CallIceLocalDescription.VideoCandidates"/>.
    /// </summary>
    Task<CallIceLocalDescription?> BuildLocalDescriptionAsync(
        IPEndPoint localEndPoint,
        System.Net.Sockets.Socket? sharedMediaSocket = null,
        IPEndPoint? videoLocalEndPoint = null,
        CancellationToken ct = default);

    /// <summary>
    /// Runs ICE connectivity checks and returns the selected candidate pair.
    /// </summary>
    Task<CallIceSelectionResult> SelectCandidatePairAsync(
        CallId callId,
        CallMediaParameters parameters,
        CancellationToken ct = default);
}
