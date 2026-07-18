using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// The minimal ICE view a single media 5-tuple needs to answer inbound connectivity checks
/// (RFC 8445 §7.3) and run consent freshness (RFC 7675): the nominated remote endpoint, the
/// active/role flags, and the local/remote short-term credentials. Decouples
/// <see cref="IceMediaAttachment"/> and <see cref="IceMediaConsentSessionFactory"/> from the
/// session-level <see cref="CallMediaParameters"/> so the video stream can attach ICE to its own
/// port with the same session-shared credentials — the ufrag/pwd are shared across m-lines in
/// this SDK (no BUNDLE), only the 5-tuple differs.
/// </summary>
internal sealed record IceMediaParameters(
    IPEndPoint RemoteEndPoint,
    bool IceEnabled,
    bool IceControlling,
    string? LocalIceUfrag,
    string? LocalIcePwd,
    string? RemoteIceUfrag,
    string? RemoteIcePwd)
{
    /// <summary>
    /// The remote candidates the controlling agent runs connectivity checks against to nominate a pair
    /// (RFC 8445 §7.2.2/§8). Empty runs no candidate-pair checking — consent stays on
    /// <see cref="RemoteEndPoint"/> and the symmetric transport latches the peer's source. The audio/video
    /// projections leave it empty; the WebRTC path populates it from the peer's <c>a=candidate</c> lines.
    /// </summary>
    public IReadOnlyList<IceRemoteCandidate> RemoteCandidates { get; init; } = [];

    /// <summary>Projects the audio/session-level ICE parameters onto the audio media 5-tuple.</summary>
    public static IceMediaParameters FromCall(CallMediaParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new IceMediaParameters(
            parameters.RemoteEndPoint,
            parameters.IceEnabled,
            parameters.IceControlling,
            parameters.LocalIceUfrag,
            parameters.LocalIcePwd,
            parameters.RemoteIceUfrag,
            parameters.RemoteIcePwd);
    }

    /// <summary>
    /// Projects the video stream's ICE parameters onto the video media 5-tuple — the same
    /// session-shared credentials as audio, but the video remote endpoint (its own port).
    /// </summary>
    public static IceMediaParameters FromVideo(CallVideoParameters video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return new IceMediaParameters(
            video.RemoteEndPoint,
            video.IceEnabled,
            video.IceControlling,
            video.LocalIceUfrag,
            video.LocalIcePwd,
            video.RemoteIceUfrag,
            video.RemoteIcePwd);
    }
}
