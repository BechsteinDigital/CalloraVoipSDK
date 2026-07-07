namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Disposes two resources together in deterministic order.
/// </summary>
internal sealed class CompositeDisposable : IDisposable, IAsyncDisposable
{
    private readonly IDisposable _first;
    private readonly IDisposable _second;
    private int _disposed;

    /// <summary>
    /// Creates a paired disposable wrapper.
    /// </summary>
    internal CompositeDisposable(IDisposable first, IDisposable second)
    {
        _first = first ?? throw new ArgumentNullException(nameof(first));
        _second = second ?? throw new ArgumentNullException(nameof(second));
    }

    /// <summary>
    /// Disposes both wrapped resources once.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _first.Dispose();
        _second.Dispose();
    }

    /// <summary>
    /// Asynchronously disposes both wrapped resources once, preferring
    /// <see cref="IAsyncDisposable"/> when a member supports it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisposeMemberAsync(_first).ConfigureAwait(false);
        await DisposeMemberAsync(_second).ConfigureAwait(false);
    }

    private static ValueTask DisposeMemberAsync(IDisposable member)
    {
        if (member is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        member.Dispose();
        return ValueTask.CompletedTask;
    }
}
