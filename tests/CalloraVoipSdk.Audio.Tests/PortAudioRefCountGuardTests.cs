using CalloraVoipSdk.Audio.Linux;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// The PortAudio reference-count guard (issue #18, A7). Each acquire must pair with exactly one
/// release; initialize runs only on the first acquire, terminate only when the last release brings
/// the count back to zero — so nested acquisitions (a live device plus a concurrent enumeration)
/// never leave the backend initialized after everyone is done.
/// </summary>
public sealed class PortAudioRefCountGuardTests
{
    [Fact]
    public void First_acquire_initializes_and_last_release_terminates_exactly_once()
    {
        var inits = 0;
        var terms = 0;
        var guard = new PortAudioRefCountGuard(() => inits++, () => terms++);

        guard.Acquire();
        Assert.Equal(1, inits);
        Assert.Equal(0, terms);
        Assert.Equal(1, guard.Count);

        guard.Release();
        Assert.Equal(1, inits);
        Assert.Equal(1, terms);
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public void Nested_acquisitions_initialize_once_and_terminate_only_at_the_final_release()
    {
        var inits = 0;
        var terms = 0;
        var guard = new PortAudioRefCountGuard(() => inits++, () => terms++);

        guard.Acquire(); // device lifetime
        guard.Acquire(); // enumeration
        guard.Acquire(); // second enumeration

        Assert.Equal(1, inits);
        Assert.Equal(3, guard.Count);

        guard.Release();
        guard.Release();
        Assert.Equal(0, terms); // still one outstanding

        guard.Release();
        Assert.Equal(1, terms);
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public void Balanced_reacquire_reinitializes_after_a_full_release()
    {
        var inits = 0;
        var terms = 0;
        var guard = new PortAudioRefCountGuard(() => inits++, () => terms++);

        guard.Acquire();
        guard.Release();
        guard.Acquire();
        guard.Release();

        Assert.Equal(2, inits);
        Assert.Equal(2, terms);
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public void Release_without_a_matching_acquire_is_a_no_op()
    {
        var terms = 0;
        var guard = new PortAudioRefCountGuard(() => { }, () => terms++);

        guard.Release();

        Assert.Equal(0, terms);
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public void Null_actions_throw()
    {
        Assert.Throws<ArgumentNullException>(() => new PortAudioRefCountGuard(null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new PortAudioRefCountGuard(() => { }, null!));
    }
}
