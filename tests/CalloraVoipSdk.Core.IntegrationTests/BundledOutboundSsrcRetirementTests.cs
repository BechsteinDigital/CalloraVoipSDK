using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound SSRCs are retired, never released (#161 P2-12). A bundle protects every stream with one shared
/// SRTP context under one DTLS-derived master key, and that context keys its per-SSRC state (rollover counter,
/// replay window) by SSRC for the lifetime of the key. Handing a deactivated track's SSRC to a new track would
/// restart that stream's sequence numbering under the same key — SRTP derives its keystream from
/// (SSRC, ROC‖SEQ), so the two streams would share a keystream. The tracker therefore keeps a deactivated
/// track's SSRCs out of every later allocation.
/// </summary>
public sealed class BundledOutboundSsrcRetirementTests
{
    private const uint AudioSsrc = 0x0A0A_0A0A;

    private static BundledTrackConfig Video(string mid, uint ssrc, uint? rtxSsrc = null) => new()
    {
        Mid = mid,
        Ssrc = ssrc,
        PayloadType = 96,
        VideoCodecName = "H264",
        RtxSsrc = rtxSsrc,
        RtxPayloadType = rtxSsrc is null ? null : (byte)98,
    };

    [Fact]
    public void A_deactivated_tracks_ssrcs_stay_out_of_later_allocations()
    {
        var tracker = new BundledOutboundSsrcTracker(AudioSsrc, NullLogger.Instance);
        tracker.Add("vid2", Video("vid2", 0x0B0B_0B0B, rtxSsrc: 0x0C0C_0C0C));

        tracker.Remove("vid2");

        var snapshot = tracker.Snapshot();
        Assert.Contains(0x0B0B_0B0Bu, snapshot); // the primary SSRC is retired, not released
        Assert.Contains(0x0C0C_0C0Cu, snapshot); // and so is the RTX repair SSRC
        Assert.Contains(AudioSsrc, snapshot);
    }

    [Fact]
    public void Every_simulcast_encoding_is_retired_too()
    {
        var tracker = new BundledOutboundSsrcTracker(AudioSsrc, NullLogger.Instance);
        var simulcast = Video("vid2", 0x0B0B_0B0B) with
        {
            Encodings =
            [
                new BundledVideoEncoding { Rid = "h", Ssrc = 0x0B0B_0B0C },
                new BundledVideoEncoding { Rid = "l", Ssrc = 0x0B0B_0B0D },
            ],
        };
        tracker.Add("vid2", simulcast);

        tracker.Remove("vid2");

        var snapshot = tracker.Snapshot();
        Assert.Contains(0x0B0B_0B0Cu, snapshot);
        Assert.Contains(0x0B0B_0B0Du, snapshot);
    }

    [Fact]
    public void Replacing_a_mids_entry_retires_the_ssrcs_it_held()
    {
        var tracker = new BundledOutboundSsrcTracker(AudioSsrc, NullLogger.Instance);
        tracker.Add("vid2", Video("vid2", 0x0B0B_0B0B));

        // A re-add without a deactivate in between must not drop the previous SSRCs either.
        tracker.Add("vid2", Video("vid2", 0x0D0D_0D0D));

        var snapshot = tracker.Snapshot();
        Assert.Contains(0x0B0B_0B0Bu, snapshot);
        Assert.Contains(0x0D0D_0D0Du, snapshot);
    }

    [Fact]
    public void Retiring_the_same_mid_twice_is_idempotent()
    {
        var tracker = new BundledOutboundSsrcTracker(AudioSsrc, NullLogger.Instance);
        tracker.Add("vid2", Video("vid2", 0x0B0B_0B0B));

        tracker.Remove("vid2");
        tracker.Remove("vid2");

        Assert.Equal(new HashSet<uint> { AudioSsrc, 0x0B0B_0B0B }, tracker.Snapshot());
    }
}
