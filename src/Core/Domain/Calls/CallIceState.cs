namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Public lifecycle state of ICE (RFC 8445) connectivity establishment for one call media leg,
/// surfaced read-only via <see cref="CallIceSnapshot.State"/>. Non-ICE calls never produce an
/// ICE snapshot, so this state is only meaningful when ICE was negotiated for the leg.
/// </summary>
public enum CallIceState
{
    /// <summary>ICE is disabled or no ICE metadata was negotiated for this leg.</summary>
    Disabled,

    /// <summary>Local candidate gathering is in progress.</summary>
    Gathering,

    /// <summary>Candidate gathering completed.</summary>
    Gathered,

    /// <summary>Connectivity checks are running.</summary>
    Checking,

    /// <summary>A successful pair is being nominated.</summary>
    Nominating,

    /// <summary>A candidate pair was selected successfully; media flows over the selected pair.</summary>
    Connected,

    /// <summary>
    /// The selected pair lost connectivity after <see cref="Connected"/> — RFC 7675 consent was not
    /// refreshed in time and media transmission ceased. Transient: the path may recover (or be revived
    /// by an ICE restart), so the socket stays open. Distinct from <see cref="Failed"/>, which is an
    /// establishment failure before any pair was selected.
    /// </summary>
    Disconnected,

    /// <summary>ICE checks failed; the leg fell back to the negotiated SDP endpoints.</summary>
    Failed
}
