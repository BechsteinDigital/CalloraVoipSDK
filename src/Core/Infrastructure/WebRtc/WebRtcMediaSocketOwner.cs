using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Owns a WebRTC peer's shared media socket across the one hand-over in its life: the peer binds it up front
/// (Trickle-ICE early-bind) so the offer/answer can advertise a real ephemeral port and a host candidate
/// <em>before</em> the transport exists, and the BUNDLE transport takes ownership when the session is built.
/// Extracted from <see cref="WebRtcPeerConnection"/> to keep that file under the size limit.
/// </summary>
/// <remarks>
/// The hand-over is the reason this is a type rather than a field: until it happens the peer must dispose the
/// socket itself, afterwards it must not — and disposing it twice would close a socket the transport's receive
/// loop is reading. <see cref="TakeOrphan"/> encodes both halves in one place. Like the connection-state
/// machine, it shares the owning peer's lock, so the socket stays serialised against the peer's other guarded
/// state exactly as it was inline; C# monitors are reentrant, so the peer may call in from inside its own
/// <c>lock</c> blocks.
/// </remarks>
internal sealed class WebRtcMediaSocketOwner
{
    private readonly object _gate;
    private readonly IPEndPoint _bindEndPoint;
    private UdpClient? _socket;
    private bool _handedOver;

    /// <param name="gate">The owning peer's lock, shared so the socket keeps its original serialisation.</param>
    /// <param name="bindEndPoint">The configured local endpoint to bind (a wildcard address is a bind policy).</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public WebRtcMediaSocketOwner(object gate, IPEndPoint bindEndPoint)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _bindEndPoint = bindEndPoint ?? throw new ArgumentNullException(nameof(bindEndPoint));
    }

    /// <summary>
    /// Binds the socket if it is not bound yet and returns the endpoint it is actually on (the real port after an
    /// ephemeral bind). Idempotent — every caller after the first gets the same endpoint.
    /// </summary>
    public IPEndPoint EnsureBound()
    {
        lock (_gate)
        {
            if (_socket is null)
            {
                // Match the socket family to the configured local bind address; binding an IPv4
                // UdpClient to an IPv6 endpoint (or vice versa) throws on family mismatch.
                var socket = new UdpClient(_bindEndPoint.AddressFamily);
                // Kernel SO_RCVBUF for the shared media socket; sized for video bitrates, not the max
                // datagram (MediaSocketDefaults keeps those two concerns separate).
                socket.Client.ReceiveBufferSize = MediaSocketDefaults.SocketReceiveBufferBytes;
                socket.Client.Bind(_bindEndPoint);
                _socket = socket;
            }

            return (IPEndPoint)_socket.Client.LocalEndPoint!;
        }
    }

    /// <summary>The bound socket, or <see langword="null"/> before the first <see cref="EnsureBound"/>.</summary>
    public UdpClient? Socket
    {
        get { lock (_gate) { return _socket; } }
    }

    /// <summary>The endpoint the socket is bound to, or <see langword="null"/> before the bind.</summary>
    public IPEndPoint? BoundEndPoint
    {
        get { lock (_gate) { return _socket?.Client.LocalEndPoint as IPEndPoint; } }
    }

    /// <summary>
    /// Records whether a built session took the socket over. From then on the transport owns it and the peer
    /// must not dispose it.
    /// </summary>
    /// <param name="handedOver">True once a session was built on this socket.</param>
    public void MarkHandedOver(bool handedOver)
    {
        lock (_gate) { _handedOver = handedOver; }
    }

    /// <summary>
    /// Releases the socket at teardown and returns it only if this peer still owns it — i.e. the early bind
    /// happened but no session ever took it over. Returns <see langword="null"/> after a hand-over, and after a
    /// first call, so a double dispose can never close a socket twice.
    /// </summary>
    public UdpClient? TakeOrphan()
    {
        lock (_gate)
        {
            var orphan = _handedOver ? null : _socket;
            _socket = null;
            return orphan;
        }
    }
}
