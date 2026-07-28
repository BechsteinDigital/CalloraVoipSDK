using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed class CalloraCapacityCallTracker
{
    private long _windowStartTicks;
    private long _windowEndTicks;
    private int _notConnectedDuringWindow;
    private int _armed;

    public CalloraCapacityCallTracker(TimeSpan expectedFrameInterval)
    {
        Outbound = new CalloraCapacityDirectionTracker(expectedFrameInterval);
        Inbound = new CalloraCapacityDirectionTracker(expectedFrameInterval);
    }

    public CalloraCapacityDirectionTracker Outbound { get; }

    public CalloraCapacityDirectionTracker Inbound { get; }

    public void Arm(
        long windowStartTicks,
        long windowEndTicks,
        DateTimeOffset windowStartAtUtc,
        CallState state)
    {
        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _windowStartTicks, windowStartTicks);
        Volatile.Write(ref _windowEndTicks, windowEndTicks);
        Volatile.Write(
            ref _notConnectedDuringWindow,
            state == CallState.Connected ? 0 : 1);
        Outbound.Arm(windowStartTicks, windowEndTicks, windowStartAtUtc);
        Inbound.Arm(windowStartTicks, windowEndTicks, windowStartAtUtc);
        Volatile.Write(ref _armed, 1);
    }

    public void ObserveState(CallState state, long timestamp)
    {
        if (state == CallState.Connected || Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        if (timestamp >= Volatile.Read(ref _windowStartTicks) &&
            timestamp <= Volatile.Read(ref _windowEndTicks))
        {
            Volatile.Write(ref _notConnectedDuringWindow, 1);
        }
    }

    public bool ConnectedThroughoutWindow
    {
        get
        {
            Volatile.Write(ref _armed, 0);
            return Volatile.Read(ref _notConnectedDuringWindow) == 0;
        }
    }
}
