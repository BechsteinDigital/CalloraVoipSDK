namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// Bundles several <see cref="IRelayKeepAlive"/> loops into one so a media session can drive them through the
/// single keepalive seam on <see cref="RelayIceBinding"/> (e.g. an allocation Refresh loop and a permission
/// refresh loop). Starting starts every member; disposing disposes every member — in reverse start order, and
/// all of them even if one throws, so a failed teardown of one loop cannot skip the others — aggregating any
/// dispose failures.
/// </summary>
internal sealed class CompositeRelayKeepAlive : IRelayKeepAlive
{
    private readonly IReadOnlyList<IRelayKeepAlive> _members;

    /// <summary>Creates a composite over <paramref name="members"/>, started in order and disposed in reverse.</summary>
    public CompositeRelayKeepAlive(params IRelayKeepAlive[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        foreach (var member in members)
            ArgumentNullException.ThrowIfNull(member);
        _members = members;
    }

    /// <inheritdoc />
    public void Start()
    {
        foreach (var member in _members)
            member.Start();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<Exception>? errors = null;
        // Reverse start order: the last-started loop is torn down first. The allocation teardown (Refresh 0)
        // deletes the allocation server-side, so a permission refresh loop listed after it stops first, before
        // there is nothing left to refresh.
        for (var i = _members.Count - 1; i >= 0; i--)
        {
            try
            {
                await _members[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
            throw new AggregateException("One or more relay keepalives failed to dispose.", errors);
    }
}
