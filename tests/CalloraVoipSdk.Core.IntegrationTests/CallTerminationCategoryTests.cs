using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L0/L1 unit coverage for the domain SIP-status → <see cref="CallTerminationCategory"/> mapping
/// (RFC 3261 §21). Each terminating status resolves to a stable protocol-neutral category so callers
/// can branch on the outcome without parsing SIP.
/// </summary>
public sealed class CallTerminationCategoryTests
{
    public static TheoryData<int?, CallTerminationCategory> Cases => new()
    {
        // Provisional (1xx), success (2xx), redirect (3xx) → non-failure completion. The null case is
        // covered separately (it depends on the connected-gate) in NullStatus_* below.
        { 100, CallTerminationCategory.Completed },
        { 180, CallTerminationCategory.Completed },
        { 200, CallTerminationCategory.Completed },
        { 302, CallTerminationCategory.Completed },

        // Busy (RFC 3261 §21.5.6 / §21.6.1).
        { 486, CallTerminationCategory.Busy },
        { 600, CallTerminationCategory.Busy },

        // No answer (RFC 3261 §21.4.7 / §21.5.2).
        { 408, CallTerminationCategory.NoAnswer },
        { 480, CallTerminationCategory.NoAnswer },

        // Canceled (RFC 3261 §21.4.26).
        { 487, CallTerminationCategory.Canceled },

        // Rejected: an active decline / refusal only (RFC 3261 §21.6.2 / §21.4.4).
        { 603, CallTerminationCategory.Rejected },
        { 403, CallTerminationCategory.Rejected },

        // Auth challenges (RFC 3261 §21.4.2 / §21.5.5) are technical failures, not an active decline.
        { 401, CallTerminationCategory.Failed },
        { 407, CallTerminationCategory.Failed },

        // Any other 4xx/5xx/6xx → Failed.
        { 400, CallTerminationCategory.Failed },
        { 404, CallTerminationCategory.Failed },
        { 500, CallTerminationCategory.Failed },
        { 503, CallTerminationCategory.Failed },
        { 606, CallTerminationCategory.Failed },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void CategoryForSipStatus_maps_each_status_to_the_expected_category(
        int? statusCode,
        CallTerminationCategory expected)
    {
        // A non-null status is classified purely from the status; the connected-gate is irrelevant, so
        // both wasConnected values must yield the same category.
        Assert.Equal(expected, CallTerminationReason.CategoryForSipStatus(statusCode, wasConnected: true));
        Assert.Equal(expected, CallTerminationReason.CategoryForSipStatus(statusCode, wasConnected: false));
    }

    [Fact]
    public void NullStatus_after_connected_is_Completed()
    {
        // A graceful teardown with no SIP failure status, after the call was up, is a normal completion.
        Assert.Equal(
            CallTerminationCategory.Completed,
            CallTerminationReason.CategoryForSipStatus(null, wasConnected: true));
    }

    [Fact]
    public void NullStatus_never_connected_is_Failed()
    {
        // A null-status abort that never connected carries no SIP failure signal but is not a completion:
        // it is a technical failure (matching Twilio `failed` / Ozeki `Error`), not a false Completed.
        Assert.Equal(
            CallTerminationCategory.Failed,
            CallTerminationReason.CategoryForSipStatus(null, wasConnected: false));
    }

    [Fact]
    public void NullStatus_defaults_to_connected_Completed()
    {
        // The wasConnected default preserves the historical Completed for a plain null status.
        Assert.Equal(
            CallTerminationCategory.Completed,
            CallTerminationReason.CategoryForSipStatus(null));
    }

    [Fact]
    public void CallTerminationReason_defaults_are_null_and_completed_local()
    {
        var reason = new CallTerminationReason();

        Assert.Null(reason.SipStatusCode);
        Assert.Null(reason.ReasonPhrase);
        Assert.Null(reason.RetryAfterSeconds);
        Assert.Equal(CallTerminationCategory.Completed, reason.Category);
        Assert.Equal(CallTerminatedBy.Local, reason.TerminatedBy);
    }
}
