using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Holds one adaptive jitter buffer per inbound audio m-line and releases packets on a steady playout
/// cadence instead of the moment they arrive off the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the receive path needs this at all.</b> A peer that only forwards does not: it hands packets
/// on and the receiving browser's own jitter buffer (NetEQ) absorbs the network. A consumer that
/// <em>mixes</em> is in the opposite position — it must produce one frame every frame interval, from
/// whatever each source has contributed by then, and it cannot wait. Handed raw arrivals, it sees a
/// burst as one usable frame and the rest as nothing, and the far end hears audio that stops after
/// every pause and returns a few seconds later. Opus DTX makes that the normal case rather than the
/// exception: a browser sends nothing while nobody speaks, and the packets that follow the silence
/// arrive together.
/// </para>
/// <para>
/// This is the same shape the SIP path has used all along
/// (<see cref="RtpCallMediaSession"/> buffers arrivals and drains them from a playout loop), and the
/// same one Janus arrived at for its mixing plugin: put the packet in the jitter buffer on arrival,
/// and decode from the buffer on the participant's own cadence.
/// </para>
/// <para>
/// <b>Opt-in on purpose.</b> Buffering costs latency — the initial delay plus whatever the adaptive
/// controller settles on — and a forwarding consumer pays it for nothing, because the browser at the
/// far end buffers anyway. Only a consumer that mixes, transcodes or otherwise needs a steady cadence
/// should ask for it.
/// </para>
/// <para>
/// <b>No concealment here.</b> A gap is passed on as a gap rather than filled by repeating the last
/// payload. This layer carries encoded RTP and does not know the codec; repeating an Opus frame
/// produces an artefact, while a consumer that decodes can ask its decoder for proper packet-loss
/// concealment. The SIP path conceals because it delivers into a G.711 stream, where repetition is
/// benign and a silent gap is not.
/// </para>
/// </remarks>
internal sealed class BundledAudioJitterBuffers : IAsyncDisposable
{
    private readonly Dictionary<string, AudioJitterBufferEntry> _buffers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Action<string, RtpPacket> _release;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _playout;

    /// <summary>
    /// Starts the playout loop over the given tracks.
    /// </summary>
    /// <param name="tracks">The inbound audio m-lines to buffer, with the clock rate each one uses.</param>
    /// <param name="playoutInterval">How often to release what has come due — the audio frame interval.</param>
    /// <param name="initialDelayMs">Starting playout delay; the buffer adapts from here.</param>
    /// <param name="release">Called for each packet whose playout time has arrived, tagged with its m-line.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public BundledAudioJitterBuffers(
        IReadOnlyList<BundledTrackConfig> tracks,
        TimeSpan playoutInterval,
        int initialDelayMs,
        Action<string, RtpPacket> release,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(logger);

        _release = release;
        _logger = logger;

        foreach (var track in tracks)
        {
            // Per track, because the clock rate is the track's: an Opus m-line runs at 48 kHz and a
            // G.711 one at 8 kHz, and the buffer converts RTP timestamps to playout instants with it.
            // One shared rate would schedule one of the two six times too early or too late.
            _buffers[track.Mid] = new AudioJitterBufferEntry(
                new JitterBuffer.JitterBuffer(new JitterBufferOptions
                {
                    ClockRate = track.ClockRate > 0 ? track.ClockRate : 8000,
                    InitialDelayMs = initialDelayMs,
                }));
        }

        _playout = RunPlayoutLoopAsync(playoutInterval, _stopping.Token);
    }

    /// <summary>
    /// Builds the buffers for a session that asked for inbound audio pacing, or returns
    /// <see langword="null"/> for one that did not — the common case, and the one that must stay free
    /// of both the latency and the machinery.
    /// </summary>
    public static BundledAudioJitterBuffers? CreateIfRequested(
        BundledMediaSessionOptions options,
        TimeSpan playoutInterval,
        Action<string, RtpPacket> release,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AudioReceivePlayoutDelayMs <= 0)
        {
            return null;
        }

        // The primary audio m-line is the transport anchor and is not in AdditionalAudioTracks, so it
        // has to be added by hand — leaving it out would buffer every track except the one a two-party
        // call actually uses.
        var tracks = new List<BundledTrackConfig>(options.AdditionalAudioTracks.Count + 1) { options.Audio };
        tracks.AddRange(options.AdditionalAudioTracks);

        return new BundledAudioJitterBuffers(
            tracks, playoutInterval, options.AudioReceivePlayoutDelayMs, release, logger);
    }

    /// <summary>
    /// Takes one arriving packet. Returns <see langword="false"/> for an m-line this was not built for,
    /// so the caller can pass it straight through rather than dropping it.
    /// </summary>
    public bool TryAdd(string mid, RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        AudioJitterBufferEntry entry;
        lock (_gate)
        {
            if (!_buffers.TryGetValue(mid, out var found))
            {
                return false;
            }

            entry = found;

            // A new synchronisation source brings its own sequence and timestamp space, which has no
            // relation to the previous one. Without the reset every packet of the new stream reads as
            // wildly out of order and is discarded until the numbering happens to catch up.
            if (entry.Ssrc != packet.Ssrc)
            {
                if (entry.Ssrc is not null)
                {
                    _logger.LogDebug(
                        "Audio jitter buffer for MID '{Mid}': source changed from {Old} to {New}; resetting.",
                        mid,
                        entry.Ssrc,
                        packet.Ssrc);
                }

                entry.Buffer.Reset();
                entry.Ssrc = packet.Ssrc;
            }
        }

        // Added outside the dictionary lock: the buffer has its own, and holding two is how two objects
        // that each behave deadlock together. Arrival and playout must read the same jump-free clock, so
        // both use MonotonicClock — a wall-clock step mid-call would otherwise corrupt the schedule.
        entry.Buffer.Add(packet, MonotonicClock.Now);
        return true;
    }

    /// <summary>Feeds the RTT hint the adaptive delay controller uses, for every buffered track.</summary>
    public void UpdateRoundTripTime(double roundTripTimeMs)
    {
        AudioJitterBufferEntry[] entries;
        lock (_gate)
        {
            entries = [.. _buffers.Values];
        }

        foreach (var entry in entries)
        {
            entry.Buffer.UpdateRoundTripTime(roundTripTimeMs);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _playout.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // The ordinary way the loop ends, and said rather than swallowed: a cancellation that shows
            // up anywhere other than teardown is worth seeing in a trace.
            _logger.LogTrace(ex, "Audio jitter buffer playout loop ended by cancellation.");
        }

        _stopping.Dispose();
    }

    private async Task RunPlayoutLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            AudioJitterBufferEntry[] entries;
            string[] mids;
            lock (_gate)
            {
                mids = [.. _buffers.Keys];
                entries = [.. _buffers.Values];
            }

            for (var i = 0; i < entries.Length; i++)
            {
                DrainDuePackets(mids[i], entries[i]);
            }
        }
    }

    private void DrainDuePackets(string mid, AudioJitterBufferEntry entry)
    {
        // Drained in a loop rather than one per tick: a burst that arrived together is due together,
        // and releasing one per interval would turn the buffer into the very backlog it exists to undo.
        while (true)
        {
            var packet = entry.Buffer.TryGetNext(MonotonicClock.Now);
            if (packet is null)
            {
                return;
            }

            try
            {
                _release(mid, packet);
            }
            catch (Exception ex)
            {
                // One misbehaving subscriber must not end the playout loop for every other track on
                // this session — the rest of the room would fall silent with it.
                _logger.LogError(ex, "Releasing a buffered audio packet for MID '{Mid}' failed.", mid);
            }
        }
    }
}
