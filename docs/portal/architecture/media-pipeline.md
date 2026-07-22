# Media Pipeline

## Receive Path (Inbound Audio)

```
Network ──UDP──► RTP Receiver ──demux──► Jitter Buffer ──► PCM Decode ──► IMediaReceiver
```

## Send Path (Outbound Audio)

```
IMediaSender ──► PCM Encode ──► RTP Packetize ──► SRTP Encrypt ──► UDP ──► Network
```

## SRTP

SRTP encryption is negotiated during SDP offer/answer. The SDK automatically selects the strongest mutually supported crypto suite. No manual key management is required.

## Jitter Buffer

An adaptive jitter buffer smooths out network packet reordering and delay variation. The target delay adjusts automatically based on observed network conditions.

## RTCP

RTCP Sender Reports and Receiver Reports are sent every 5 seconds. Quality metrics (packet loss, jitter, round-trip time) are available via the `ICall.QualitySnapshot` property and the `ICall.QualitySnapshotChanged` event (or `client.QualityManager`).
