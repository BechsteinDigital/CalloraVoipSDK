using System.Reflection;
using CalloraVoipSdk.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Drift guard for the <see cref="SdkOptions"/> → <see cref="SdkConfiguration"/> mapping (HARD-G-follow-up,
/// Client F2/F3). The mapping claims "every configurable option is carried across"; these reflection
/// tests fail the day a new option is added without a matching configuration property or without the
/// mapping copying it — the silent-default trap the explicit mapping test cannot catch for future fields.
/// </summary>
public sealed class SdkOptionsMappingCompletenessTests
{
    // LoggerFactory is intentionally sourced from the resolved factory argument, not from the options
    // instance; the value sweep also skips the non-1:1 fields (nested Ice, defaulted AudioDevice, and
    // the reference types the sweep does not synthesize) — those are covered by SdkOptionsMappingTests.
    private static readonly HashSet<string> SweepExclusions = new(StringComparer.Ordinal)
    {
        nameof(SdkOptions.LoggerFactory),
        nameof(SdkOptions.Ice),
        nameof(SdkOptions.AudioDevice),
        nameof(SdkOptions.Tls),
        nameof(SdkOptions.DtlsCertificate),
        nameof(SdkOptions.PreferredAudioCodecs),
        nameof(SdkOptions.PreferredVideoCodecs),
    };

    [Fact]
    public void Every_option_has_a_matching_configuration_property()
    {
        var configProps = typeof(SdkConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = typeof(SdkOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(SdkOptions.LoggerFactory))
            .Select(p => p.Name)
            .Where(name => !configProps.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"SdkOptions properties without a matching SdkConfiguration property (mapping drift): {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_scalar_option_is_carried_onto_the_configuration()
    {
        var options = new SdkOptions();
        var swept = new List<PropertyInfo>();

        foreach (var prop in typeof(SdkOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (SweepExclusions.Contains(prop.Name) || !prop.CanWrite)
                continue;

            var nonDefault = NonDefaultValue(prop.PropertyType, prop.GetValue(options));
            if (nonDefault is null)
                continue; // a type the sweep does not synthesize — left to the explicit mapping test

            prop.SetValue(options, nonDefault);
            swept.Add(prop);
        }

        var config = options.ToConfiguration(null);

        Assert.NotEmpty(swept);
        foreach (var prop in swept)
        {
            var configProp = typeof(SdkConfiguration).GetProperty(prop.Name);
            Assert.NotNull(configProp);
            Assert.Equal(prop.GetValue(options), configProp!.GetValue(config));
        }
    }

    // Returns a value distinct from the property's current default, or null for types not synthesized.
    private static object? NonDefaultValue(Type type, object? current)
    {
        if (type == typeof(string))
            return "drift-sweep";
        if (type == typeof(bool))
            return !(bool)(current ?? false);
        if (type == typeof(int))
            return (int)(current ?? 0) + 7;
        if (type == typeof(TimeSpan))
            return (TimeSpan)(current ?? TimeSpan.Zero) + TimeSpan.FromSeconds(11);
        if (type.IsEnum)
            return Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current));
        return null;
    }
}
