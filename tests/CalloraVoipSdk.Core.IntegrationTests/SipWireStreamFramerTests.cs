using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behavioural gate for <see cref="SipWireStreamFramer"/>. The framer parses the header
/// block once (single split) and reuses it for both the Content-Length and
/// Transfer-Encoding checks; these tests lock that parsing behaviour so the allocation
/// refactor cannot silently change how frames are sized or rejected.
/// </summary>
public sealed class SipWireStreamFramerTests
{
    private static byte[] Message(string headers, string body)
    {
        var text = headers.Replace("\n", "\r\n") + "\r\n" + body;
        return Encoding.UTF8.GetBytes(text);
    }

    [Fact]
    public void Frames_a_complete_message_by_content_length()
    {
        var framer = new SipWireStreamFramer();
        var message = Message(
            "MESSAGE sip:bob@example.org SIP/2.0\n" +
            "Call-ID: c1\n" +
            "Content-Length: 5\n",
            "hello");

        framer.Append(message);

        Assert.True(framer.TryReadFrame(out var frame));
        Assert.Equal(message.Length, frame.Length);
        Assert.False(framer.TryReadFrame(out _)); // buffer fully consumed
    }

    [Fact]
    public void Accepts_compact_content_length_header_l()
    {
        var framer = new SipWireStreamFramer();
        var message = Message(
            "MESSAGE sip:bob@example.org SIP/2.0\n" +
            "l: 3\n",
            "abc");

        framer.Append(message);

        Assert.True(framer.TryReadFrame(out var frame));
        Assert.Equal(message.Length, frame.Length);
    }

    [Fact]
    public void Rejects_chunked_transfer_encoding()
    {
        var framer = new SipWireStreamFramer();
        var message = Message(
            "MESSAGE sip:bob@example.org SIP/2.0\n" +
            "Transfer-Encoding: chunked\n" +
            "Content-Length: 0\n",
            string.Empty);

        framer.Append(message);

        Assert.Throws<InvalidOperationException>(() => framer.TryReadFrame(out _));
    }

    [Fact]
    public void Reassembles_a_message_split_across_multiple_appends()
    {
        var framer = new SipWireStreamFramer();
        var message = Message(
            "MESSAGE sip:bob@example.org SIP/2.0\n" +
            "Content-Length: 4\n",
            "data");

        // Feed the message in three fragments; only the final append completes the frame.
        framer.Append(message.AsSpan(0, 10));
        Assert.False(framer.TryReadFrame(out _));
        framer.Append(message.AsSpan(10, message.Length - 15));
        Assert.False(framer.TryReadFrame(out _));
        framer.Append(message.AsSpan(message.Length - 5));

        Assert.True(framer.TryReadFrame(out var frame));
        Assert.Equal(message.Length, frame.Length);
    }

    [Fact]
    public void Consumes_a_double_crlf_keepalive_ping_without_producing_a_frame()
    {
        var framer = new SipWireStreamFramer();
        framer.Append(Encoding.UTF8.GetBytes("\r\n\r\n"));

        Assert.False(framer.TryReadFrame(out _));
        Assert.True(framer.ConsumedKeepalivePing);

        framer.ClearKeepalivePingFlag();
        Assert.False(framer.ConsumedKeepalivePing);
    }

    [Fact]
    public void Throws_on_missing_content_length_over_stream_transport()
    {
        var framer = new SipWireStreamFramer();
        var message = Message(
            "MESSAGE sip:bob@example.org SIP/2.0\n" +
            "Call-ID: c1\n",
            string.Empty);

        framer.Append(message);

        Assert.Throws<InvalidOperationException>(() => framer.TryReadFrame(out _));
    }
}
