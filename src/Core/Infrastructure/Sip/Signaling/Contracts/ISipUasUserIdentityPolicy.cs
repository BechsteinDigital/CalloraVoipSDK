namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Determines whether an inbound SIP request targets a user served by this UAS.
///
/// Per RFC 3261 §8.2.2.1, a UAS SHOULD inspect the Request-URI and To header to
/// decide if it services the addressed user. If the user is not known, the UAS
/// MUST respond with 404 Not Found.
///
/// The default is <see cref="AcceptAllSipUasUserIdentityPolicy"/>, which accepts every inbound request.
/// Configuring served addresses-of-record selects <see cref="ServedUserSipIdentityPolicy"/> instead, which
/// matches the Request-URI with a full RFC 3261 §19.1.4 URI comparison. This interface is internal: the
/// selection is made from configuration, not by injecting a consumer implementation.
/// </summary>
internal interface ISipUasUserIdentityPolicy
{
    /// <summary>
    /// Returns <c>true</c> when the <paramref name="requestUri"/> identifies a user
    /// served by this UAS and the request should be processed.
    /// Returns <c>false</c> to reject the inbound request with 404 Not Found.
    /// </summary>
    /// <param name="requestUri">Normalized SIP or SIPS Request-URI from the inbound request.</param>
    bool IsServedUser(string requestUri);
}
