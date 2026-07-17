using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Transport layer for <see cref="TurnClient"/>: sends an encoded TURN request or indication over
/// UDP, TCP or TLS and — for requests — reads the transaction-matched response, applying RFC 5389
/// UDP retransmission (RTO backoff) and validating the response class/method (raising
/// <see cref="TurnChallengeException"/> on 401/438). Extracted so the socket and retransmission
/// machinery stays isolated from the client's auth orchestration and message construction.
/// </summary>
internal sealed class TurnClientTransport
{
    private readonly IStunMessageCodec _codec;
    private readonly ILogger _logger;

    /// <summary>Initial UDP retransmission timeout in milliseconds.</summary>
    private const int InitialRtoMs = 500;

    /// <summary>Maximum UDP retransmission timeout in milliseconds.</summary>
    private const int MaxRtoMs = 16_000;

    /// <summary>Total UDP transmission attempts (1 send + 6 retransmissions).</summary>
    private const int MaxAttempts = 7;

    /// <summary>Single response timeout in milliseconds for TCP/TLS requests.</summary>
    private const int ReliableResponseTimeoutMs = 8_000;

    /// <summary>Creates the transport over the shared wire codec and logger.</summary>
    public TurnClientTransport(IStunMessageCodec codec, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);
        _codec = codec;
        _logger = logger;
    }

    /// <summary>
    /// Sends an already-encoded request over the chosen transport and returns the transaction-matched,
    /// validated success response.
    /// </summary>
    public async Task<StunMessage> SendRequestAsync(
        IPEndPoint serverEndPoint,
        StunMessage request,
        byte[] requestBytes,
        TurnTransport transport,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        CancellationToken ct) =>
        transport switch
        {
            TurnTransport.Udp => await SendRequestUdpAsync(serverEndPoint, request, requestBytes, ct).ConfigureAwait(false),
            TurnTransport.Tcp => await SendRequestStreamAsync(
                    serverEndPoint,
                    request,
                    requestBytes,
                    tls: false,
                    tlsTargetHost,
                    tlsRemoteCertificateValidationCallback,
                    ct)
                .ConfigureAwait(false),
            TurnTransport.Tls => await SendRequestStreamAsync(
                    serverEndPoint,
                    request,
                    requestBytes,
                    tls: true,
                    tlsTargetHost,
                    tlsRemoteCertificateValidationCallback,
                    ct)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported TURN transport.")
        };

    /// <summary>Sends an already-encoded indication over the chosen transport (no response expected).</summary>
    public async Task SendIndicationAsync(
        IPEndPoint serverEndPoint,
        byte[] bytes,
        TurnTransport transport,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        CancellationToken ct)
    {
        switch (transport)
        {
            case TurnTransport.Udp:
                using (var udp = new UdpClient(serverEndPoint.AddressFamily))
                {
                    udp.Connect(serverEndPoint);
                    await udp.SendAsync(bytes, ct).ConfigureAwait(false);
                }
                break;

            case TurnTransport.Tcp:
            case TurnTransport.Tls:
                await SendIndicationStreamAsync(
                        serverEndPoint,
                        bytes,
                        tls: transport == TurnTransport.Tls,
                        tlsTargetHost,
                        tlsRemoteCertificateValidationCallback,
                        ct)
                    .ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported TURN transport.");
        }
    }

    private async Task<StunMessage> SendRequestUdpAsync(
        IPEndPoint serverEndPoint,
        StunMessage request,
        byte[] requestBytes,
        CancellationToken ct)
    {
        using var udp = new UdpClient(serverEndPoint.AddressFamily);
        udp.Connect(serverEndPoint);

        int rtoMs = InitialRtoMs;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await udp.SendAsync(requestBytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "TURN UDP send failed on attempt {Attempt}", attempt);
                throw new TurnException($"TURN UDP send failed: {ex.Message}", ex);
            }

            var response = await ReceiveMatchingUdpAsync(udp, request.TransactionId, rtoMs, ct).ConfigureAwait(false);
            if (response is null)
            {
                _logger.LogTrace("TURN attempt {Attempt} timed out after {Rto} ms", attempt, rtoMs);
                rtoMs = Math.Min(rtoMs * 2, MaxRtoMs);
                continue;
            }

            return ProcessResponse(response, request.MessageMethod);
        }

        throw new TurnException($"TURN server {serverEndPoint} did not respond after {MaxAttempts} attempts.");
    }

    private async Task<StunMessage> SendRequestStreamAsync(
        IPEndPoint serverEndPoint,
        StunMessage request,
        byte[] requestBytes,
        bool tls,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        CancellationToken ct)
    {
        using var tcp = new TcpClient(serverEndPoint.AddressFamily);

        try
        {
            await tcp.ConnectAsync(serverEndPoint.Address, serverEndPoint.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN {Transport} connect failed to {Server}", tls ? "TLS" : "TCP", serverEndPoint);
            throw new TurnException($"TURN connect failed: {ex.Message}", ex);
        }

        using var networkStream = tcp.GetStream();
        Stream stream = networkStream;

        if (tls)
        {
            var targetHost = string.IsNullOrWhiteSpace(tlsTargetHost) ? serverEndPoint.Address.ToString() : tlsTargetHost;
            var tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: true, tlsRemoteCertificateValidationCallback);

            try
            {
                await tlsStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = targetHost,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "TURN TLS handshake failed for {Server}", serverEndPoint);
                throw new TurnException($"TURN TLS handshake failed: {ex.Message}", ex);
            }

            stream = tlsStream;
        }

        try
        {
            await stream.WriteAsync(requestBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN {Transport} send failed", tls ? "TLS" : "TCP");
            throw new TurnException($"TURN send failed: {ex.Message}", ex);
        }

        var response = await ReceiveMatchingStreamAsync(stream, request.TransactionId, ReliableResponseTimeoutMs, ct)
            .ConfigureAwait(false);
        if (response is null)
            throw new TurnException($"TURN server {serverEndPoint} did not respond on {(tls ? "TLS" : "TCP")}.");

        return ProcessResponse(response, request.MessageMethod);
    }

    private async Task SendIndicationStreamAsync(
        IPEndPoint serverEndPoint,
        byte[] bytes,
        bool tls,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        CancellationToken ct)
    {
        using var tcp = new TcpClient(serverEndPoint.AddressFamily);
        try
        {
            await tcp.ConnectAsync(serverEndPoint.Address, serverEndPoint.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN indication connect failed to {Server}", serverEndPoint);
            throw new TurnException($"TURN indication connect failed: {ex.Message}", ex);
        }

        using var networkStream = tcp.GetStream();
        Stream stream = networkStream;

        if (tls)
        {
            var targetHost = string.IsNullOrWhiteSpace(tlsTargetHost) ? serverEndPoint.Address.ToString() : tlsTargetHost;
            var tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: true, tlsRemoteCertificateValidationCallback);

            try
            {
                await tlsStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = targetHost,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "TURN indication TLS handshake failed for {Server}", serverEndPoint);
                throw new TurnException($"TURN indication TLS handshake failed: {ex.Message}", ex);
            }

            stream = tlsStream;
        }

        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN indication send failed");
            throw new TurnException($"TURN indication send failed: {ex.Message}", ex);
        }
    }

    private static StunMessage ProcessResponse(StunMessage response, StunMessageMethod expectedMethod)
    {
        if (response.MessageMethod != expectedMethod)
            throw new TurnException(
                $"TURN response method mismatch. Expected 0x{(ushort)expectedMethod:X4}, got 0x{(ushort)response.MessageMethod:X4}.");

        if (response.MessageClass == StunMessageClass.ErrorResponse)
        {
            var error = response.Attributes.OfType<ErrorCodeAttribute>().FirstOrDefault();
            int code = error?.Code ?? 0;
            string reason = string.IsNullOrWhiteSpace(error?.Reason) ? "(no reason)" : error!.Reason;

            if (code is 401 or 438)
            {
                var realm = response.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
                var nonce = response.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value;
                throw new TurnChallengeException(code, reason, realm, nonce);
            }

            throw new TurnException($"TURN error {code}: {reason}");
        }

        if (response.MessageClass != StunMessageClass.SuccessResponse)
            throw new TurnException($"TURN unexpected response class: {response.MessageClass}");

        return response;
    }

    private async Task<StunMessage?> ReceiveMatchingUdpAsync(
        UdpClient udp,
        byte[] transactionId,
        int timeoutMs,
        CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(timeoutMs);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, ct);

        while (!linkedSource.Token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            var decoded = _codec.Decode(received.Buffer);
            if (decoded is not null && decoded.TransactionId.SequenceEqual(transactionId))
                return decoded;

            _logger.LogTrace("TURN discarded non-matching UDP packet ({Bytes} bytes)", received.Buffer.Length);
        }

        return null;
    }

    private async Task<StunMessage?> ReceiveMatchingStreamAsync(
        Stream stream,
        byte[] transactionId,
        int timeoutMs,
        CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(timeoutMs);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, ct);

        while (!linkedSource.Token.IsCancellationRequested)
        {
            byte[]? raw;
            try
            {
                raw = await StunTcpFramer.ReadMessageAsync(stream, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "TURN stream receive failed");
                throw new TurnException($"TURN stream receive failed: {ex.Message}", ex);
            }

            if (raw is null)
                return null;

            var decoded = _codec.Decode(raw);
            if (decoded is not null && decoded.TransactionId.SequenceEqual(transactionId))
                return decoded;

            _logger.LogTrace("TURN discarded non-matching stream packet ({Bytes} bytes)", raw.Length);
        }

        return null;
    }
}
