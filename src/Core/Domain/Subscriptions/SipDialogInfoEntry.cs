namespace CalloraVoipSdk.Core.Domain.Subscriptions;

/// <summary>One dialog inside a dialog-info document (RFC 4235 §3.7).</summary>
/// <param name="Id">Identifies the dialog within the document; the same id across updates is the same call.</param>
/// <param name="State">Where the call stands.</param>
/// <param name="Direction">
/// <c>initiator</c> or <c>recipient</c> as the document put it, or null. Kept as text: it is the far
/// end's word about its own role, and mapping it to an enum here would invent certainty.
/// </param>
/// <param name="LocalIdentity">The watched party, when the document names it.</param>
/// <param name="RemoteIdentity">Who they are talking to — the number a busy lamp can show on hover.</param>
public sealed record SipDialogInfoEntry(
    string? Id,
    SipDialogState State,
    string? Direction,
    string? LocalIdentity,
    string? RemoteIdentity);
