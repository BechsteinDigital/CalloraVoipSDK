using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Sends a REFER and waits for the transfer to actually finish (RFC 3515 §2.4.4, RFC 5589 §7).
/// </summary>
/// <remarks>
/// A 202 Accepted says only that the peer took the REFER, not that the transfer worked. The outcome
/// arrives later, as a NOTIFY carrying a <c>message/sipfrag</c> status line on the implicit
/// subscription the REFER created.
///
/// Returning at the 202 made the transferor declare the call terminated while the PBX was still
/// re-bridging the transferee, and it reported success for transfers that then failed. Against a real
/// Asterisk this cost the transferee its dialog in a majority of runs (#256).
/// </remarks>
internal static class SipReferCompletion
{
    /// <summary>
    /// Sends the REFER and resolves once the peer reports the outcome: <see langword="true"/> when the
    /// transfer succeeded, <see langword="false"/> when the REFER was rejected or the transfer failed.
    /// </summary>
    /// <remarks>
    /// The handler is attached <em>before</em> the REFER goes out: a fast peer can deliver the NOTIFY
    /// before the 202 has been processed, and a subscription attached afterwards would miss it.
    ///
    /// When no NOTIFY arrives within <paramref name="timeout"/> the result is <see langword="true"/> —
    /// the peer did accept the REFER, and a peer that suppresses the subscription (RFC 4488) never
    /// reports at all. Waiting is what removes the race; failing closed on silence would break every
    /// transfer against such a peer.
    /// </remarks>
    public static async Task<bool> SendAndAwaitAsync(
        ISipCallSession session,
        string referTo,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNotify(object? sender, SipNotifyReceivedEventArgs e)
        {
            // The dispatcher already strips any ";id=" parameter from the Event header.
            if (!string.Equals(e.EventType, "refer", StringComparison.OrdinalIgnoreCase))
                return;

            var status = TryParseSipfragStatus(e.ContentType, e.Body);
            if (status is null)
            {
                // No usable sipfrag. A terminated subscription still ends the transfer: nothing further
                // will be reported, so waiting on for the full timeout would only add latency.
                if (e.IsTerminated)
                    completion.TrySetResult(true);
                return;
            }

            // 1xx is progress ("SIP/2.0 100 Trying"), not an outcome (RFC 3515 §2.4.5).
            if (status < 200)
                return;

            completion.TrySetResult(status < 300);
        }

        session.NotifyReceived += OnNotify;
        try
        {
            var accepted = await session
                .SendReferAsync(referTo, referredBy: session.LocalUri, ct: ct)
                .ConfigureAwait(false);
            if (!accepted)
                return false;

            try
            {
                return await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogDebug(
                    "REFER on call {CallId} was accepted but reported no outcome within {TimeoutSeconds}s; treating the transfer as complete.",
                    session.CallId,
                    timeout.TotalSeconds);
                return true;
            }
        }
        finally
        {
            session.NotifyReceived -= OnNotify;
        }
    }

    /// <summary>
    /// Reads the status code from a <c>message/sipfrag</c> body whose first line is a status line
    /// (RFC 3420) — e.g. <c>SIP/2.0 200 OK</c>. Returns <see langword="null"/> when the body is not one.
    /// </summary>
    internal static int? TryParseSipfragStatus(string? contentType, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // An explicit non-sipfrag content type is not ours to read. A missing one is tolerated: the
        // status line is self-describing, and peers do omit the header.
        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.Contains("sipfrag", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var line = body.AsSpan();
        var lineEnd = line.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
            line = line[..lineEnd];
        line = line.Trim();

        // "SIP/2.0 200 OK" — skip the version, take the code.
        var afterVersion = line.IndexOf(' ');
        if (afterVersion < 0)
            return null;

        var rest = line[(afterVersion + 1)..].TrimStart();
        var afterCode = rest.IndexOf(' ');
        var codeSpan = afterCode < 0 ? rest : rest[..afterCode];

        return int.TryParse(codeSpan, out var code) && code is >= 100 and <= 699 ? code : null;
    }
}
