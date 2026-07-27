namespace CalloraVoipSdk.InteropTests.Turn;

/// <summary>
/// Serialisiert coturn-Container im Hostnetz innerhalb eines Testprozesses und über parallel laufende
/// Target-Framework-Testprozesse hinweg. Damit können alle Instanzen den festen TURN-Port 3478 und den
/// Relay-Port-Bereich konfliktfrei belegen. Eigene Lock-Datei (unabhängig vom Asterisk-Lease), da der
/// coturn-Port sich nicht mit den Asterisk-Ports überschneidet.
/// </summary>
internal sealed class CoturnHostNetworkLease : IDisposable
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private readonly FileStream _lockFile;
    private bool _disposed;

    private CoturnHostNetworkLease(FileStream lockFile) => _lockFile = lockFile;

    public static async Task<CoturnHostNetworkLease> AcquireAsync()
    {
        await ProcessGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lockPath = Path.Combine(Path.GetTempPath(), "callora-voipsdk-coturn-host-network.lock");
            var deadline = DateTimeOffset.UtcNow + AcquisitionTimeout;

            while (true)
            {
                try
                {
                    var lockFile = new FileStream(
                        lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new CoturnHostNetworkLease(lockFile);
                }
                catch (IOException) when (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            ProcessGate.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lockFile.Dispose();
        ProcessGate.Release();
    }
}
