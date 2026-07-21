using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

public sealed class ResourceSamplerTests
{
    [Fact]
    public void Capture_ReturnsPositiveMemoryAndThreadCounts()
    {
        var sampler = new ResourceSampler();
        var s = sampler.Capture();

        Assert.True(s.ManagedBytes > 0, "ManagedBytes muss > 0 sein.");
        Assert.True(s.PrivateMemoryBytes > 0, "PrivateMemoryBytes muss > 0 sein.");
        Assert.True(s.WorkingSetBytes > 0, "WorkingSetBytes muss > 0 sein.");
        Assert.True(s.ThreadCount > 0, "ThreadCount muss > 0 sein.");
    }

    [Fact]
    public void Capture_IncrementsSampleIndex()
    {
        var sampler = new ResourceSampler();

        Assert.Equal(0, sampler.Capture().SampleIndex);
        Assert.Equal(1, sampler.Capture().SampleIndex);
        Assert.Equal(2, sampler.Capture().SampleIndex);
    }

    [Fact]
    public void Capture_OnLinux_CountsFileAndSocketDescriptors()
    {
        if (!OperatingSystem.IsLinux())
            return; // /proc-basierte FD/Socket-Zählung nur auf Linux; sonst Sentinel -1 (eigener Test).

        var sampler = new ResourceSampler();
        var before = sampler.Capture();

        Assert.True(before.FileDescriptorCount >= 0, "FD-Count auf Linux ist ≥ 0.");
        Assert.True(before.SocketDescriptorCount >= 0, "Socket-Count auf Linux ist ≥ 0.");
        Assert.True(before.SocketDescriptorCount <= before.FileDescriptorCount,
            "Sockets sind eine Teilmenge aller FDs.");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var after = sampler.Capture();

        Assert.True(after.SocketDescriptorCount > before.SocketDescriptorCount,
            $"Offener UDP-Socket muss den Socket-Zähler erhöhen: {before.SocketDescriptorCount} → {after.SocketDescriptorCount}.");
    }

    [Fact]
    public void Capture_OnNonLinux_ReportsDescriptorSentinel()
    {
        if (OperatingSystem.IsLinux())
            return; // Sentinel-Verhalten gilt nur außerhalb Linux.

        var s = new ResourceSampler().Capture();

        Assert.Equal(-1, s.FileDescriptorCount);
        Assert.Equal(-1, s.SocketDescriptorCount);
    }
}
