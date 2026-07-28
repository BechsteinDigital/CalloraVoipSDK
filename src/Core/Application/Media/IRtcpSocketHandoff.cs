using System.Net.Sockets;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Implemented by a call channel that reserved the RTCP socket as a pair with the media socket and holds it
/// for handoff to the quality monitor — closing the race where a concurrent call's random port grabs this
/// call's still-unbound RTCP port (N+1) between SDP publication and the monitor's bind. The media
/// orchestrator takes the socket when wiring a non-mux RTCP monitor; a channel that reserved none, muxes
/// RTCP, or already handed it off returns <see langword="null"/>.
/// </summary>
internal interface IRtcpSocketHandoff
{
    /// <summary>Transfers ownership of the held RTCP socket to the caller, or returns null if none is held.</summary>
    UdpClient? TakeRtcpSocket();
}
