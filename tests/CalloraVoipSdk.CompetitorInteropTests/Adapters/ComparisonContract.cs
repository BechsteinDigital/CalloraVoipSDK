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

public enum ComparisonTerminationCategory
{
    Completed,
    Busy,
    NoAnswer,
    Rejected,
    Canceled,
    Failed,
}

public enum ComparisonTerminatedBy
{
    Local,
    Remote,
    Unknown,
}

public sealed record ComparisonTerminationReason(
    int? SipStatusCode,
    ComparisonTerminationCategory Category,
    ComparisonTerminatedBy TerminatedBy,
    string? Detail = null)
{
    public static ComparisonTerminationReason FromRemoteSipResponse(
        int? statusCode,
        string? detail = null) =>
        new(
            statusCode,
            CategoryForSipStatus(statusCode),
            ComparisonTerminatedBy.Remote,
            detail);

    private static ComparisonTerminationCategory CategoryForSipStatus(int? statusCode) =>
        statusCode switch
        {
            486 or 600 => ComparisonTerminationCategory.Busy,
            408 or 480 => ComparisonTerminationCategory.NoAnswer,
            487 => ComparisonTerminationCategory.Canceled,
            403 or 603 => ComparisonTerminationCategory.Rejected,
            >= 100 and < 400 => ComparisonTerminationCategory.Completed,
            _ => ComparisonTerminationCategory.Failed,
        };
}

public sealed record SipTestAccount(
    string Server,
    int Port,
    string Username,
    string Password,
    int RegistrationExpirySeconds = 10);

public sealed record DialAttempt(
    DialAttemptStatus Status,
    IComparisonCall? Call = null,
    string? Detail = null,
    ComparisonTerminationReason? TerminationReason = null);

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
