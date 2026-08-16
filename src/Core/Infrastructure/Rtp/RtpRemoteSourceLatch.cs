using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Binds one single-stream RTP leg to one remote synchronisation source (#161 P2-6).
/// <para>
/// The inbound processor tracks up to 64 SSRCs with a sequence validator each, but everything downstream of it
/// — the jitter buffer, the playout cursor, the concealment state, the RFC 3550 §A.1/§A.3 receiver-report
/// bookkeeping — is single-stream. Two sources arriving at once therefore interleave two sequence and timestamp
/// spaces in one buffer: the playout schedule and the jitter estimate become meaningless, every alternation
/// looks like massive loss, and the receiver report restarts on each flip.
/// </para>
/// <para>
/// So the first source is latched and the rest are dropped and counted. A genuine source change still has to
/// work — a media server switching legs mid-call, or a peer reseeding after an SSRC collision (RFC 3550 §8.2) —
/// so a new source takes over once it has delivered <see cref="TakeoverPackets"/> consecutive packets AND the
/// latched source has been silent for <see cref="TakeoverIdle"/>. Both conditions together, following pjmedia's
/// consecutive-packet source-change detection: a single injected packet never wins, and any packet from the
/// latched source resets the candidate's streak.
/// </para>
/// </summary>
/// <remarks>
/// Not thread-safe by design: <see cref="Admit"/> is confined to the single RTP receive-loop thread, like the
/// stream state it guards. The drop counter is the one exception — it is read from other threads.
/// </remarks>
internal sealed class RtpRemoteSourceLatch
{
    /// <summary>
    /// Consecutive packets a new source must deliver before it can take over. At the usual 20 ms audio cadence
    /// this is 200 ms of an uninterrupted new stream; a single injected packet, or one interleaved with the
    /// latched source, never reaches it.
    /// </summary>
    public const int TakeoverPackets = 10;

    /// <summary>
    /// How long the latched source must have been silent before another one may take over. Long enough to
    /// outlast an ordinary jitter/reordering gap, short enough that a genuine mid-call source change costs a
    /// fraction of a second.
    /// </summary>
    public static readonly TimeSpan TakeoverIdle = TimeSpan.FromMilliseconds(500);

    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;

    private bool _hasSource;
    private uint _source;
    private DateTimeOffset _lastSourceActivity;
    private uint _candidate;
    private int _candidateStreak;
    private long _dropped;

    /// <param name="logger">Logs the first drop and every source change.</param>
    /// <param name="clock">Monotonic clock; injectable so the takeover window is testable without waiting.</param>
    public RtpRemoteSourceLatch(ILogger logger, Func<DateTimeOffset>? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? (() => MonotonicClock.Now);
    }

    /// <summary>Packets dropped because they came from a source this leg is not latched to.</summary>
    public long DroppedPackets => Interlocked.Read(ref _dropped);

    /// <summary>The currently latched source, or <see langword="null"/> before the first packet.</summary>
    public uint? LatchedSource => _hasSource ? _source : null;

    /// <summary>
    /// Decides whether a packet's source may drive this leg. Returns <see langword="true"/> for the latched
    /// source (and for the first packet, which latches it); <see langword="false"/> for any other source until
    /// it earns a takeover. <paramref name="sourceChanged"/> is set on the packet that completes a takeover —
    /// the caller must then reset its stream state, since the new source brings its own sequence and timestamp
    /// space.
    /// </summary>
    public bool Admit(uint ssrc, out bool sourceChanged)
    {
        sourceChanged = false;
        var now = _clock();

        if (!_hasSource)
        {
            _source = ssrc;
            _hasSource = true;
            _lastSourceActivity = now;
            return true;
        }

        if (_source == ssrc)
        {
            _lastSourceActivity = now;
            _candidateStreak = 0;
            return true;
        }

        _candidateStreak = _candidate == ssrc ? _candidateStreak + 1 : 1;
        _candidate = ssrc;

        if (_candidateStreak >= TakeoverPackets && now - _lastSourceActivity >= TakeoverIdle)
        {
            _logger.LogInformation(
                "Inbound RTP source changed from SSRC {PreviousSsrc} to {NewSsrc}; stream state reset.",
                _source, ssrc);
            _source = ssrc;
            _lastSourceActivity = now;
            _candidateStreak = 0;
            sourceChanged = true;
            return true;
        }

        if (Interlocked.Increment(ref _dropped) == 1)
        {
            _logger.LogDebug(
                "Dropping inbound RTP from SSRC {ForeignSsrc}: this leg is latched to SSRC {LatchedSsrc}. " +
                "Further drops are counted, not logged.", ssrc, _source);
        }

        return false;
    }
}
