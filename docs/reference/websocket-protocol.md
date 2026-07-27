# Realtime WebSocket Audio Protocol v1

This document specifies the websocket binary framing used by `CalloraVoipSdk.WebSocket` for realtime audio bridging.

## Scope

- Transport: WebSocket (`ws://` and `wss://`)
- Message type: binary websocket messages only
- Endianness: network byte order (big-endian)
- Version: `1`

## Frame Layout (v1)

Each websocket binary message contains exactly one audio frame.

Header length: 16 bytes

- Byte `0`: Magic `0x56` (`'V'`)
- Byte `1`: Magic `0x41` (`'A'`)
- Byte `2`: Version `0x01`
- Byte `3`: Flags (reserved, currently `0x00`)
- Bytes `4..7`: `payloadType` (`int32`, big-endian)
- Bytes `8..11`: `durationRtpUnits` (`uint32`, big-endian)
- Bytes `12..15`: `payloadLength` (`int32`, big-endian)
- Bytes `16..N`: payload bytes (`payloadLength` bytes)

## Validation Rules

A frame is invalid when:

1. message length is smaller than 16 bytes
2. magic bytes are not `0x56 0x41`
3. version is not `1`
4. `payloadLength` is negative
5. total message length is not `16 + payloadLength`
6. `payloadLength` exceeds configured `MaxAudioPayloadBytes`

Invalid frames are dropped.

## Runtime Limits

Two limit classes apply:

1. WebSocket message size limit (`MaxIncomingMessageBytes`)
2. Audio payload size limit (`MaxAudioPayloadBytes`)

When `MaxIncomingMessageBytes` is exceeded, the connection is closed with `MessageTooBig`.

## Compatibility

- Producers and consumers must use protocol version `1`.
- Future versions must bump byte `2` and keep old versions backward compatible or negotiate externally.
