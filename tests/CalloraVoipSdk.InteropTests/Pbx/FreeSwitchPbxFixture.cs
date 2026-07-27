using CalloraVoipSdk.InteropTests.FreeSwitch;

namespace CalloraVoipSdk.InteropTests.Pbx;

/// <summary>Adaptiert <see cref="FreeSwitchContainer"/> auf <see cref="IPbxFixture"/> (spiegelt AsteriskPbxFixture).</summary>
public sealed class FreeSwitchPbxFixture : IPbxFixture
{
    private readonly FreeSwitchContainer _fs;

    public FreeSwitchPbxFixture(int bridgePairs = 1)
        => _fs = new FreeSwitchContainer(extraBridgePairs: Math.Max(0, bridgePairs - 1));

    public Task StartAsync() => _fs.StartAsync();
    public string SipHost => _fs.ContainerIpAddress;
    public int SipUdpPort => 5060;
    public string MediaPlaybackUri => _fs.CallTargetUri("answer");
    public Task<string> GetLogsAsync() => _fs.GetConsoleLogsAsync();

    public PbxBridgePair BridgePair(PbxMediaMode mode, int index) => (mode, index) switch
    {
        (PbxMediaMode.Plain, 0) => new(
            new(_fs.Username, _fs.Password),
            new(_fs.BridgeUsername, _fs.BridgePassword),
            _fs.CallTargetUri("6003")),
        (PbxMediaMode.Plain, _) => new(
            new(_fs.SoakCallerUser(index - 1), _fs.SoakPassword),
            new(_fs.SoakCalleeUser(index - 1), _fs.SoakPassword),
            _fs.CallTargetUri(_fs.SoakBridgeExtension(index - 1))),
        (PbxMediaMode.Sdes, 0) => new(
            new(_fs.SdesUsername, _fs.SdesPassword),
            new(_fs.SdesBridgeUsername, _fs.SdesBridgePassword),
            _fs.CallTargetUri("6004")),
        _ => throw new ArgumentOutOfRangeException(nameof(index), $"Kein Bridge-Paar für ({mode}, {index})."),
    };

    public ValueTask DisposeAsync() => _fs.DisposeAsync();
}
