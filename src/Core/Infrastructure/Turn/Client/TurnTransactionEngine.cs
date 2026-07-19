using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// The transport-agnostic core of a TURN transaction: it builds the request message (credentials header +
/// method attributes + a fresh transaction id), encodes it with MESSAGE-INTEGRITY when authenticated, and
/// runs the RFC 5389 §10.2 long-term-credential challenge/response state machine — an unauthenticated
/// probe when REALM/NONCE are unknown, a retry with MESSAGE-INTEGRITY on the 401 challenge, and a further
/// retry on a 438 Stale Nonce with the server's fresh nonce.
/// <para>
/// The actual send/receive is supplied by the caller as a single-round-trip delegate, so the identical
/// auth orchestration and message construction serve both the per-transaction socket
/// (<see cref="TurnClientTransport"/>) and the shared bundle socket (<see cref="TurnControlTransactor"/>)
/// without the security-sensitive auth flow diverging between the two transports.
/// </para>
/// </summary>
internal sealed class TurnTransactionEngine
{
    private readonly IStunMessageCodec _codec;

    /// <summary>Creates the engine over the shared STUN wire codec.</summary>
    public TurnTransactionEngine(IStunMessageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    /// <summary>
    /// Runs a request with the long-term-credential challenge/response flow, delegating each single
    /// round-trip to <paramref name="sendOnce"/>. Returns the validated success response together with the
    /// effective credentials (updated with any REALM/NONCE the server supplied).
    /// </summary>
    /// <param name="method">The TURN method to issue.</param>
    /// <param name="attributeFactory">
    /// Builds the method-specific attributes for a given transaction id. Invoked fresh per attempt because
    /// attributes such as XOR-PEER-ADDRESS are keyed to the (per-attempt) transaction id.
    /// </param>
    /// <param name="credentials">
    /// The credentials to authenticate with, or <see langword="null"/> for an unauthenticated request.
    /// Short-term credentials skip the challenge flow.
    /// </param>
    /// <param name="sendOnce">Sends one encoded request and returns its validated success response.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The success response and the effective (possibly updated) credentials.</returns>
    public async Task<(StunMessage Response, StunCredentials? EffectiveCredentials)> ExecuteWithAuthAsync(
        TurnMessageMethod method,
        Func<byte[], IReadOnlyList<StunAttribute>> attributeFactory,
        StunCredentials? credentials,
        Func<StunMessage, byte[], CancellationToken, Task<StunMessage>> sendOnce,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attributeFactory);
        ArgumentNullException.ThrowIfNull(sendOnce);

        if (credentials is null || !credentials.IsLongTerm)
        {
            var response = await ExecuteRequestCoreAsync(method, attributeFactory, credentials, sendOnce, ct)
                .ConfigureAwait(false);
            return (response, ApplyAuthUpdates(response, credentials));
        }

        var activeCredentials = credentials;

        // Long-term TURN flow starts unauthenticated when REALM/NONCE are not known yet.
        if (activeCredentials.Realm is null || activeCredentials.Nonce is null)
        {
            try
            {
                _ = await ExecuteRequestCoreAsync(method, attributeFactory, credentials: null, sendOnce, ct)
                    .ConfigureAwait(false);
            }
            catch (TurnChallengeException challenge) when (challenge.ErrorCode == 401
                                                           && challenge.Realm is not null
                                                           && challenge.Nonce is not null)
            {
                activeCredentials = activeCredentials.WithRealmAndNonce(challenge.Realm, challenge.Nonce);
            }
        }

        try
        {
            var response = await ExecuteRequestCoreAsync(method, attributeFactory, activeCredentials, sendOnce, ct)
                .ConfigureAwait(false);
            return (response, ApplyAuthUpdates(response, activeCredentials));
        }
        catch (TurnChallengeException staleNonce) when (staleNonce.ErrorCode == 438 && staleNonce.Nonce is not null)
        {
            activeCredentials = activeCredentials.WithNonce(staleNonce.Nonce);
        }

        var finalResponse = await ExecuteRequestCoreAsync(method, attributeFactory, activeCredentials, sendOnce, ct)
            .ConfigureAwait(false);

        return (finalResponse, ApplyAuthUpdates(finalResponse, activeCredentials));
    }

    /// <summary>
    /// Builds and encodes a fire-and-forget indication (no response is expected), applying the same
    /// credentials header and MESSAGE-INTEGRITY rules as a request.
    /// </summary>
    /// <param name="method">The TURN indication method.</param>
    /// <param name="attributeFactory">Builds the method-specific attributes for the transaction id.</param>
    /// <param name="credentials">Credentials to authenticate with, or <see langword="null"/>.</param>
    /// <returns>The encoded indication datagram.</returns>
    public byte[] EncodeIndication(
        TurnMessageMethod method,
        Func<byte[], IReadOnlyList<StunAttribute>> attributeFactory,
        StunCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(attributeFactory);

        var transactionId = CreateTransactionId();
        var indication = BuildMessage(
            StunMessageClass.Indication,
            method,
            transactionId,
            attributeFactory(transactionId),
            credentials);

        return EncodeMessage(indication, credentials);
    }

    private async Task<StunMessage> ExecuteRequestCoreAsync(
        TurnMessageMethod method,
        Func<byte[], IReadOnlyList<StunAttribute>> attributeFactory,
        StunCredentials? credentials,
        Func<StunMessage, byte[], CancellationToken, Task<StunMessage>> sendOnce,
        CancellationToken ct)
    {
        var transactionId = CreateTransactionId();
        var request = BuildMessage(
            StunMessageClass.Request,
            method,
            transactionId,
            attributeFactory(transactionId),
            credentials);

        var requestBytes = EncodeMessage(request, credentials);

        return await sendOnce(request, requestBytes, ct).ConfigureAwait(false);
    }

    private static StunMessage BuildMessage(
        StunMessageClass messageClass,
        TurnMessageMethod method,
        byte[] transactionId,
        IReadOnlyList<StunAttribute> methodAttributes,
        StunCredentials? credentials)
    {
        var attributes = new List<StunAttribute>();
        if (credentials is not null)
        {
            attributes.Add(new UsernameAttribute { Value = credentials.Username });

            if (credentials.IsLongTerm)
            {
                if (credentials.Realm is not null)
                    attributes.Add(new RealmAttribute { Value = credentials.Realm });
                if (credentials.Nonce is not null)
                    attributes.Add(new NonceAttribute { Value = credentials.Nonce });
            }
        }

        attributes.AddRange(methodAttributes);

        return new StunMessage
        {
            MessageClass = messageClass,
            MessageMethod = (StunMessageMethod)(ushort)method,
            TransactionId = transactionId,
            Attributes = attributes
        };
    }

    private byte[] EncodeMessage(StunMessage message, StunCredentials? credentials)
    {
        if (credentials is null)
            return _codec.Encode(message);

        return _codec.EncodeWithIntegrity(message, credentials.DeriveHmacKey(), addFingerprint: false);
    }

    private static byte[] CreateTransactionId()
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return transactionId;
    }

    private static StunCredentials? ApplyAuthUpdates(StunMessage response, StunCredentials? credentials)
    {
        if (credentials is null || !credentials.IsLongTerm)
            return credentials;

        var realm = response.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value ?? credentials.Realm;
        var nonce = response.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value ?? credentials.Nonce;

        if (realm is null || nonce is null)
            return credentials;

        return !string.Equals(realm, credentials.Realm, StringComparison.Ordinal)
               || !string.Equals(nonce, credentials.Nonce, StringComparison.Ordinal)
            ? credentials.WithRealmAndNonce(realm, nonce)
            : credentials;
    }
}
