using System.Globalization;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed record CalloraCapacityProfile(
    IReadOnlyList<int> Levels,
    int SetupParallelism,
    int Repetitions,
    TimeSpan ConnectTimeout,
    TimeSpan SettleWindow,
    TimeSpan MediaWindow,
    string ReportPath,
    long AsteriskOpenFileLimit,
    int MediaWorkers,
    bool ContinueAfterQualityFailure,
    CalloraCapacityQualityGate QualityGate)
{
    /// <summary>
    /// Benchmark-spezifischer SDK-Guard. Er entspricht der höchsten Messstufe, damit nicht Calloras
    /// produktiver Default von zehn Calls, sondern die konfigurierte Maschinenhülle gemessen wird.
    /// </summary>
    public int SdkCallLimit => Levels[^1];

    private const int DefaultStart = 64;
    private const int DefaultCeiling = 4096;
    private const int MaximumLevel = 4096;
    private const int FineGrainedThreshold = 1024;
    private const int FineGrainedStep = 256;

    public static CalloraCapacityProfile FromEnvironment()
    {
        var start = ReadPositiveInt("CALLORA_CAPACITY_START", DefaultStart, MaximumLevel);
        var ceiling = ReadPositiveInt("CALLORA_CAPACITY_CEILING", DefaultCeiling, MaximumLevel);
        if (ceiling < start)
        {
            throw new InvalidOperationException(
                "CALLORA_CAPACITY_CEILING must be greater than or equal to CALLORA_CAPACITY_START.");
        }

        var levels = ParseLevels(
            Environment.GetEnvironmentVariable("CALLORA_CAPACITY_LEVELS"),
            start,
            ceiling);
        var setupParallelism = ReadPositiveInt(
            "CALLORA_CAPACITY_SETUP_PARALLELISM",
            defaultValue: 8,
            maximum: 256);
        var repetitions = ReadPositiveInt(
            "CALLORA_CAPACITY_REPETITIONS",
            defaultValue: 1,
            maximum: 10);
        var settleSeconds = ReadPositiveInt(
            "CALLORA_CAPACITY_SETTLE_SECONDS",
            defaultValue: 10,
            maximum: 120);
        var mediaSeconds = ReadPositiveInt(
            "CALLORA_CAPACITY_MEDIA_SECONDS",
            defaultValue: 30,
            maximum: 600);
        var connectSeconds = ReadPositiveInt(
            "CALLORA_CAPACITY_CONNECT_SECONDS",
            defaultValue: 20,
            maximum: 120);
        var asteriskOpenFileLimit = ReadPositiveInt(
            "CALLORA_CAPACITY_ASTERISK_NOFILE",
            defaultValue: 65536,
            maximum: 1048576);
        var mediaWorkers = ReadPositiveInt(
            "CALLORA_CAPACITY_MEDIA_WORKERS",
            defaultValue: Environment.ProcessorCount,
            maximum: 256);
        var continueAfterQualityFailure = ReadBoolean(
            "CALLORA_CAPACITY_CONTINUE_AFTER_FAILURE",
            defaultValue: false);
        var reportPath = Environment.GetEnvironmentVariable("CALLORA_CAPACITY_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            reportPath = Path.Combine(
                Path.GetTempPath(),
                $"callora-capacity-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        }

        return new CalloraCapacityProfile(
            levels,
            setupParallelism,
            repetitions,
            TimeSpan.FromSeconds(connectSeconds),
            TimeSpan.FromSeconds(settleSeconds),
            TimeSpan.FromSeconds(mediaSeconds),
            Path.GetFullPath(reportPath),
            asteriskOpenFileLimit,
            mediaWorkers,
            continueAfterQualityFailure,
            CalloraCapacityQualityGate.FromEnvironment());
    }

    internal static IReadOnlyList<int> ParseLevels(string? raw, int start, int ceiling)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parsed = raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var level)
                    ? level
                    : throw new InvalidOperationException(
                        $"CALLORA_CAPACITY_LEVELS contains an invalid integer: '{value}'."))
                .ToArray();

            if (parsed.Length == 0 ||
                parsed.Any(level => level <= 0 || level > MaximumLevel) ||
                parsed.Distinct().Count() != parsed.Length ||
                !parsed.SequenceEqual(parsed.Order()))
            {
                throw new InvalidOperationException(
                    $"CALLORA_CAPACITY_LEVELS must contain unique ascending values from 1 to {MaximumLevel}.");
            }

            return parsed;
        }

        var levels = new List<int>();
        var level = start;
        while (level < ceiling)
        {
            levels.Add(level);
            var next = level < FineGrainedThreshold
                ? Math.Min(checked(level * 2), FineGrainedThreshold)
                : checked(level + FineGrainedStep);
            level = Math.Min(next, ceiling);
        }

        levels.Add(ceiling);

        return levels;
    }

    private static int ReadPositiveInt(string name, int defaultValue, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 ||
            value > maximum)
        {
            throw new InvalidOperationException(
                $"{name} must be an integer from 1 to {maximum}; actual value: '{raw}'.");
        }

        return value;
    }

    private static bool ReadBoolean(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!bool.TryParse(raw, out var value))
        {
            throw new InvalidOperationException(
                $"{name} must be 'true' or 'false'; actual value: '{raw}'.");
        }

        return value;
    }
}
