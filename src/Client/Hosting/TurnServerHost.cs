using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreTurnServer = CalloraVoipSdk.Core.Infrastructure.Turn.Server.TurnServer;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// The public TURN/STUN server host (Level 2 facade). Assembles the internal <see cref="CoreTurnServer"/> from a
/// <see cref="TurnServerHostConfiguration"/> — binding the socket on construction — and exposes only the small
/// hostable surface (<see cref="ITurnServerHost"/>). Kept in the Client layer so the server infrastructure stays
/// an internal implementation detail, reached only through this facade.
/// </summary>
public sealed class TurnServerHost : ITurnServerHost
{
    private readonly CoreTurnServer _server;
    private int _started;
    private int _disposed;

    /// <summary>Builds and binds the server from <paramref name="configuration"/>. The socket is bound here, so
    /// <see cref="LocalEndPoint"/> is valid before <see cref="Start"/>.</summary>
    /// <exception cref="ArgumentException">TLS without a certificate, or authentication without a realm/credentials.</exception>
    public TurnServerHost(TurnServerHostConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.BindEndPoint);

        var transport = MapTransport(configuration.Transport);
        if (transport == TurnServerTransport.Tls && configuration.TlsCertificate is null)
            throw new ArgumentException("A TLS transport requires a TlsCertificate.", nameof(configuration));

        var loggerFactory = configuration.LoggerFactory ?? NullLoggerFactory.Instance;
        var authOptions = configuration.RequireAuthentication ? BuildAuthOptions(configuration) : null;
        var options = new TurnServerOptions
        {
            RequireAuthentication = configuration.RequireAuthentication,
            DefaultAllocationLifetimeSeconds = configuration.DefaultAllocationLifetimeSeconds,
            MaxAllocationLifetimeSeconds = configuration.MaxAllocationLifetimeSeconds,
            MaxTotalAllocations = configuration.MaxTotalAllocations,
            PublicRelayAddress = configuration.PublicRelayAddress,
        };

        _server = new CoreTurnServer(
            configuration.BindEndPoint,
            transport,
            new StunMessageCodec(),
            loggerFactory.CreateLogger<CoreTurnServer>(),
            authOptions,
            configuration.TlsCertificate,
            options);
    }

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint => _server.LocalEndPoint;

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || Volatile.Read(ref _disposed) != 0)
            return;
        _server.Start();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private static TurnAuthOptions BuildAuthOptions(TurnServerHostConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Realm))
            throw new ArgumentException("A realm is required when RequireAuthentication is true.", nameof(configuration));
        if (configuration.Credentials.Count == 0)
            throw new ArgumentException("At least one credential is required when RequireAuthentication is true.", nameof(configuration));

        var realm = configuration.Realm;
        var credentials = configuration.Credentials
            .Select(c => new StunCredentials { Username = c.Username, Password = c.Password, Realm = realm })
            .ToArray();

        return new TurnAuthOptions
        {
            Realm = realm,
            CredentialProvider = new InMemoryStunCredentialProvider(credentials),
            NonceManager = new StunNonceManager(),
        };
    }

    private static TurnServerTransport MapTransport(IceTransport transport) => transport switch
    {
        IceTransport.Udp => TurnServerTransport.Udp,
        IceTransport.Tcp => TurnServerTransport.Tcp,
        IceTransport.Tls => TurnServerTransport.Tls,
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported TURN server transport."),
    };
}
