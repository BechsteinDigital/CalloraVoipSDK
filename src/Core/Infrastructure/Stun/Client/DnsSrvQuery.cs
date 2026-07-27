using System.Buffers.Binary;
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
            var txId  = NextTransactionId();
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

    /// <summary>
    /// Generates a cryptographically strong DNS query ID across the full 16-bit range (0..65535).
    /// RFC 5452 §10 requires the query ID to be unpredictable to an off-path attacker to close the
    /// DNS-spoofing/cache-poisoning window; a non-crypto PRNG (<see cref="Random"/>) is unsuitable.
    /// Uses a CSPRNG and reads the whole 0..0xFFFF range — <c>Random.Shared.Next(0, 65535)</c> both
    /// excluded 0xFFFF (exclusive upper bound) and was predictable.
    /// </summary>
    internal static ushort NextTransactionId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

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

        var records = new List<DnsSrvRecord>(anCount);

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

            if (rrType == TypeSrv && rrClass == ClassIn && offset + rdLen <= data.Length)
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
    /// Returns the first system DNS resolver endpoint found in <c>/etc/resolv.conf</c> (Linux/macOS)
    /// or falls back to Google's public resolver at 8.8.8.8:53.
    /// </summary>
    public static IPEndPoint GetSystemDnsServer()
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                foreach (var line in File.ReadLines("/etc/resolv.conf"))
                {
                    const string prefix = "nameserver ";
                    if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    var ip = line[prefix.Length..].Trim();
                    if (IPAddress.TryParse(ip, out var addr))
                        return new IPEndPoint(addr, DnsPort);
                }
            }
        }
        catch
        {
            // Ignore — fall through to default.
        }

        return new IPEndPoint(IPAddress.Parse("8.8.8.8"), DnsPort);
    }
}
