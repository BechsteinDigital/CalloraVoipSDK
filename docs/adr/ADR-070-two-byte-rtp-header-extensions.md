# ADR-070: Two-Byte RTP Header Extensions and Form Selection by Need

Status: Accepted
Date: 2026-08-17

## Context

The SDK knew only the RFC 8285 §4.2 one-byte header-extension form: profile `0xBEDE`, identifiers 1..14,
values 1..16 bytes. `RtpExtension` named the two-byte profile `0x1000` in a doc comment, but no encoder or
parser existed, and `RtpMidHeaderExtension` / `RtpRidHeaderExtension` threw with "a longer MID/RID needs the
two-byte form" — the gap was documented rather than closed (#224).

Three things the one-byte form cannot express: an identifier above 14, a value longer than 16 bytes, and a
value of length zero. The Dependency Descriptor — the extension through which a forwarder obtains key-frame
and layer information *without reading the payload* — routinely exceeds 16 bytes, so #225 cannot be built on
the one-byte form, and #310 (a media path that stops depending on payload semantics) depends on that in turn.

## Decision

1. **`TwoByteRtpHeaderExtensions` mirrors the one-byte codec** for RFC 8285 §4.3: one identifier byte, one
   length byte, then the value. Identifier 0 is padding; unlike the one-byte form there is no "stop parsing"
   identifier, since 15 is an ordinary id here. Parsing stays lenient on received data as the RFC requires
   (skip padding, drop a truncated tail, return the valid prefix) — remote input never throws (K4).

2. **The profile is matched on its defined bits.** RFC 8285 §4.3 fixes the top twelve bits to `0x100` and
   leaves the low nibble as appbits, so `0x1000` through `0x100F` are all the two-byte form. Matching the
   literal `0x1000` would have failed against a peer that sets an appbit.

3. **The send side picks the form from the elements, not from configuration.** `RtpHeaderExtensions.Encode`
   uses the one-byte form while every element fits it and the two-byte form otherwise (RFC 8285 §4.3, final
   paragraph). One oversized element moves the whole extension, because a packet carries one form. No send
   path has to know about wire forms; the one-byte form remains the default for everything that fits, being
   a byte shorter per element and universally understood.

4. **The receive side reads whichever form arrives — in all three readers.** Transport-cc, MID and RID each
   had their own allocation-free scan gated on `0xBEDE`. That gate is the real hazard of this feature: a peer
   that needs the two-byte form for *one* extension writes *every* element of that packet in it, so a
   `0xBEDE`-only reader silently loses congestion feedback and, on a BUNDLE, the MID routing token for
   exactly those packets. The scans moved behind `RtpHeaderExtensions.TryFindValue`, which dispatches on the
   profile and stays allocation-free (K3).

5. **`a=extmap` assigns ids from 1 upwards across the full 1..255 range.** The first fourteen land in the
   one-byte range, so the SDP is byte-identical for any peer with at most fourteen extensions — which is
   every one today. Beyond that the assignment continues into the two-byte range instead of dropping
   extensions, and packets carrying such an id are written in the two-byte form automatically. An offered id
   above 255 does not exist in RFC 8285 and is still refused.

6. **The one-byte fast paths stay.** `EncodeTransportSequenceNumber` and the MID/RID direct writers bypass
   the element list on the per-packet path. The stamper keeps using them when the negotiated id fits the
   one-byte form and falls back to the general encoder when it does not, so the common case is unchanged
   byte for byte and allocation for allocation.

## Consequences

- The Dependency Descriptor becomes transportable; #225 is unblocked.
- A MID or RID token longer than 16 bytes is still rejected by `RtpMidHeaderExtension` /
  `RtpRidHeaderExtension`, whose value encoders remain one-byte-bounded. Nothing in the SDK produces such a
  token, and no peer sends one — lifting that limit is a separate, unmotivated change, so the throw stays and
  now names a form that actually exists.
- SDP output changes only for a peer offering more than fourteen extensions, which the SDK does not do today.
- Interop risk is one-sided and small: the SDK now *accepts* a form it previously ignored, and only *emits*
  it when an element cannot be expressed otherwise.

## References

- RFC 8285 §4.2 (one-byte form), §4.3 (two-byte form and the selection rule), §5 (`a=extmap`)
- RFC 9143 (MID), RFC 8852 (RID) — the extensions carried on the bundled transport
- ADR-034 (secondary-stream transport and the one-byte form this extends)
- Issues #224 (this decision), #225 (Dependency Descriptor), #310 (payload-independent media path)
