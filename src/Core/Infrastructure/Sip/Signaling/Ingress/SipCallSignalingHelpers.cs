using System.Diagnostics;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Stateless helpers for <see cref="SipCallSignalingService"/>: outbound INVITE input validation, trace/
/// correlation identifiers, dialog-method classification and default remote-port resolution. Extracted as a
/// static utility (mirroring <c>SipTransportRuntimeUtilities</c>) to keep the signaling service within the
/// per-file size limit; the service imports these via <c>using static</c>, so call sites are unchanged.
/// </summary>
internal static class SipCallSignalingHelpers
{
    /// <summary>
    /// Returns true when an outbound INVITE failure is a transport error eligible for the retry policy.
    /// </summary>
    public static bool IsTransportFailure(InvalidOperationException exception) =>
        SipOutboundInviteRetryPolicy.IsTransportFailure(exception);

    /// <summary>
    /// Validates outbound INVITE request input.
    /// </summary>
    public static void ValidateInviteRequest(SipInviteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LocalUsername))
            throw new ArgumentException("LocalUsername is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.LocalDomain))
            throw new ArgumentException("LocalDomain is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RemoteUri))
            throw new ArgumentException("RemoteUri is required.", nameof(request));
        if (!string.IsNullOrWhiteSpace(request.PreferredIdentityUri))
        {
            var preferredIdentityUri = SipProtocol.ExtractUriFromNameAddr(request.PreferredIdentityUri)
                ?? request.PreferredIdentityUri;
            if (!SipProtocol.TryParseSipUri(
                    preferredIdentityUri,
                    out _,
                    out _,
                    out _))
            {
                throw new ArgumentException(
                    $"PreferredIdentityUri must be a valid SIP URI, got '{request.PreferredIdentityUri}'.",
                    nameof(request));
            }
        }
        if (request.RemotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), "RemotePort must be between 1 and 65535.");
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be positive.");
    }

    /// <summary>
    /// Builds lightweight trace correlation key for SIP events.
    /// </summary>
    public static string BuildCorrelationId(string callId, string operation, string? tag) =>
        string.IsNullOrWhiteSpace(tag)
            ? $"{callId}:{operation}"
            : $"{callId}:{operation}:{tag}";

    /// <summary>
    /// Returns true when method semantically requires an existing SIP dialog.
    /// </summary>
    public static bool IsDialogScopedMethod(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return normalized is "BYE" or "INFO" or "UPDATE" or "PRACK" or "REFER" or "NOTIFY" or "SUBSCRIBE";
    }

    /// <summary>
    /// Resolves effective remote port with SIPS default handling.
    /// </summary>
    public static int ResolveDefaultRemotePort(int configuredPort, bool secureTarget)
    {
        if (secureTarget && configuredPort == 5060)
            return 5061;
        return configuredPort;
    }

    /// <summary>
    /// Resolves a deterministic trace identifier for SIP dialog observability.
    /// </summary>
    public static string ResolveTraceId(string fallback) =>
        Activity.Current?.TraceId.ToString() ?? fallback;
}
