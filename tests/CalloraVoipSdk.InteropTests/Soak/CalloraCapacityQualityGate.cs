using System.Globalization;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed record CalloraCapacityQualityGate(
    double MinimumDeliveryRatio,
    double MaximumP99IntervalMilliseconds,
    double MaximumSilenceMilliseconds,
    double MaximumPacketLossRatio,
    double MaximumJitterMilliseconds)
{
    internal const double DefaultMinimumDeliveryRatio = 0.99;
    internal const double DefaultMaximumP99IntervalMilliseconds = 40;
    internal const double DefaultMaximumSilenceMilliseconds = 250;
    internal const double DefaultMaximumPacketLossRatio = 0.01;
    internal const double DefaultMaximumJitterMilliseconds = 30;

    public static CalloraCapacityQualityGate FromEnvironment() =>
        new(
            ReadRatio(
                "CALLORA_CAPACITY_MIN_DELIVERY_RATIO",
                DefaultMinimumDeliveryRatio),
            ReadPositiveDouble(
                "CALLORA_CAPACITY_MAX_P99_INTERVAL_MS",
                DefaultMaximumP99IntervalMilliseconds),
            ReadPositiveDouble(
                "CALLORA_CAPACITY_MAX_SILENCE_MS",
                DefaultMaximumSilenceMilliseconds),
            ReadRatio(
                "CALLORA_CAPACITY_MAX_PACKET_LOSS_RATIO",
                DefaultMaximumPacketLossRatio),
            ReadPositiveDouble(
                "CALLORA_CAPACITY_MAX_JITTER_MS",
                DefaultMaximumJitterMilliseconds));

    private static double ReadRatio(string name, double defaultValue)
    {
        var value = ReadDouble(name, defaultValue);
        if (value is < 0 or > 1)
        {
            throw new InvalidOperationException($"{name} must be a number from 0 to 1.");
        }

        return value;
    }

    private static double ReadPositiveDouble(string name, double defaultValue)
    {
        var value = ReadDouble(name, defaultValue);
        if (value <= 0)
        {
            throw new InvalidOperationException($"{name} must be greater than zero.");
        }

        return value;
    }

    private static double ReadDouble(string name, double defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) ||
            !double.IsFinite(value))
        {
            throw new InvalidOperationException(
                $"{name} must be a finite invariant-culture number; actual value: '{raw}'.");
        }

        return value;
    }
}
