namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed class NoopDisposable : IDisposable
{
    public static readonly NoopDisposable Instance = new();

    public void Dispose()
    {
    }
}
