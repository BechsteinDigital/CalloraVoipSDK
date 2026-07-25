using CalloraVoipSdk.InteropTests.Asterisk;

namespace CalloraVoipSdk.InteropTests.Pbx;

/// <summary>Adaptiert die bestehende <see cref="AsteriskContainer"/> auf <see cref="IPbxFixture"/>.</summary>
public sealed class AsteriskPbxFixture : IPbxFixture
{
    private readonly AsteriskContainer _asterisk;

    /// <summary><paramref name="bridgePairs"/> = Anzahl bereitgestellter Plain-Bridge-Paare (Paar 0 = Basis 6001/6003).</summary>
    public AsteriskPbxFixture(int bridgePairs = 1)
        => _asterisk = new AsteriskContainer(extraBridgePairs: Math.Max(0, bridgePairs - 1));

    public Task StartAsync() => _asterisk.StartAsync();
    public string SipHost => _asterisk.ContainerIpAddress;
    public int SipUdpPort => 5060;
    public string MediaPlaybackUri => _asterisk.CallTargetUri("answer");
    public Task<string> GetLogsAsync() => _asterisk.GetConsoleLogsAsync();

    public PbxBridgePair BridgePair(PbxMediaMode mode, int index) => (mode, index) switch
    {
        (PbxMediaMode.Plain, 0) => new(
            new(_asterisk.Username, _asterisk.Password),
            new(_asterisk.BridgeUsername, _asterisk.BridgePassword),
            _asterisk.CallTargetUri("6003")),
        (PbxMediaMode.Plain, _) => new(
            new(_asterisk.SoakCallerUser(index - 1), _asterisk.SoakPassword),
            new(_asterisk.SoakCalleeUser(index - 1), _asterisk.SoakPassword),
            _asterisk.CallTargetUri(_asterisk.SoakBridgeExtension(index - 1))),
        (PbxMediaMode.Sdes, 0) => new(
            new(_asterisk.SdesUsername, _asterisk.SdesPassword),
            new(_asterisk.SdesBridgeUsername, _asterisk.SdesBridgePassword),
            _asterisk.CallTargetUri("6004")),
        _ => throw new ArgumentOutOfRangeException(nameof(index), $"Kein Bridge-Paar für ({mode}, {index})."),
    };

    public ValueTask DisposeAsync() => _asterisk.DisposeAsync();
}
