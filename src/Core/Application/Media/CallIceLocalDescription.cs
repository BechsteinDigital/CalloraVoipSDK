using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Local ICE credentials and candidates generated for one call leg.
/// </summary>
internal sealed class CallIceLocalDescription
{
    /// <summary>
    /// Generated local ICE username fragment.
    /// </summary>
    public required string Ufrag { get; init; }

    /// <summary>
    /// Generated local ICE password.
    /// </summary>
    public required string Pwd { get; init; }

    /// <summary>
    /// Optional ICE options value for local SDP.
    /// </summary>
    public string? Options { get; init; }

    /// <summary>
    /// Gathered local ICE candidates for the audio m-line.
    /// </summary>
    public IReadOnlyList<CallIceCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Gathered local ICE candidates for the video m-line (its own 5-tuple; the ufrag/pwd are
    /// shared session-wide). Host, server-reflexive and relay, mirroring the audio candidates.
    /// Empty on an audio-only leg.
    /// </summary>
    public IReadOnlyList<CallIceCandidate> VideoCandidates { get; init; } = [];
}
