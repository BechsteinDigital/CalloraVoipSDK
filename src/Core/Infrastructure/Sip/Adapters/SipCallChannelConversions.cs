using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Pure SIP↔domain conversions used by <see cref="SipCoreCallChannel"/>: dialog-state
/// mapping and DTMF tone-code to symbol. Extracted to keep the channel adapter focused
/// and within the file-size budget.
/// </summary>
internal static class SipCallChannelConversions
{
    /// <summary>
    /// Maps a SIP dialog state to the domain call state, or <see langword="null"/> for
    /// states that do not surface as a call-state transition.
    /// </summary>
    public static CallState? MapState(SipDialogState state) => state switch
    {
        SipDialogState.Inviting     => CallState.Dialing,
        SipDialogState.Ringing      => CallState.Ringing,
        SipDialogState.Established  => CallState.Connected,
        SipDialogState.OnHold       => CallState.OnHold,
        SipDialogState.Terminated   => CallState.Terminated,
        _                           => null
    };

    /// <summary>
    /// Maps a DTMF tone code (0–15, RFC 4733 events) to its symbol; throws for out-of-range codes.
    /// </summary>
    public static char ToDtmfSymbol(byte toneCode) => toneCode switch
    {
        <= 9 => (char)('0' + toneCode),
        10   => '*',
        11   => '#',
        12   => 'A',
        13   => 'B',
        14   => 'C',
        15   => 'D',
        _    => throw new ArgumentOutOfRangeException(nameof(toneCode))
    };
}
