using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Validates a decoded TURN response against the request that produced it: it enforces the response
/// method match, surfaces long-term-credential challenges (401 Unauthorized / 438 Stale Nonce) as a
/// <see cref="TurnChallengeException"/> carrying the server REALM/NONCE, raises any other error response
/// as a <see cref="TurnException"/>, and returns the message on a success response.
/// <para>
/// Shared by the per-transaction <see cref="TurnClientTransport"/> and the shared-socket
/// <see cref="TurnControlTransactor"/> so both apply identical challenge and error semantics — the
/// 401/438 detection is security-sensitive and must not diverge between the two transports.
/// </para>
/// </summary>
internal static class TurnResponseValidator
{
    /// <summary>
    /// Validates <paramref name="response"/> and returns it when it is a success response for
    /// <paramref name="expectedMethod"/>.
    /// </summary>
    /// <param name="response">The decoded TURN response received for the request.</param>
    /// <param name="expectedMethod">The method of the request that was sent.</param>
    /// <returns>The response when it is a valid success response.</returns>
    /// <exception cref="TurnChallengeException">The response is a 401/438 authentication challenge.</exception>
    /// <exception cref="TurnException">
    /// The response method does not match, or it is an error response, or an unexpected class.
    /// </exception>
    public static StunMessage Validate(StunMessage response, StunMessageMethod expectedMethod)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.MessageMethod != expectedMethod)
            throw new TurnException(
                $"TURN response method mismatch. Expected 0x{(ushort)expectedMethod:X4}, got 0x{(ushort)response.MessageMethod:X4}.");

        if (response.MessageClass == StunMessageClass.ErrorResponse)
        {
            var error = response.Attributes.OfType<ErrorCodeAttribute>().FirstOrDefault();
            int code = error?.Code ?? 0;
            string reason = string.IsNullOrWhiteSpace(error?.Reason) ? "(no reason)" : error!.Reason;

            if (code is 401 or 438)
            {
                var realm = response.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
                var nonce = response.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value;
                throw new TurnChallengeException(code, reason, realm, nonce);
            }

            throw new TurnException($"TURN error {code}: {reason}");
        }

        if (response.MessageClass != StunMessageClass.SuccessResponse)
            throw new TurnException($"TURN unexpected response class: {response.MessageClass}");

        return response;
    }
}
