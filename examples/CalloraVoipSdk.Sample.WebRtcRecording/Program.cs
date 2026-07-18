using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;

// Demonstrates recording via an L3 media tap: two peers connect over an in-memory signalling channel and
// a tap on the callee records every inbound audio payload to a buffer. Combine with TrackReceived to
// group tracks per participant. Transport-only — the recorded payloads are the raw codec bitstream.

var caller = new WebRtcClient(new WebRtcConfiguration { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 46010) });
var callee = new WebRtcClient(new WebRtcConfiguration { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 46011) });

await using var callerPeer = caller.CreatePeer();
await using var calleePeer = callee.CreatePeer();

var recorder = new AudioRecorder();
using var recording = calleePeer.AttachMediaTap(recorder);

var (callerChannel, calleeChannel) = InMemorySignalling.Pair();

Console.WriteLine("Connecting two peers…");
await Task.WhenAll(
    callerPeer.ConnectAsync(callerChannel, WebRtcRole.Offerer),
    calleePeer.ConnectAsync(calleeChannel, WebRtcRole.Answerer));

// The app owns the codec — send a handful of already-encoded audio payloads.
for (var i = 0; i < 10; i++)
{
    await callerPeer.SendAudioAsync(new byte[] { (byte)i, 0x01, 0x02, 0x03 });
}

await Task.Delay(300);   // let the media traverse the loopback transport
Console.WriteLine($"Recorded {recorder.FrameCount} inbound audio frames ({recorder.ByteCount} bytes).");

/// <summary>A media tap that records every inbound audio payload — a minimal recording module.</summary>
internal sealed class AudioRecorder : IMediaTap
{
    private readonly MemoryStream _buffer = new();
    private int _frames;

    public int FrameCount => Volatile.Read(ref _frames);
    public long ByteCount => _buffer.Length;

    public void OnAudio(MediaDirection direction, ReadOnlyMemory<byte> payload)
    {
        if (direction != MediaDirection.Inbound)
        {
            return;
        }

        lock (_buffer)
        {
            _buffer.Write(payload.Span);
        }

        Interlocked.Increment(ref _frames);
    }

    public void OnVideo(MediaDirection direction, ReadOnlyMemory<byte> frame, uint? rtpTimestamp, bool isKeyFrame)
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
