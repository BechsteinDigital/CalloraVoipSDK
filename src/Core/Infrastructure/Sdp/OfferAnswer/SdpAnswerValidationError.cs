namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// One RFC 3264 §6 violation found in a remote answer: what was wrong, where, and a message for the log.
/// </summary>
/// <param name="Violation">The typed reason, so a caller can react rather than only report.</param>
/// <param name="MediaSectionIndex">The offending m-line index, or <see langword="null"/> for a session-level violation.</param>
/// <param name="Message">Human-readable detail, including the offered and answered values.</param>
internal sealed record SdpAnswerValidationError(
    SdpAnswerViolation Violation,
    int? MediaSectionIndex,
    string Message)
{
    /// <inheritdoc />
    public override string ToString() => Message;
}
