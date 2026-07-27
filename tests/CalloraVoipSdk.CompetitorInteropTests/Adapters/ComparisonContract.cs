namespace MiniCore.Compare.Interop.Adapters;

public enum StackKind
{
    Callora,
    SipSorcery,
    Ozeki,
}

public enum DialAttemptStatus
{
    Connected,
    Timeout,
    Canceled,
    Failed,
}

public sealed record SipTestAccount(
    string Server,
    int Port,
    string Username,
    string Password);

public sealed record DialAttempt(
    DialAttemptStatus Status,
    IComparisonCall? Call = null,
    string? Detail = null);

public interface IComparisonStack : IAsyncDisposable
{
    string Name { get; }

    bool IsRegistered { get; }

    int ActiveCallCount { get; }

    Task RegisterAsync(SipTestAccount account, CancellationToken ct = default);

    Task<DialAttempt> DialAsync(
        string targetUri,
        TimeSpan connectTimeout,
        CancellationToken ct = default);

    Task<IComparisonCall> WaitForIncomingAndAnswerAsync(
        TimeSpan timeout,
        CancellationToken ct = default);

    IComparisonBridge Bridge(IComparisonCall left, IComparisonCall right);
}

public interface IComparisonCall : IAsyncDisposable
{
    bool IsConnected { get; }

    bool IsOnHold { get; }

    long ReceivedPacketCount { get; }

    string ReceivedDtmf { get; }

    Task HoldAsync(CancellationToken ct = default);

    Task UnholdAsync(CancellationToken ct = default);

    Task PlayWavAsync(string path, CancellationToken ct = default);

    Task<IComparisonRecording> StartWavRecordingAsync(
        string outputDirectory,
        CancellationToken ct = default);

    Task HangupAsync(CancellationToken ct = default);
}

public interface IComparisonRecording : IAsyncDisposable
{
    IReadOnlyList<string> OutputFiles { get; }

    Task StopAsync(CancellationToken ct = default);
}

public interface IComparisonBridge : IAsyncDisposable;
