namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Pure, idempotent decision logic for learning a NAT-routable public contact from the
/// address a registrar reflects back (Via <c>received=</c>/<c>rport=</c>).
/// The line channel holds a single learned <c>(Host, Port)</c> state and always builds the
/// Contact from it; a corrective re-registration happens only when applying a fresh
/// observation actually changes that state. This makes the correction self-terminating
/// (a second identical observation yields no change) and self-healing on IP changes,
/// without any "already corrected" flag or REGISTER counter.
/// </summary>
internal static class NatPublicContactState
{
    /// <summary>
    /// Applies a registrar-observed public address to the current learned state.
    /// </summary>
    /// <param name="hasManualOverride">
    /// True when a manual public host is configured; then auto-learning is disabled and the
    /// state never changes (the override wins for the Contact).
    /// </param>
    /// <param name="currentHost">Current learned public host, or null.</param>
    /// <param name="currentPort">Current learned public port, or null.</param>
    /// <param name="observedHost">Host from the response Via <c>received=</c>, or null.</param>
    /// <param name="observedPort">Port from the response Via <c>rport=</c>, or null.</param>
    /// <returns>
    /// The next learned host/port and whether the state changed. When <c>Changed</c> is true
    /// the caller re-registers once immediately so the Contact reflects the new address.
    /// </returns>
    public static (string? Host, int? Port, bool Changed) ApplyObserved(
        bool hasManualOverride,
        string? currentHost,
        int? currentPort,
        string? observedHost,
        int? observedPort)
    {
        // Manual override wins and disables auto-learning entirely — no state, no churn.
        if (hasManualOverride)
            return (null, null, false);

        // Nothing reflected (e.g. LAN/loopback or a registrar without rport) — keep state.
        if (string.IsNullOrWhiteSpace(observedHost))
            return (currentHost, currentPort, false);

        var host = observedHost.Trim();
        var changed = !string.Equals(host, currentHost, StringComparison.OrdinalIgnoreCase)
                      || observedPort != currentPort;

        return (host, observedPort, changed);
    }
}
