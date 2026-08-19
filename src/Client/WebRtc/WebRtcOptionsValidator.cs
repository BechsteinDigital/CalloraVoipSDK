using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.Options;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Startup validation for host-bound <see cref="WebRtcOptions"/>. The WebRTC counterpart to
/// <see cref="VoipOptionsValidator"/>: registered with <c>ValidateOnStart()</c> so an invalid ICE-server
/// entry surfaces at host start rather than only when the first peer is created.
/// </summary>
public sealed class WebRtcOptionsValidator : IValidateOptions<WebRtcOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, WebRtcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        for (var i = 0; i < options.IceServers.Count; i++)
        {
            var server = options.IceServers[i];
            var prefix = $"WebRtcOptions.IceServers[{i}]";

            if (string.IsNullOrWhiteSpace(server.Host))
            {
                failures.Add($"{prefix}.Host must not be empty.");
            }

            // Port is optional here (null selects protocol defaults); only a supplied value is range-checked.
            if (server.Port is { } port && port is < 1 or > 65535)
            {
                failures.Add($"{prefix}.Port must be within 1..65535, got {port}.");
            }

            if (server.Type == IceServerType.Turn)
            {
                if (string.IsNullOrWhiteSpace(server.Username))
                {
                    failures.Add($"{prefix}.Username is required for TURN servers.");
                }

                if (string.IsNullOrWhiteSpace(server.Password))
                {
                    failures.Add($"{prefix}.Password is required for TURN servers.");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
