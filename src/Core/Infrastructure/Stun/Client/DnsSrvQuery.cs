using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Client;

/// <summary>
/// Performs raw UDP DNS queries to retrieve SRV resource records (RFC 2782).
/// Used by <see cref="StunServerResolver"/> to implement RFC 5389 §9 DNS-based server discovery.
/// <para>
/// Sends a standard DNS query (opcode 0, recursion desired) over UDP and parses the SRV RDATA
/// from the answer section. DNS name compression (pointer encoding) in responses is fully
/// supported.
/// </para>
/// </summary>
internal static class DnsSrvQuery
{
    private const ushort TypeSrv  = 33;
    private const ushort ClassIn  = 1;
    private const int    DnsPort  = 53;
    private const int    TimeoutMs = 3_000;
    private const string ResolvConfPath = "/etc/resolv.conf";

    /// <summary>
    /// Queries the given DNS server for SRV records matching <paramref name="srvName"/>.
    /// Returns an empty list when no SRV records exist or the query fails.
    /// Never throws; failures are returned as an empty list.
    /// </summary>
    /// <param name="srvName">Full SRV owner name, e.g. <c>_stun._udp.example.org</c>.</param>
    /// <param name="dnsServer">DNS resolver endpoint to query.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<IReadOnlyList<DnsSrvRecord>> QueryAsync(
        string            srvName,
        IPEndPoint        dnsServer,
        CancellationToken ct = default)
        => await QueryAsync(srvName, dnsServer, TimeSpan.FromMilliseconds(TimeoutMs), ct).ConfigureAwait(false);

    /// <summary>
    /// Queries the given DNS server for SRV records matching <paramref name="srvName"/>
    /// with an explicit response timeout.
    /// Returns an empty list when no SRV records exist or the query fails.
    /// Never throws except caller-requested cancellation.
    /// </summary>
    internal static async Task<IReadOnlyList<DnsSrvRecord>> QueryAsync(
        string            srvName,
        IPEndPoint        dnsServer,
        TimeSpan          responseTimeout,
        CancellationToken ct = default)
    {
        if (responseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseTimeout), "DNS response timeout must be positive.");

        try
        {
            // Cryptographically secure transaction ID over the full 16-bit range (0..65535
            // inclusive) as an anti-spoofing measure (CWE-330). GetInt32's upper bound is
            // exclusive, so 65536 is used to include 65535.
            var txId  = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
            var query = BuildQuery(txId, srvName);

            using var udp = new UdpClient(dnsServer.AddressFamily);
            udp.Connect(dnsServer);
            await udp.SendAsync(query, ct).ConfigureAwait(false);

            using var rtoSource = new CancellationTokenSource(responseTimeout);
            using var linked    = CancellationTokenSource.CreateLinkedTokenSource(rtoSource.Token, ct);

            var result = await udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
            return ParseSrvResponse(result.Buffer, txId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    // ── DNS query construction ─────────────────────────────────────────────────

    private static byte[] BuildQuery(ushort txId, string name)
    {
        var encodedName = EncodeDnsName(name);
        // Header (12) + encoded name + QTYPE (2) + QCLASS (2)
        var buf  = new byte[12 + encodedName.Length + 4];
        var span = buf.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span,       txId);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..],  0x0100); // Flags: QR=0, OPCODE=0, RD=1
        BinaryPrimitives.WriteUInt16BigEndian(span[4..],  1);      // QDCount = 1
        BinaryPrimitives.WriteUInt16BigEndian(span[6..],  0);      // ANCount
        BinaryPrimitives.WriteUInt16BigEndian(span[8..],  0);      // NSCount
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 0);      // ARCount

        encodedName.CopyTo(span[12..]);
        int qOff = 12 + encodedName.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span[qOff..],       TypeSrv);
        BinaryPrimitives.WriteUInt16BigEndian(span[(qOff + 2)..], ClassIn);
        return buf;
    }

    /// <summary>
    /// Encodes a domain name as DNS label sequence: <c>\x04_stun\x03udp\x07example\x03org\x00</c>.
    /// </summary>
    private static byte[] EncodeDnsName(string name)
    {
        var result = new List<byte>(name.Length + 2);
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            result.Add((byte)bytes.Length);
            result.AddRange(bytes);
        }

        result.Add(0); // root label
        return [.. result];
    }

    // ── DNS response parsing ──────────────────────────────────────────────────

    private static IReadOnlyList<DnsSrvRecord> ParseSrvResponse(byte[] data, ushort expectedTxId)
    {
        if (data.Length < 12)
            return [];

        var span = data.AsSpan();
        ushort txId  = BinaryPrimitives.ReadUInt16BigEndian(span);
        if (txId != expectedTxId) return [];

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        if ((flags & 0x8000) == 0) return []; // QR bit not set — not a response
        if ((flags & 0x000F) != 0) return []; // RCODE != 0 (NXDOMAIN, REFUSED, etc.)

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);

        int offset = 12;

        // Skip the question section.
        for (int i = 0; i < qdCount && offset < data.Length; i++)
        {
            offset = SkipDnsName(data, offset);
            offset += 4; // QTYPE + QCLASS
        }

        // Cap the pre-allocation to the datagram's real answer capacity: each SRV answer RR
        // needs at least ~13 wire bytes, so a spoofed ANCount cannot force an oversized list.
        var records = new List<DnsSrvRecord>(Math.Min((int)anCount, data.Length / 13 + 1));

        // Parse the answer section.
        for (int i = 0; i < anCount && offset + 10 <= data.Length; i++)
        {
            offset = SkipDnsName(data, offset);
            if (offset + 10 > data.Length) break;

            ushort rrType  = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 2)..]);
            // Skip TTL (4 bytes)
            ushort rdLen   = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 8)..]);
            offset += 10;

            // SRV RDATA is priority(2)+weight(2)+port(2)+target; require the 6 fixed bytes to be
            // present so a hostile short RDLEN cannot drive the fixed-field reads past the buffer.
            if (rrType == TypeSrv && rrClass == ClassIn && rdLen >= 6 && offset + rdLen <= data.Length)
            {
                ushort priority = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
                ushort weight   = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 2)..]);
                ushort port     = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 4)..]);
                string target   = DecodeDnsName(data, offset + 6);
                records.Add(new DnsSrvRecord(priority, weight, port, target));
            }

            offset += rdLen;
        }

        return records;
    }

    /// <summary>
    /// Advances past a DNS name (labels or compression pointer) and returns the new offset.
    /// </summary>
    private static int SkipDnsName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            byte b = data[offset];
            if (b == 0)          return offset + 1;      // root label
            if ((b & 0xC0) == 0xC0) return offset + 2;  // compression pointer (2 bytes)
            offset += 1 + b;                             // label
        }

        return offset;
    }

    /// <summary>
    /// Decodes a (possibly compressed) DNS name starting at <paramref name="offset"/>.
    /// Follows at most one level of compression pointer to prevent infinite loops.
    /// </summary>
    private static string DecodeDnsName(byte[] data, int offset)
    {
        var    parts    = new List<string>();
        bool   followed = false; // Only follow one compression pointer.
        int    pos      = offset;

        while (pos < data.Length)
        {
            byte b = data[pos];

            if (b == 0) break; // Root label.

            if ((b & 0xC0) == 0xC0)
            {
                if (followed || pos + 1 >= data.Length) break;
                followed = true;
                pos = ((b & 0x3F) << 8) | data[pos + 1];
                continue;
            }

            int labelLen = b;
            pos++;
            if (pos + labelLen > data.Length) break;
            parts.Add(Encoding.ASCII.GetString(data, pos, labelLen));
            pos += labelLen;
        }

        return string.Join('.', parts);
    }

    // ── System DNS resolver ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first usable system DNS resolver endpoint found in <c>/etc/resolv.conf</c>
    /// (Linux/macOS). All <c>nameserver</c> entries (IPv4 and IPv6) are scanned; the first one
    /// that parses as a valid IP address is returned.
    /// <para>
    /// There is deliberately no silent fallback to a third-party public resolver: callers that
    /// require a specific resolver must supply one explicitly rather than relying on system
    /// discovery.
    /// </para>
    /// </summary>
    /// <returns>The first valid system DNS resolver endpoint.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no usable system DNS resolver can be determined — for example when the platform
    /// is neither Linux nor macOS, <c>/etc/resolv.conf</c> is missing or unreadable, or the file
    /// contains no valid <c>nameserver</c> entry.
    /// </exception>
    public static IPEndPoint GetSystemDnsServer()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                if (TryParseResolvConf(File.ReadLines(ResolvConfPath), out var resolver))
                    return resolver;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"No system DNS resolver configured (failed to read {ResolvConfPath}).", ex);
            }
        }

        throw new InvalidOperationException(
            $"No system DNS resolver configured ({ResolvConfPath}).");
    }

    /// <summary>
    /// Parses <c>resolv.conf</c>-style lines and returns the first valid <c>nameserver</c>
    /// resolver endpoint. Both IPv4 and IPv6 addresses are supported. All <c>nameserver</c>
    /// lines are scanned; the first one whose address parses wins.
    /// </summary>
    /// <param name="lines">Lines of a <c>resolv.conf</c> file.</param>
    /// <param name="resolver">The resolved endpoint on success; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a valid resolver was found; otherwise <c>false</c>.</returns>
    internal static bool TryParseResolvConf(IEnumerable<string> lines, [NotNullWhen(true)] out IPEndPoint? resolver)
    {
        ArgumentNullException.ThrowIfNull(lines);

        const string prefix = "nameserver ";
        foreach (var line in lines)
        {
            if (line is null) continue;

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed[prefix.Length..].Trim();

            // Keep only the address token; ignore any trailing tokens on the line.
            var spaceIdx = value.IndexOf(' ');
            if (spaceIdx >= 0) value = value[..spaceIdx];

            if (IPAddress.TryParse(value, out var addr))
            {
                resolver = new IPEndPoint(addr, DnsPort);
                return true;
            }
        }

        resolver = null;
        return false;
    }
}
