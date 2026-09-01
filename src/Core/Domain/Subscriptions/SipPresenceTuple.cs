namespace CalloraVoipSdk.Core.Domain.Subscriptions;

/// <summary>One reported state inside a presence document (PIDF, RFC 3863 §4.1).</summary>
/// <param name="Id">Identifies the tuple within the document; stable across updates.</param>
/// <param name="IsOpen">
/// Whether this one reports itself reachable. RFC 3863 defines <c>open</c> and <c>closed</c> and
/// nothing else — anything unrecognised is read as not open, because announcing somebody as available
/// on the strength of a value nobody defined is the wrong way to be wrong.
/// </param>
/// <param name="Contact">Where to reach this state, when the document says.</param>
/// <param name="Note">Free text the other side chose to add ("in a meeting").</param>
public sealed record SipPresenceTuple(string? Id, bool IsOpen, string? Contact, string? Note);
