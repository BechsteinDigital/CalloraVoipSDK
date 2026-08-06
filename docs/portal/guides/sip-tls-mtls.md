# SIP over TLS with mutual TLS (per line)

The SDK runs SIP over TLS (`sip:`/`sips:`, port 5061) and WSS. On such a line it can present a
**client certificate** for mutual TLS, and each line carries its **own** TLS identity — two accounts
to the same registrar can present two different certificates. The identity is bound to the *line*, not
to the client as a whole (the model Asterisk `pjsip` and SIPSorcery/PJSIP use), and it is stamped on
every request the line originates (REGISTER, INVITE, in-dialog requests, MESSAGE, PUBLISH). See
[ADR-064](../adr/ADR-064-per-line-sip-mutual-tls.md).

## Configuration surface

| Member | Type | Meaning |
|--------|------|---------|
| `TlsConfiguration.ClientCertificate` | `X509Certificate2?` | In-memory client identity; takes precedence over `CertificatePath` and is **caller-owned** (never disposed by the SDK) |
| `TlsConfiguration.CertificatePath` / `CertificatePassword` | `string?` | File-loaded identity (PFX/P12 or PEM); an SDK-loaded certificate stays SDK-owned |
| `TlsConfiguration.TrustMode` | `SipTlsTrustMode` | Server-trust policy: `System` (default) or `DangerousAcceptAnyChain` |
| `TlsConfiguration.ExpectedSipDomain` | `string?` | RFC 5922 §7 SIP-domain SAN check, fail-closed in every trust mode |
| `ConnectOptions.LineTls` | `TlsConfiguration?` | Per-line/per-connect override of the client-wide `VoipConfiguration.Tls` |

The client-wide identity is `VoipConfiguration.Tls`; `ConnectOptions.LineTls` overrides it for one
`ConnectAsync` call. `null` keeps the client-wide identity. TLS members are only relevant when the
line's transport is `Tls` or `Wss`.

## A client certificate from memory

Consumers often hold the identity in memory — from a secret store, an HSM export, or a rotated
in-process cache — not as a file. Assign it to `ClientCertificate`; keep the instance alive for as long
as the line uses it, and dispose it yourself when done.

```csharp
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Domain.Lines;

using var identity = /* X509Certificate2 with a private key, from your store/HSM */;

using var client = new VoipClient(new VoipConfiguration
{
    DefaultTransport = SipTransport.Tls,
});

var account = new SipAccount
{
    SipServer = "sip.example.com",
    Username  = "line-a",
    Password  = "…",
    Transport = SipTransport.Tls,
};

var result = await client.ConnectAsync(account, new ConnectOptions
{
    LineTls = new TlsConfiguration
    {
        ClientCertificate = identity,      // caller-owned; the SDK never disposes it
        ExpectedSipDomain = "sip.example.com",
    },
});
```

The certificate is presented **only if the registrar sends a `CertificateRequest`** (RFC 8446 §4.4.2),
so behaviour against a registrar that does not request one is byte-identical to a line without a client
certificate. A file-loaded identity is the same shape with `CertificatePath`/`CertificatePassword`
instead of `ClientCertificate`.

## Two lines, two identities, one registrar

Because the pool key is `(transport, addr:port, identity)`, two lines to the same endpoint present
their own certificate over separate pooled connections — they do not collapse onto one connection.

```csharp
using var identityA = /* certificate for line A */;
using var identityB = /* certificate for line B */;

await client.ConnectAsync(
    new SipAccount { SipServer = "sip.example.com", Username = "line-a", Transport = SipTransport.Tls },
    new ConnectOptions { LineTls = new TlsConfiguration { ClientCertificate = identityA } });

await client.ConnectAsync(
    new SipAccount { SipServer = "sip.example.com", Username = "line-b", Transport = SipTransport.Tls },
    new ConnectOptions { LineTls = new TlsConfiguration { ClientCertificate = identityB } });
```

## Server-trust modes

`TrustMode` governs the **standard** chain/hostname decision only:

| Value | Behaviour |
|-------|-----------|
| `System` *(default)* | Platform trust store; the server chain and hostname must validate |
| `DangerousAcceptAnyChain` | Accept any chain/hostname result (self-signed included) — development/test only |

`DangerousAcceptAnyChain` never relaxes the two fail-closed checks: a configured `ExpectedSipDomain` is
still enforced (RFC 5922 §7 — exact `dNSName` or `sip:` URI SAN; `sips:`, userinfo URIs and wildcards
are rejected), and a **missing** peer certificate is still rejected, in every mode.

```csharp
LineTls = new TlsConfiguration
{
    ClientCertificate = identity,
    TrustMode         = SipTlsTrustMode.DangerousAcceptAnyChain,  // lab only
    ExpectedSipDomain = "sip.example.com",                        // still enforced
};
```

### Deprecated: `AcceptUntrustedCertificates`

`TlsConfiguration.AcceptUntrustedCertificates` is now an `[Obsolete]` alias — `true` maps to
`TrustMode = DangerousAcceptAnyChain`. It still compiles and behaves as before; prefer `TrustMode` in
new code. If both are set in one initializer, the last assignment wins.

## Limitations

- The client certificate is sent **only when the registrar requests it** with a `CertificateRequest`
  (RFC 8446 §4.4.2). A registrar that does not request one sees no client identity, by design.
- Live `verify_client=yes` interop against a production registrar is **not** part of CI and is
  therefore not claimed here. The per-line behaviour is covered by wire tests against a loopback
  mutual-TLS server (two lines → two certificates at the same endpoint) — see ADR-064.
- An in-memory `ClientCertificate` is **caller-owned**: keep it alive while the line uses it and
  dispose it yourself. Only a certificate the SDK loads from `CertificatePath` is SDK-disposed.

## See also

- [VoipClient](../concepts/voipclient.md) — client-wide TLS via `VoipConfiguration.Tls`
- [NAT and SIP trunks](nat-and-trunks.md) — choosing the SIP transport
- [SRTP / SRTCP](srtp-srtcp.md) — securing the media path (TLS secures signalling only)
