namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The detach handle returned by <see cref="MediaTapSet.Attach"/>: disposing it removes the tap exactly
/// once (idempotent), so a <c>using</c> scope cleanly bounds a tap's lifetime.
/// </summary>
internal sealed class MediaTapHandle(MediaTapSet owner, IMediaTap tap) : IDisposable
{
    private IMediaTap? _tap = tap;

    public void Dispose()
    {
        var detaching = Interlocked.Exchange(ref _tap, null);
        if (detaching is not null)
        {
            owner.Detach(detaching);
        }
    }
}
