using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.Modules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Behavior tests for the generic module registry (package A2).
/// </summary>
public sealed class ModuleRegistryTests
{
    private static VoipConfiguration TestConfiguration() => new()
    {
        UserAgent = "CalloraVoipSdk.Client.Tests/1.0",
        EnableAutomaticAudioDeviceSelection = false,
    };

    [Fact]
    public void Get_returns_module_registered_programmatically()
    {
        using var client = new VoipClient(TestConfiguration());
        var module = new FakeFeatureModule();

        client.Modules.Register(module);

        Assert.Same(module, client.Modules.Get<IFakeFeature>());
        Assert.Same(module, client.Modules.Get<FakeFeatureModule>());
    }

    [Fact]
    public void Get_throws_documented_exception_when_module_missing()
    {
        using var client = new VoipClient(TestConfiguration());

        Assert.Throws<ModuleFeatureUnavailableException>(() => client.Modules.Get<IFakeFeature>());
    }

    [Fact]
    public void TryGet_returns_false_when_module_missing()
    {
        using var client = new VoipClient(TestConfiguration());

        Assert.False(client.Modules.TryGet<IFakeFeature>(out var module));
        Assert.Null(module);
    }

    [Fact]
    public void TryGet_returns_true_and_module_when_registered()
    {
        using var client = new VoipClient(TestConfiguration());
        var module = new FakeFeatureModule();
        client.Modules.Register(module);

        Assert.True(client.Modules.TryGet<IFakeFeature>(out var resolved));
        Assert.Same(module, resolved);
    }

    [Fact]
    public void Register_attaches_owning_client_to_module()
    {
        using var client = new VoipClient(TestConfiguration());
        var module = new FakeFeatureModule();

        client.Modules.Register(module);

        Assert.Same(client, module.AttachedClient);
    }

    [Fact]
    public void Modules_registered_in_di_are_resolvable_on_client()
    {
        var services = new ServiceCollection();
        services.AddCalloraVoip(options =>
        {
            options.UserAgent = "CalloraVoipSdk.Client.Tests/1.0";
            options.EnableAutomaticAudioDeviceSelection = false;
        });
        services.AddSingleton<IVoipClientModule, FakeFeatureModule>();

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IVoipClient>();

        var module = client.Modules.Get<IFakeFeature>();

        Assert.NotNull(module);
        Assert.Same(client, ((FakeFeatureModule)module).AttachedClient);
    }

    [Fact]
    public void Get_returns_first_registered_match_when_multiple_modules_satisfy_type()
    {
        using var client = new VoipClient(TestConfiguration());
        var first = new FakeFeatureModule();
        var second = new FakeFeatureModule();

        client.Modules.Register(first);
        client.Modules.Register(second);

        Assert.Same(first, client.Modules.Get<IFakeFeature>());
    }

    [Fact]
    public void Module_is_not_resolvable_before_attach_hook_completed()
    {
        using var client = new VoipClient(TestConfiguration());
        var module = new AttachProbeModule();

        client.Modules.Register(module);

        Assert.False(module.WasResolvableDuringAttach);
        Assert.True(client.Modules.TryGet<AttachProbeModule>(out _));
    }

    [Fact]
    public void Register_rejects_null_module()
    {
        using var client = new VoipClient(TestConfiguration());

        Assert.Throws<ArgumentNullException>(() => client.Modules.Register(null!));
    }

    [Fact]
    public async Task Parallel_resolution_is_thread_safe()
    {
        using var client = new VoipClient(TestConfiguration());
        client.Modules.Register(new FakeFeatureModule());

        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 250; n++)
            {
                if (i % 2 == 0)
                {
                    _ = client.Modules.Get<IFakeFeature>();
                }
                else
                {
                    Assert.True(client.Modules.TryGet<IFakeFeature>(out _));
                }
            }
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Parallel_registration_and_resolution_do_not_corrupt_registry()
    {
        using var client = new VoipClient(TestConfiguration());
        client.Modules.Register(new FakeFeatureModule());

        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 100; n++)
            {
                if (i % 4 == 0)
                {
                    client.Modules.Register(new OtherFeatureModule());
                }

                _ = client.Modules.Get<IFakeFeature>();
            }
        }));

        await Task.WhenAll(tasks);

        Assert.True(client.Modules.TryGet<IOtherFeature>(out _));
    }
}

/// <summary>Feature contract simulating a plugin-owned module interface.</summary>
public interface IFakeFeature
{
    /// <summary>Marker member.</summary>
    string Name { get; }
}

/// <summary>Second feature contract for multi-module scenarios.</summary>
public interface IOtherFeature;

/// <summary>Fake module capturing the attached client.</summary>
public sealed class FakeFeatureModule : IVoipClientModule, IFakeFeature
{
    /// <inheritdoc />
    public string ModuleId => "fake-feature";

    /// <inheritdoc />
    public string Name => "fake";

    /// <summary>Client passed via the attach hook.</summary>
    public IVoipClient? AttachedClient { get; private set; }

    /// <inheritdoc />
    public void OnAttached(IVoipClient client) => AttachedClient = client;
}

/// <summary>Fake module without attach-hook override.</summary>
public sealed class OtherFeatureModule : IVoipClientModule, IOtherFeature
{
    /// <inheritdoc />
    public string ModuleId => "other-feature";
}

/// <summary>Module probing its own visibility during the attach hook.</summary>
public sealed class AttachProbeModule : IVoipClientModule
{
    /// <inheritdoc />
    public string ModuleId => "attach-probe";

    /// <summary>True when the module could already be resolved while attaching.</summary>
    public bool WasResolvableDuringAttach { get; private set; }

    /// <inheritdoc />
    public void OnAttached(IVoipClient client) =>
        WasResolvableDuringAttach = client.Modules.TryGet<AttachProbeModule>(out _);
}
