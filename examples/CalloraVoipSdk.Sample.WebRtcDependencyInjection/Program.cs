using System.Net;
using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;

// Demonstrates the 4.6 dependency-injection surface and two-facade composition with the renamed API:
// AddCalloraVoip(...) (formerly AddCallora) composes with AddWebRtc(...), and the WebRTC client is
// resolved from the container. Transport-only — the app owns signalling and the codec.

var services = new ServiceCollection();
services
    .AddCalloraVoip(voip =>
    {
        voip.UserAgent = "WebRtcDiSample/1.0";
        voip.EnableAutomaticAudioDeviceSelection = false;
    })
    .AddWebRtc(rtc =>
    {
        rtc.LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 46000);
        rtc.EnableVideo = true;
    });

using var provider = services.BuildServiceProvider();

// Both facades are registered; here we resolve the WebRTC one.
var rtc = provider.GetRequiredService<IWebRtcClient>();

await using var peer = rtc.CreatePeer();
var offer = peer.CreateOffer();

Console.WriteLine("Resolved IWebRtcClient from DI (AddCalloraVoip(...).AddWebRtc(...)).");
Console.WriteLine($"Live peers tracked by the client: {rtc.Peers.Count}");
Console.WriteLine();
Console.WriteLine("Generated WebRTC offer (BUNDLE + DTLS-SRTP + a=msid):");
Console.WriteLine(offer);
