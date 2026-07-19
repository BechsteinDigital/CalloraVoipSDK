using System.Net.Sockets;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// A relay socket reserved by an EVEN-PORT (reserve) allocation (RFC 8656 §7): the odd port next to an even
/// relayed port, bound eagerly and held until a RESERVATION-TOKEN allocation claims it or it expires.
/// </summary>
/// <param name="Socket">The pre-bound reserved relay socket.</param>
/// <param name="ExpiresAtUtc">When the reservation lapses and the socket is released.</param>
internal sealed record TurnPortReservation(UdpClient Socket, DateTimeOffset ExpiresAtUtc);
