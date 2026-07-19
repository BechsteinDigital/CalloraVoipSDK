using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 5389 §15.5 FINGERPRINT validation on the server side: a present FINGERPRINT must be valid or the request
/// is discarded. The codec always had <c>VerifyFingerprint</c>; the STUN and TURN request paths now call it.
/// </summary>
public sealed class ServerFingerprintValidationTests
{
    [Fact]
    public void Stun_binding_handler_answers_a_valid_fingerprint_and_discards_an_invalid_one()
    {
        var codec = new StunMessageCodec();
        var handler = new StunBindingRequestHandler(codec, NullLogger<StunBindingRequestHandler>.Instance);
        var sender = new IPEndPoint(IPAddress.Loopback, 5000);

        var request = StunMessage.CreateBindingRequest();
        var validRaw = codec.EncodeWithIntegrity(request, new byte[16], addFingerprint: true);
        var validDecoded = codec.Decode(validRaw)!;

        Assert.NotNull(handler.Handle(validDecoded, validRaw, sender)); // valid FINGERPRINT → answered

        var badRaw = (byte[])validRaw.Clone();
        badRaw[12] ^= 0xFF; // flip a transaction-id byte so the CRC no longer matches the stored fingerprint
        var badDecoded = codec.Decode(badRaw)!;

        Assert.Null(handler.Handle(badDecoded, badRaw, sender)); // present but invalid FINGERPRINT → discarded
    }

    [Fact]
    public async Task Turn_server_drops_a_request_with_an_invalid_fingerprint()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RequireAuthentication = false,
        });
        host.Start();

        // An Allocate request (method 0x003, class request) carrying a FINGERPRINT with a deliberately wrong CRC.
        var datagram = new byte[]
        {
            0x00, 0x03, 0x00, 0x08,                 // Allocate request, message length 8 (one 8-byte attribute)
            0x21, 0x12, 0xA4, 0x42,                 // magic cookie
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,  // transaction id
            0x80, 0x28, 0x00, 0x04,                 // FINGERPRINT attribute (type 0x8028, length 4)
            0xDE, 0xAD, 0xBE, 0xEF,                 // wrong CRC
        };

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(datagram, datagram.Length, host.LocalEndPoint);

        // The fingerprint check drops the datagram before dispatch → no response arrives within the window.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.ReceiveAsync(cts.Token));
    }
}
