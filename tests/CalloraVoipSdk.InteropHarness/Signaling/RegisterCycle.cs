namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>Beobachtung eines einzelnen REGISTER-Aufrufs im Refresh-Loop.</summary>
/// <param name="StartCSeq">CSeq, mit dem dieser REGISTER gesendet wurde.</param>
/// <param name="ExistingCallId">Wiederverwendete Call-ID (null beim ersten Zyklus).</param>
public readonly record struct RegisterCycle(int StartCSeq, string? ExistingCallId);
