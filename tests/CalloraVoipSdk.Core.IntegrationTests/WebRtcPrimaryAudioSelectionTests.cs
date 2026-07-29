using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The primary (transport-anchor) audio m-line selection is chosen by ONE canonical rule
/// (<see cref="WebRtcSessionFactory.SelectAudioLines"/>) shared by the session factory's anchor + additional-skip
/// and by <see cref="WebRtcRemoteMediaInventory"/> (4.7.0 Slice 3 MINOR-fix). The prior divergence — the factory
/// anchoring on the first NON-disabled audio m-line while the additional-skip and the inventory used the first
/// audio m-line by index (ignoring a leading port-0/rejected one) — could double-count the anchor or mistake an
/// additional track for it. These tests pin the unified rule: a leading port-0 audio m-line is never the anchor,
/// and it is never surfaced as an additional inbound track.
/// </summary>
public sealed class WebRtcPrimaryAudioSelectionTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static SdpMediaDescription Audio(int port, string mid, SdpMediaDirection direction = SdpMediaDirection.SendRecv) =>
        new()
        {
            MediaType = "audio",
            Port = port,
            Profile = "UDP/TLS/RTP/SAVPF",
            Codecs = Pcmu,
            Direction = direction,
            Mid = mid,
        };

    [Fact]
    public void A_leading_port_zero_audio_m_line_is_not_the_anchor_and_the_next_non_disabled_one_is()
    {
        // First audio m-line is rejected (port 0); the second is the real transport anchor.
        var media = new[] { Audio(0, "0"), Audio(5000, "1"), Audio(5002, "2") };

        var (primary, additional) = WebRtcSessionFactory.SelectAudioLines(media);

        Assert.NotNull(primary);
        Assert.Equal("1", primary!.Mid); // the first NON-disabled audio m-line is the anchor, not index 0
        // Every OTHER audio m-line — including the leading disabled one — is additional (the anchor is excluded).
        Assert.Equal(new[] { "0", "2" }, additional.Select(m => m.Mid));
        Assert.DoesNotContain(additional, m => ReferenceEquals(m, primary)); // the anchor is never also additional
    }

    [Fact]
    public void The_inventory_agrees_with_the_factory_when_the_first_audio_m_line_is_rejected()
    {
        // Same shape as the factory sees it: a rejected leading audio m-line, a sending anchor "1", and a sending
        // additional track "2". The inventory must treat "1" as the anchor (surfaced via the mid-less path, so NOT
        // an additional track) and "2" as the one additional inbound audio track — never the disabled "0".
        var remote = new SdpSessionDescription
        {
            OriginAddress = "127.0.0.1",
            ConnectionAddress = "127.0.0.1",
            Media = [Audio(0, "0"), Audio(5000, "1"), Audio(5002, "2")],
        };

        var inventory = WebRtcRemoteMediaInventory.FromRemoteDescription(remote);

        Assert.True(inventory.HasRemoteAudio); // the sending anchor "1" makes the remote an audio sender
        var additional = Assert.Single(inventory.AudioTracks); // exactly one additional track ("2"), not "0" nor "1"
        Assert.Equal("2", additional.Mid);
    }

    [Fact]
    public void A_single_non_disabled_audio_m_line_yields_no_additional_tracks()
    {
        // Byte-identity guard: the ordinary one-audio shape still selects that m-line as the anchor and produces no
        // additional tracks (the anchor is excluded from the additional list).
        var media = new[] { Audio(5000, "0") };

        var (primary, additional) = WebRtcSessionFactory.SelectAudioLines(media);

        Assert.Equal("0", primary!.Mid);
        Assert.Empty(additional);
    }
}
