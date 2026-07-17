using Microsoft.Extensions.Options;

namespace CalloraVoipSdk.DependencyInjection;

/// <summary>
/// Startup validation for host-bound <see cref="SdkOptions"/>.
/// </summary>
public sealed class SdkOptionsValidator : IValidateOptions<SdkOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SdkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            failures.Add("SdkOptions.UserAgent must not be empty.");
        }

        if (options.MaxConcurrentCallsPerLine < 0)
        {
            failures.Add($"SdkOptions.MaxConcurrentCallsPerLine must be >= 0, got {options.MaxConcurrentCallsPerLine}.");
        }

        // Zero is the documented "disable the timeout" sentinel; a negative value is neither a disable
        // nor a valid interval and would otherwise silently feed a call-teardown timer (HARD-E9).
        if (options.InboundMediaTimeout < TimeSpan.Zero)
        {
            failures.Add(
                $"SdkOptions.InboundMediaTimeout must be >= 0 (TimeSpan.Zero disables it), got {options.InboundMediaTimeout}.");
        }

        if (options.Ice.ConnectivityCheckTimeout <= TimeSpan.Zero)
        {
            failures.Add(
                $"SdkOptions.Ice.ConnectivityCheckTimeout must be > 0, got {options.Ice.ConnectivityCheckTimeout}.");
        }

        if (options.Ice.ConnectivityCheckRetries < 0)
        {
            failures.Add($"SdkOptions.Ice.ConnectivityCheckRetries must be >= 0, got {options.Ice.ConnectivityCheckRetries}.");
        }

        for (var i = 0; i < options.Ice.Servers.Count; i++)
        {
            var server = options.Ice.Servers[i];
            var prefix = $"SdkOptions.Ice.Servers[{i}]";

            if (string.IsNullOrWhiteSpace(server.Host))
            {
                failures.Add($"{prefix}.Host must not be empty.");
            }

            if (server.Port is < 1 or > 65535)
            {
                failures.Add($"{prefix}.Port must be within 1..65535, got {server.Port}.");
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
