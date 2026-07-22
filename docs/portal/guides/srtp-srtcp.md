# SRTP / SRTCP

The SDK encrypts media with SRTP (SDES keying, RFC 4568) and encrypts/authenticates RTCP
with SRTCP (RFC 3711 §3.4). It acts as **both** offerer and answerer. A rekey re-INVITE
swaps the keys; a hold/unhold re-offer deliberately **reuses** the existing keys (no rekey).

## Policy

`VoipConfiguration.SrtpPolicy` sets the default for all calls:

| Value | Behaviour |
|-------|-----------|
| `Disabled` | Never use SRTP; media is plain RTP |
| `Optional` *(default)* | Offer/accept SRTP when the peer supports it, else fall back to RTP |
| `Required` | Only complete calls with SRTP; calls without it fail |

```csharp
new VoipConfiguration { SrtpPolicy = SrtpPolicy.Required };
```

## Per-call override

```csharp
ICall call = await line.DialAsync(
    "sip:1002@pbx.example.com",
    new DialOptions { UseSrtp = true });
```

`DialOptions.UseSrtp` overrides the configured policy for a single outbound call.

## What is negotiated

- **Offer as caller** — outbound INVITEs advertise `RTP/SAVP` with an `a=crypto` line
  (AES-CM / HMAC-SHA1 suites).
- **Answer as callee** — inbound SRTP offers are accepted and keyed.
- **SRTCP** — once SRTP is negotiated, RTCP is protected too (encrypted + authenticated).
- **Rekey** — a rekey re-INVITE (RFC 3264 §8) swaps the SRTP keys. Hold/unhold sends a
  re-offer but **reuses** the existing keys (no rekey, by design — avoids needless key churn).

## Verifying

The negotiated crypto is readable on the call once connected — no wire trace required:

```csharp
var media = call.MediaParameters;
bool    encrypted = media?.IsSrtpNegotiated ?? false;
string? suite     = media?.SrtpSuite;                 // e.g. "AES_CM_128_HMAC_SHA1_80", null = plain RTP
bool    srtcp     = media?.IsSrtcpEncrypted ?? false; // RTCP protected too (RFC 3711 §3.4)
```

The key material itself stays internal. You can still confirm the `a=crypto` line via the
[SIP wire trace](../production/diagnostics.md). With `Required`, a peer that cannot do SRTP
yields a failed call rather than a silent downgrade.

## Limitations

- Keying is **SDES** (`a=crypto`). DTLS-SRTP is available separately (RFC 5763, opt-in via
  `VoipConfiguration.OfferDtlsSrtp`) and is not the SDES negotiation path described here.
- For maximum interop, prefer the `AES_CM_128_HMAC_SHA1_80` suite. There is a known defect in
  the `*_HMAC_SHA1_32` suites where the **SRTCP** auth tag is 4 bytes instead of the required
  10 (RFC 4568 §6.2), which breaks RTCP interop with standards-compliant peers (e.g. libsrtp);
  tracked in the [issue tracker](https://github.com/BechsteinDigital/callora-voip-sdk/issues).
- SRTP protects the media path; it does not by itself secure signaling — run SIP over TLS
  for signaling confidentiality.
