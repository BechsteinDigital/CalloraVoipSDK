namespace CalloraVoipSdk.InteropTests.Asterisk;

/// <summary>
/// Serialisiert Asterisk-Container im Hostnetz innerhalb eines Testprozesses und über parallel
/// laufende Target-Framework-Testprozesse hinweg. Damit können alle Instanzen die festen
/// Asterisk-Ports 5060/5061 und den RTP-Bereich konfliktfrei verwenden.
/// </summary>
internal sealed class BrowserSafeInteropLease : IDisposable
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private readonly FileStream _lockFile;
    private bool _disposed;

    private BrowserSafeInteropLease(FileStream lockFile)
    {
        _lockFile = lockFile;
    }

    public static async Task<BrowserSafeInteropLease> AcquireAsync()
    {
        await ProcessGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lockPath = Path.Combine(Path.GetTempPath(), "callora-voipsdk-browser-safe-container.lock");
            var deadline = DateTimeOffset.UtcNow + AcquisitionTimeout;

            while (true)
            {
                try
                {
                    var lockFile = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return new BrowserSafeInteropLease(lockFile);
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
        {
            return;
        }

        _disposed = true;
        _lockFile.Dispose();
        ProcessGate.Release();
    }
}
