using CalloraVoipSdk.Core.Application.Ports.Security;

namespace CalloraVoipSdk;

/// <summary>
/// Options for convenience registration flows.
/// </summary>
public sealed class ConnectOptions
{
    /// <summary>
    /// Default connect options.
    /// </summary>
    public static ConnectOptions Default { get; } = new();

    /// <summary>
    /// Optional per-line TLS configuration (mutual-TLS client certificate and RFC 5922 server-trust
    /// policy) applied to this line's SIP-over-TLS signaling, overriding the client-wide
    /// <c>SdkConfiguration.Tls</c> (issue #183). When <see langword="null"/> the line uses the
    /// client-wide TLS identity. The supplied certificate is caller-owned; the SDK never disposes it.
    /// Only relevant when the line's transport is TLS or WSS.
    /// </summary>
    public TlsConfiguration? LineTls { get; init; }

    /// <summary>
    /// Maximum time to wait until the line reaches <see cref="Domain.Lines.LineState.Registered"/>.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Completes with <see cref="ConnectStatus.Failed"/> as soon as the line enters
    /// <see cref="Domain.Lines.LineState.RegistrationFailed"/>.
    /// </summary>
    public bool FailFastOnRegistrationFailed { get; init; } = true;
}
