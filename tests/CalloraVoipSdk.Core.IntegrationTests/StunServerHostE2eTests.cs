using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Hosting;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Proves the public <see cref="StunServerHost"/> facade produces a real, serving STUN server: a hosted server
/// answers a Binding request over the wire with a Binding Success Response echoing the transaction id.
/// </summary>
public sealed class StunServerHostE2eTests
{
    [Fact]
    public async Task The_hosted_stun_server_answers_a_binding_request()
    {
        await using var host = new StunServerHost(new StunServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });
        host.Start();

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var request = new byte[20];
        request[0] = 0x00; // Binding request (0x0001)
        request[1] = 0x01;
        request[4] = 0x21; // RFC 5389 magic cookie
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;
        for (byte i = 0; i < 12; i++)
            request[8 + i] = (byte)(i + 1); // transaction id

        await client.SendAsync(request, request.Length, host.LocalEndPoint);
        var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0x01, response.Buffer[0]);                 // Binding Success Response (0x0101)
        Assert.Equal(0x01, response.Buffer[1]);
        Assert.Equal(request.AsSpan(8, 12).ToArray(), response.Buffer.AsSpan(8, 12).ToArray()); // transaction id echoed
    }
}
