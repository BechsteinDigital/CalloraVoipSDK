namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// Detects an ICE restart (RFC 8445 §9.1.1.1): a re-negotiation is a restart when the peer's
/// ice-ufrag and/or ice-pwd differ from the values of the currently running session. On a restart
/// the agent must re-gather and re-run connectivity checks against the new credentials (a fresh
/// <c>BuildLocalDescriptionAsync</c> already yields new local ufrag/pwd). Pure comparison — the
/// caller supplies the current and the newly negotiated credentials.
/// </summary>
internal static class IceRestartDetector
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="incomingUfrag"/> /
    /// <paramref name="incomingPwd"/> represent an ICE restart relative to the running
    /// <paramref name="currentUfrag"/> / <paramref name="currentPwd"/>.
    /// </summary>
    public static bool IsRestart(
        string? currentUfrag,
        string? currentPwd,
        string? incomingUfrag,
        string? incomingPwd)
    {
        // First negotiation: there is no running session to restart from.
        if (string.IsNullOrEmpty(currentUfrag) && string.IsNullOrEmpty(currentPwd))
            return false;

        // A restart requires ICE credentials in the new negotiation; their absence signals ICE
        // being removed, not restarted.
        if (string.IsNullOrEmpty(incomingUfrag) || string.IsNullOrEmpty(incomingPwd))
            return false;

        return !string.Equals(currentUfrag, incomingUfrag, StringComparison.Ordinal)
            || !string.Equals(currentPwd, incomingPwd, StringComparison.Ordinal);
    }
}
