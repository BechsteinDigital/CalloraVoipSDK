namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The routing key for an outbound track in the <see cref="BundledOutboundPipeline"/>: the m-line MID and,
/// for simulcast (RFC 8853), the <c>a=rid</c> layer. A non-simulcast m-line registers one track under
/// <c>(mid, null)</c>; a simulcast m-line registers one track per rid under <c>(mid, rid)</c>.
/// </summary>
internal readonly record struct BundledOutboundTrackKey(string Mid, string? Rid);
