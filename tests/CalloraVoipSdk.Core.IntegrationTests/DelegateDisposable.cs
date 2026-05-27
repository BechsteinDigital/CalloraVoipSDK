namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed class DelegateDisposable : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    public DelegateDisposable(Action dispose)
    {
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _dispose();
    }
}
