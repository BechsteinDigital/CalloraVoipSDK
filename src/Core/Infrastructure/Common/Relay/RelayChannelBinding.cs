namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// The result of binding a relay channel to the nominated peer (RFC 8656 §11 ChannelBind): the bound data-path
/// channel the media transport switches onto, plus the optional rebind keepalive that re-issues ChannelBind at
/// half the channel lifetime (§12) so the binding does not lapse under a long-lived relayed session. The media
/// session installs the channel, starts the rebind, and disposes the rebind — before the transport it rides — on
/// teardown.
/// </summary>
/// <param name="Channel">The bound relay data-path channel (RFC 8656 ChannelData framing).</param>
/// <param name="Rebind">
/// The channel rebind keepalive (RFC 8656 §12), or <see langword="null"/> when the producer supplies none.
/// </param>
internal sealed record RelayChannelBinding(
    IRelayDatagramChannel Channel,
    IRelayKeepAlive? Rebind = null);
