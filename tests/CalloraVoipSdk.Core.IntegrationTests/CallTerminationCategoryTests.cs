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
        // Normal completion (no failure status): null BYE, provisional, success, redirect.
        { null, CallTerminationCategory.Completed },
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

        // Rejected (decline / forbidden / auth challenge).
        { 603, CallTerminationCategory.Rejected },
        { 403, CallTerminationCategory.Rejected },
        { 401, CallTerminationCategory.Rejected },
        { 407, CallTerminationCategory.Rejected },

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
        Assert.Equal(expected, CallTerminationReason.CategoryForSipStatus(statusCode));
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
