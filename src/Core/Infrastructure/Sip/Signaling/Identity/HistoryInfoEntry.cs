namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// One parsed <c>History-Info</c> entry (RFC 4244): the address the request was targeted at, and the
/// dotted <c>index</c> that places it in the retargeting order.
/// </summary>
/// <param name="Uri">The targeted address, as received.</param>
/// <param name="Index">The dotted index ("1", "1.1", ...); empty when the entry carried none.</param>
internal sealed record HistoryInfoEntry(string Uri, string Index);
