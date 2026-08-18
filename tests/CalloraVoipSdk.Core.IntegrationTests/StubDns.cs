using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A canned DNS zone for the RFC 3263 resolution tests: answers exactly the four query types the SIP route
/// resolver asks for (NAPTR, SRV, A, AAAA) and nothing else.
/// </summary>
/// <remarks>
/// Every other <see cref="IDnsQuery"/> member throws rather than returning an empty answer, so a resolver
/// change that starts asking a different question fails loudly here instead of silently resolving nothing.
/// </remarks>
internal sealed class StubDns : IDnsQuery
{
    private readonly Dictionary<(string Name, QueryType Type), List<DnsResourceRecord>> _zone = new();

    public StubDns Naptr(string name, ushort order, ushort preference, string service, string replacement)
        => Add(name, QueryType.NAPTR, new NAPtrRecord(
            Info(name, ResourceRecordType.NAPTR), order, preference, "s", service, string.Empty,
            DnsString.Parse(replacement)));

    public StubDns Srv(string name, ushort priority, ushort weight, ushort port, string target)
        => Add(name, QueryType.SRV, new SrvRecord(
            Info(name, ResourceRecordType.SRV), priority, weight, port, DnsString.Parse(target)));

    public StubDns A(string name, string address)
        => Add(name, QueryType.A, new ARecord(Info(name, ResourceRecordType.A), IPAddress.Parse(address)));

    public StubDns Aaaa(string name, string address)
        => Add(name, QueryType.AAAA, new AaaaRecord(Info(name, ResourceRecordType.AAAA), IPAddress.Parse(address)));

    private StubDns Add(string name, QueryType type, DnsResourceRecord record)
    {
        var key = (Normalize(name), type);
        if (!_zone.TryGetValue(key, out var records))
            _zone[key] = records = [];
        records.Add(record);
        return this;
    }

    private static ResourceRecordInfo Info(string name, ResourceRecordType type) =>
        new(DnsString.Parse(name), type, QueryClass.IN, timeToLive: 60, rawDataLength: 0);

    private static string Normalize(string name) => name.TrimEnd('.').ToLowerInvariant();

    private IDnsQueryResponse Answer(string query, QueryType type) =>
        new StubResponse(_zone.TryGetValue((Normalize(query), type), out var records) ? records : []);

    public Task<IDnsQueryResponse> QueryAsync(
        string query, QueryType queryType, QueryClass queryClass = QueryClass.IN,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Answer(query, queryType));

    public IDnsQueryResponse Query(string query, QueryType queryType, QueryClass queryClass = QueryClass.IN)
        => Answer(query, queryType);

    private sealed class StubResponse(IReadOnlyList<DnsResourceRecord> answers) : IDnsQueryResponse
    {
        public IReadOnlyList<DnsResourceRecord> Answers { get; } = answers;
        public IReadOnlyList<DnsResourceRecord> Additionals { get; } = [];
        public IEnumerable<DnsResourceRecord> AllRecords => Answers;
        public IReadOnlyList<DnsResourceRecord> Authorities { get; } = [];
        public string AuditTrail => string.Empty;
        public IReadOnlyList<DnsQuestion> Questions { get; } = [];
        public string ErrorMessage => string.Empty;
        public bool HasError => false;
        public DnsResponseHeader Header { get; } = new(1, 0, 0, 0, 0, 0);
        public int MessageSize => 0;
        public NameServer NameServer { get; } = new(IPAddress.Loopback, 53);
        public DnsQuerySettings Settings => throw new NotSupportedException(NotAsked);
    }

    private const string NotAsked =
        "The SIP route resolver does not use this member; a test reaching it means the resolution chain changed.";

    // ── everything the resolver does not ask for ─────────────────────────────────────────────────────

    public IDnsQueryResponse Query(DnsQuestion question) => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryCache(string query, QueryType queryType, QueryClass queryClass = QueryClass.IN)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryCache(DnsQuestion question) => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryAsync(DnsQuestion question, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryReverse(IPAddress ipAddress) => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryReverseAsync(IPAddress ipAddress, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServer(
        IReadOnlyCollection<NameServer> servers, DnsQuestion question) => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServer(
        IReadOnlyCollection<NameServer> servers, string query, QueryType queryType, QueryClass queryClass = QueryClass.IN)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServer(
        IReadOnlyCollection<IPAddress> servers, string query, QueryType queryType, QueryClass queryClass = QueryClass.IN)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServer(
        IReadOnlyCollection<IPEndPoint> servers, string query, QueryType queryType, QueryClass queryClass = QueryClass.IN)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerAsync(
        IReadOnlyCollection<NameServer> servers, DnsQuestion question, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerAsync(
        IReadOnlyCollection<NameServer> servers, string query, QueryType queryType,
        QueryClass queryClass = QueryClass.IN, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerAsync(
        IReadOnlyCollection<IPAddress> servers, string query, QueryType queryType,
        QueryClass queryClass = QueryClass.IN, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerAsync(
        IReadOnlyCollection<IPEndPoint> servers, string query, QueryType queryType,
        QueryClass queryClass = QueryClass.IN, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServerReverse(IReadOnlyCollection<NameServer> servers, IPAddress ipAddress)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServerReverse(IReadOnlyCollection<IPAddress> servers, IPAddress ipAddress)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServerReverse(IReadOnlyCollection<IPEndPoint> servers, IPAddress ipAddress)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerReverseAsync(
        IReadOnlyCollection<NameServer> servers, IPAddress ipAddress, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerReverseAsync(
        IReadOnlyCollection<IPAddress> servers, IPAddress ipAddress, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerReverseAsync(
        IReadOnlyCollection<IPEndPoint> servers, IPAddress ipAddress, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);

    public IDnsQueryResponse Query(DnsQuestion question, DnsQueryAndServerOptions queryOptions)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryAsync(
        DnsQuestion question, DnsQueryAndServerOptions queryOptions, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryReverse(IPAddress ipAddress, DnsQueryAndServerOptions queryOptions)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryReverseAsync(
        IPAddress ipAddress, DnsQueryAndServerOptions queryOptions, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServer(
        IReadOnlyCollection<NameServer> servers, DnsQuestion question, DnsQueryOptions queryOptions)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerAsync(
        IReadOnlyCollection<NameServer> servers, DnsQuestion question, DnsQueryOptions queryOptions,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
    public IDnsQueryResponse QueryServerReverse(
        IReadOnlyCollection<NameServer> servers, IPAddress ipAddress, DnsQueryOptions queryOptions)
        => throw new NotSupportedException(NotAsked);
    public Task<IDnsQueryResponse> QueryServerReverseAsync(
        IReadOnlyCollection<NameServer> servers, IPAddress ipAddress, DnsQueryOptions queryOptions,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotAsked);
}
