using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;

// Demonstrates the public WebRTC facade (ADR-012) end-to-end without a browser: two peers connect to each
// other over an in-memory signalling channel, the SDK drives the offer/answer handshake, one peer taps its
// media (L3), and the other reacts to inbound tracks (W3C model). Transport-only — the app owns the codec.

Console.WriteLine("CalloraVoipSdk — WebRTC peer sample (two peers over an in-memory signalling channel)");

// Both peers need a configured media port so their offer AND answer advertise a live m-line (until
// early-bind / trickle ICE lands). A real deployment configures a reachable address here.
var caller = new WebRtcClient(new WebRtcConfiguration { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 47000) });
var callee = new WebRtcClient(new WebRtcConfiguration { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 47001) });

await using var callerPeer = caller.CreatePeer();
await using var calleePeer = callee.CreatePeer();

// L3 media tap: observe everything flowing through the callee — the seam a recording or analytics module
// would use. Dispose the handle to detach.
var recorder = new FrameCounterTap();
using var tap = calleePeer.AttachMediaTap(recorder);

// W3C track model: react to the callee's inbound tracks (subscribe before connecting).
calleePeer.TrackReceived += (_, track) =>
    Console.WriteLine($"[callee] track received: {track.Kind}, stream={track.StreamId ?? "-"}");

var (callerChannel, calleeChannel) = InMemorySignalling.Pair();

Console.WriteLine("Connecting…");
await Task.WhenAll(
    callerPeer.ConnectAsync(callerChannel, WebRtcRole.Offerer),
    calleePeer.ConnectAsync(calleeChannel, WebRtcRole.Answerer));
Console.WriteLine($"Connected: caller={callerPeer.State}, callee={calleePeer.State}");

// The app owns the codec — here we just send a handful of already-encoded audio payloads.
for (var i = 0; i < 5; i++)
{
    await callerPeer.SendAudioAsync(new byte[] { (byte)i, 0xAA, 0xBB });
}

await Task.Delay(250);   // let the media traverse the loopback transport
Console.WriteLine($"[recorder] inbound audio frames observed by the callee: {recorder.InboundAudio}");
Console.WriteLine("Done.");

/// <summary>Counts the inbound audio frames a peer receives — a minimal media tap.</summary>
internal sealed class FrameCounterTap : IMediaTap
{
    private int _inboundAudio;

    public int InboundAudio => Volatile.Read(ref _inboundAudio);

    public void OnAudio(MediaDirection direction, ReadOnlyMemory<byte> payload)
    {
        if (direction == MediaDirection.Inbound)
        {
            Interlocked.Increment(ref _inboundAudio);
        }
    }

    public void OnVideo(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame, string? rid)
    {
    }
}

/// <summary>An in-process signalling channel pair cross-wiring two peers (stands in for the app's transport).</summary>
internal sealed class InMemorySignalling : IWebRtcSignaling
{
    private readonly ChannelReader<string> _inbound;
    private readonly ChannelWriter<string> _outbound;

    private InMemorySignalling(ChannelReader<string> inbound, ChannelWriter<string> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    public Task SendDescriptionAsync(string sdp, CancellationToken cancellationToken = default)
        => _outbound.WriteAsync(sdp, cancellationToken).AsTask();

    public async Task<string> ReceiveDescriptionAsync(CancellationToken cancellationToken = default)
        => await _inbound.ReadAsync(cancellationToken).ConfigureAwait(false);

    public static (IWebRtcSignaling caller, IWebRtcSignaling callee) Pair()
    {
        var callerToCallee = Channel.CreateUnbounded<string>();
        var calleeToCaller = Channel.CreateUnbounded<string>();
        return (
            new InMemorySignalling(calleeToCaller.Reader, callerToCallee.Writer),
            new InMemorySignalling(callerToCallee.Reader, calleeToCaller.Writer));
    }
}
