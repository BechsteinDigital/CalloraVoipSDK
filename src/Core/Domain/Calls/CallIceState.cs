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

    /// <summary>ICE checks failed; the leg fell back to the negotiated SDP endpoints.</summary>
    Failed
}
