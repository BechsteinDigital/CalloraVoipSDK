using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// The public STUN server host (Level 2 facade). Assembles the internal <see cref="StunServer"/> from a
/// <see cref="StunServerHostConfiguration"/> — binding the socket on construction — and answers Binding requests
/// with a <see cref="StunBindingRequestHandler"/> once started. Kept in the Client layer so the server
/// infrastructure stays an internal implementation detail reached only through this facade.
/// </summary>
public sealed class StunServerHost : IStunServerHost
{
    private readonly StunServer _server;
    private readonly IStunMessageCodec _codec;
    private readonly ILoggerFactory _loggerFactory;
    private int _started;
    private int _disposed;

    /// <summary>Builds and binds the server from <paramref name="configuration"/>. The socket is bound here, so
    /// <see cref="LocalEndPoint"/> is valid before <see cref="Start"/>.</summary>
    /// <exception cref="ArgumentException">A TLS transport without a certificate.</exception>
    public StunServerHost(StunServerHostConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.BindEndPoint);

        var transport = MapTransport(configuration.Transport);
        if (transport == StunServerTransport.Tls && configuration.TlsCertificate is null)
            throw new ArgumentException("A TLS transport requires a TlsCertificate.", nameof(configuration));

        _loggerFactory = configuration.LoggerFactory ?? NullLoggerFactory.Instance;
        _codec = new StunMessageCodec();
        _server = new StunServer(
            configuration.BindEndPoint,
            transport,
            _codec,
            responseIntegrityKey: null, // a public Binding server needs no MESSAGE-INTEGRITY
            configuration.TlsCertificate,
            _loggerFactory.CreateLogger<StunServer>(),
            new StunServerOptions());
    }

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint => _server.LocalEndPoint;

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || Volatile.Read(ref _disposed) != 0)
            return;
        _server.Start(new StunBindingRequestHandler(_codec, _loggerFactory.CreateLogger<StunBindingRequestHandler>()));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private static StunServerTransport MapTransport(IceTransport transport) => transport switch
    {
        IceTransport.Udp => StunServerTransport.Udp,
        IceTransport.Tcp => StunServerTransport.Tcp,
        IceTransport.Tls => StunServerTransport.Tls,
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported STUN server transport."),
    };
}
