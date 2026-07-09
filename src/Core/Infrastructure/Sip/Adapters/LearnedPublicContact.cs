namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Immutable NAT-learned public signaling address (host and optional port) discovered from a
/// registrar's Via <c>received=</c>/<c>rport=</c> (RFC 3261 §18.2.1 / RFC 3581). Held by
/// <see cref="SipLineChannel"/> behind a volatile reference so cross-thread readers never
/// observe a torn host/port pair.
/// </summary>
internal sealed record LearnedPublicContact(string Host, int? Port);
