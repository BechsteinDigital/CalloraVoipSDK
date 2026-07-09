# Graph Report - voip  (2026-07-09)

## Corpus Check
- 554 files · ~175,274 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 4626 nodes · 10550 edges · 242 communities (199 shown, 43 thin omitted)
- Extraction: 95% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 545 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `0703b5ca`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- ICE Agent Negotiation
- STUN Attribute Codec
- SIP Infrastructure Namespaces
- TURN Server Core
- VoipClient Facade & Lines
- STUN/TURN Server Modules
- TURN Client Authentication
- SIP Wire Protocol
- Linux Audio Device
- SIP Stream Framing & Perf
- Call Domain Model
- Media Playback & Recording
- SIP Transport & TLS
- TURN Attribute Handling
- SIP Dialog Transactions
- Windows Audio Device
- RTP Media Session
- Domain Events & Convenience
- SIP Request Handling
- SIP Call Channel
- Call Media Orchestration
- SIP Call Session
- Phone Lines & Dialing
- STUN Server Handlers
- TURN Allocations
- SDK Options & ICE Config
- RTCP Quality Monitoring
- SIP Call Signaling
- SIP Telemetry & CDR
- STUN Token Auth RFC7635
- Signaling Registration ExecuteRegisterAsync
- Ports Audio SilenceAudioDevice
- portal architecture CalloraVoipSdk SDK
- Signaling Dialogs ISipCallSessionContext
- Domain Calls ICallChannel
- Domain Calls Call
- Application Media IMediaSender
- CalloraVoipSdk.Core.IntegrationTests StubSession
- Signaling Ingress SubscribeAsync
- CalloraVoipSdk.Client.Tests ModuleRegistryTests
- Media Sessions IAsyncDisposable
- Infrastructure Media IAudioFileCodec
- Sdp Models SdpMediaDescription
- Stun Server StunMessage
- CalloraVoipSdk.Core.IntegrationTests CapturingSipTransportRuntime
- Rtp Session RtpSession
- Signaling Dialogs SipResponse
- Signaling Reliability SipReliableProvisionalManager
- Media Sessions CalloraVoipSdk.Core.Application.Media
- Rtcp Packets CalloraVoipSdk.Core.Application.Media.Rtcp.Packets
- Application Media CallAudioFrame
- Turn Server TurnTcpConnectionBroker
- Signaling Ingress TryParseSipUri
- Media Sessions AudioFileFormat
- Media Sessions PlaybackSession
- Sip Adapters SipLineChannel
- Sip Routing SipTransportProtocol
- Media Sessions RecordingSession
- Infrastructure Media Mp3PassthroughReader
- Rtp JitterBuffer RtpPacket
- Signaling Subscriptions SipSubscriptionLifecycleManager
- Sip Transactions SipClientTransactionExecutor
- Sip Adapters CalloraVoipSdk.Core.Security
- CalloraVoipSdk.Conferencing.Performance Program
- Stun Client StunClient
- Windows Infrastructure VoipClient.cs
- Srtp Context CalloraVoipSdk.Core.Infrastructure.Rtp.Packets
- Sip Transport SipTransportRuntime
- CalloraVoipSdk.Core.IntegrationTests AckTestSipCallSessionContext
- CalloraVoipSdk.Core.IntegrationTests BuildContactUri
- Application Convenience DefaultAudioCallAttachment
- CalloraVoipSdk.Core.IntegrationTests SrtpHardeningTests
- Signaling Contracts ISipServerTransactionEngine
- Signaling Dialogs SipCallSessionContextAdapter
- Turn Client TurnTcpDataConnectionFactory
- CalloraVoipSdk.ArchitectureTests CsFiles
- Common Timing ScheduledActionScheduler
- Rtp JitterBuffer JitterBuffer
- Application Facades IVoipClient
- Application Convenience SdkConvenienceOrchestrator
- Sdp OfferAnswer SdpCodecDefinition
- Linux Infrastructure AudioDeviceDescriptor
- Ports Sdp SdpMediaNegotiationOptions
- Signaling Dialogs SendReliableProvisionalAndWaitForPrackAsync
- Signaling SessionTimers SipSessionTimerManager
- Sip Transport IPEndPoint
- CalloraVoipSdk.Core.IntegrationTests OpusCodecTests
- Infrastructure Media IAudioFileWriter
- Sip Wire SipProtocol
- Stun Client StunTransport
- Stun Client IceServerConfiguration
- CalloraVoipSdk.Core.IntegrationTests ApplyObserved
- Turn Client CalloraVoipSdk.Core.Infrastructure.Turn.Client
- Transactions Server SipServerTransactionEngine
- Rtcp Wire RtcpPacketCodec
- Signaling Contracts ISipCallSession
- Srtp Context ISrtpContext
- CalloraVoipSdk.Core.IntegrationTests TryBuildNegotiatedAnswer
- CalloraVoipSdk.Core.IntegrationTests IsForThisLine
- Transactions Server SendResponseAsync
- Srtp Context SrtpContext
- CalloraVoipSdk.Core.IntegrationTests SrtpContextTests
- Stun Client DnsSrvQuery
- Infrastructure Media WavAudioFileReader
- Srtp Crypto SrtpCryptoSuite
- Application Calls SrtpPolicy
- CalloraVoipSdk.Core.IntegrationTests RecordingRegistrationService
- Signaling Dialogs SipDialogManager
- Turn Client TryAllocateRelayAsync
- CalloraVoipSdk.Core.IntegrationTests SdpSdesAnswerTests
- CalloraVoipSdk.Core.IntegrationTests FakeInboundSession
- Sdp OfferAnswer CalloraVoipSdk.Core.Infrastructure.Sdp.Models
- Transactions Server IDisposable
- Sip Transport SipWebSocketConnection
- CalloraVoipSdk.Core.IntegrationTests SrtpMediaPathTests
- Infrastructure DependencyInjection CalloraVoipSdk.DependencyInjection
- Application Workflows CallState
- Common Protocols SplitCommaSeparatedRespectingQuotes
- CalloraVoipSdk.Core.IntegrationTests SipRegistrationExpiresTests
- CalloraVoipSdk.Core.IntegrationTests RecordingMediaSession
- Sip Adapters TryPublishMediaParameters
- CalloraVoipSdk.Audio.Tests CalloraVoipSdk.Audio.Tests.csproj
- Application Media AesGcmRecordingEncryptionProvider
- Infrastructure Sdp SdpUtilities
- Turn Server TurnMobilityTicketStore
- Common Timing ScheduledActionHandle
- Application Media MediaSender
- Media Sessions PcmG711Codec
- Common Disposal AsyncDisposeAction
- Core CalloraVoipSdk.Core.csproj
- Sip Transactions SipResponseEnvelope
- CalloraVoipSdk.Core.IntegrationTests QosMetricsTests
- Infrastructure Sdp SdpSessionDescription
- CalloraVoipSdk.Core.IntegrationTests Full_chain_encrypts_and_decrypts_via_real_media_session
- CalloraVoipSdk.Core.Performance CalloraVoipSdk.sln
- Media Sessions CallRecordingFrameSource
- Infrastructure Media WavAudioFileWriter
- Signaling Formatting SipSessionTimerPolicy
- Srtp Crypto SrtpKeyDerivation
- Audio Abstractions CalloraVoipSdk.Audio.Abstractions.csproj
- Application Media ICallMediaSession
- Rtp Session RtpSequenceValidator
- Sdp OfferAnswer NegotiateAnswer
- CalloraVoipSdk.Core.IntegrationTests StartRegistration
- Transactions Server SipServerTransactionKey
- Sip Transport RequestReceived
- Stun Server StunNonceManager
- CalloraVoipSdk.Client.Tests CalloraVoipSdk.Client.Tests.csproj
- CalloraVoipSdk.Core.IntegrationTests CalloraVoipSdk.Core.IntegrationTests.csproj
- Infrastructure DependencyInjection CalloraHostedService
- Client CalloraVoipSdk.Client.csproj
- Application Media CallMediaRuntimeMetrics
- Sip Wire TryFromRequest
- CalloraVoipSdk.SoakTests CalloraVoipSdk.SoakTests.csproj
- Stun Client CalloraVoipSdk.Core.Infrastructure.Stun.Client
- Sip Routing ResolveAsync
- CalloraVoipSdk.Core.IntegrationTests ShouldUseReliableProvisional
- Sip Wire ISipWireCodec
- Stun Auth DeriveHmacKey
- Stun Client QueryBindingAsync
- Stun Wire ReadMessageAsync
- Application Facades InvalidOperationException
- Audio Linux CalloraVoipSdk.Audio.Linux.csproj
- Ports Audio AudioConnectionParameters
- Rtp Session IRtpSession
- CalloraVoipSdk.Core.IntegrationTests RtcpCompoundDecodeTests
- CalloraVoipSdk.Core.IntegrationTests StunSharedSocketTests.cs
- Rtp Profile RtpAvpProfile
- CalloraVoipSdk.Conferencing.Performance CalloraVoipSdk.Conferencing.Performance.csproj
- Infrastructure DependencyInjection SdkOptionsValidator
- Audio Windows CalloraVoipSdk.Audio.Windows.csproj
- Common Network ResolveAsync
- Rtcp Wire IRtcpPacketCodec
- Sdp Parsing SdpSessionSerializer
- Sip Adapters HandleIncomingInvite
- Signaling Contracts ISipCallSignalingService
- Signaling Contracts ISipUasUserIdentityPolicy
- Signaling Formatting TryValidateSdpRequest
- CalloraVoipSdk.ArchitectureTests CalloraVoipSdk.ArchitectureTests.csproj
- CalloraVoipSdk.Audio.Tests SmokeTests.cs
- CalloraVoipSdk.SoakTests SmokeTests.cs
- CalloraVoipSdk.Media.Performance CalloraVoipSdk.Media.Performance.csproj
- Application Media PauseAsync
- Application Media PauseAsync
- Domain Lines ReregisterOptions
- Turn Wire TurnChannelDataCodec
- CalloraVoipSdk.Core.IntegrationTests DelegateDisposable
- CalloraVoipSdk.Core.IntegrationTests SipInDialogPublicContactTests
- CalloraVoipSdk.Media.Performance Program.cs
- Application Managers QualitySubscription
- Application Media CompositeDisposable
- Media Sessions BuildFilePath
- Domain Lines SipAddress
- Common Network ResolveAdvertisedLocalEndPoint
- Sip Transport SubscribeRequests
- Sip Transport ValidateTlsServerCertificate
- CalloraVoipSdk.Core.IntegrationTests SmokeTests.cs
- CalloraVoipSdk.Core.IntegrationTests NoopDisposable
- Media-Inactivity Timeout
- portal architecture RTP Send Path
- .github ISSUE_TEMPLATE Bug Report Issue
- .github ISSUE_TEMPLATE Feature Request Issue
- Record-Route Echo Fix (RFC 3261
- .TryDecodeXorAddressValue
- [1.0.2] - 2026-04-17
- [3.0.0] - 2026-07-07
- [3.2.0] - 2026-07-08
- TurnServerChannelBinding
- [3.1.0] - 2026-07-08
- [3.1.2] - 2026-07-08
- Installation
- ThrowingModule
- .IsTrusted
- RFC 3550 Jitter Estimator Fix
- Per-Call Media Tap Contract
- Module Registry (IVoipClientModule)
- PreferredAudioCodecs Codec Preference
- Symmetric RTP (Comedia) NAT Traversal
- Call State Machine
- RTP Receive Path
- RTP Send Path
- Portal Changelog Highlights
- Demo App Walkthrough
- Getting Started Guide
- Making Calls Guide
- Media & Audio Guide
- Per-Call Media Tap (Guide)
- Recording & Playback (WAV/MP3)
- Commercial Plugins (Callora.*)
- Docs Portal Landing Page
- Sovereign Telephony Core
- Commercial Plugin Line-up (Callora.Realtime/WebSocket/Privacy/Risk/Intelligence)
- DDD-Oriented Architecture
- Per-Call Media Tap
- Module Registry (client.Modules)
- VoipClient Public Facade

## God Nodes (most connected - your core abstractions)
1. `CalloraVoipSdk.Core.Infrastructure.Sip.Signaling` - 81 edges
2. `CalloraVoipSdk.Core.Application.Media` - 78 edges
3. `StunMessage` - 74 edges
4. `CalloraVoipSdk.Core.Domain.Calls` - 73 edges
5. `ICall` - 73 edges
6. `SipCallSession` - 73 edges
7. `SipTransportProtocol` - 70 edges
8. `LinuxAudioDevice` - 69 edges
9. `SipCoreCallChannel` - 69 edges
10. `SipRequest` - 68 edges

## Surprising Connections (you probably didn't know these)
- `Dependency Rule` --rationale_for--> `DocFX API Filter Rules`  [INFERRED]
  docs/portal/architecture/overview.md → filterConfig.yml
- `ThrowingModule` --implements--> `IVoipClientModule`  [EXTRACTED]
  tests/CalloraVoipSdk.Core.IntegrationTests/VoipClientModuleRegistrationSafetyTests.cs → src/Client/Application/Modules/IVoipClientModule.cs
- `RecordingMediaSession` --implements--> `ICallMediaSession`  [EXTRACTED]
  tests/CalloraVoipSdk.Core.IntegrationTests/QosMetricsTests.cs → src/Core/Application/Media/ICallMediaSession.cs
- `CallMediaTapContractTests` --references--> `Call`  [EXTRACTED]
  tests/CalloraVoipSdk.Core.IntegrationTests/CallMediaTapContractTests.cs → src/Core/Domain/Calls/Call.cs
- `FakePhoneLine` --references--> `LineId`  [EXTRACTED]
  tests/CalloraVoipSdk.Core.IntegrationTests/CallMediaTapContractTests.cs → src/Core/Domain/Lines/LineId.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Docs Portal Navigation** — docs_portal_index_landing, docs_portal_guides_getting_started_guide, docs_portal_guides_making_calls_guide, docs_portal_guides_media_and_audio_guide, docs_portal_architecture_overview_doc, docs_portal_examples_demo_walkthrough_doc [EXTRACTED 1.00]
- **NuGet Package Family (packed and published together)** — readme_calloravoipsdk, readme_calloravoipsdk_core, readme_audio_windows, readme_audio_linux [EXTRACTED 1.00]

## Communities (242 total, 43 thin omitted)

### Community 0 - "ICE Agent Negotiation"
Cohesion: 0.06
Nodes (44): FakeStunProbe, FakeTurnAllocator, CallIceAgent, CancellationToken, IEnumerable, ILogger, int, IPEndPoint (+36 more)

### Community 1 - "STUN Attribute Codec"
Cohesion: 0.05
Nodes (34): CalloraVoipSdk.Core.Infrastructure.Stun.Attributes, AccessTokenAttribute, ReadOnlyMemory, AlternateServerAttribute, IPEndPoint, ChangeRequestAttribute, ErrorCodeAttribute, FingerprintAttribute (+26 more)

### Community 2 - "SIP Infrastructure Namespaces"
Cohesion: 0.14
Nodes (4): CalloraVoipSdk.Core.Infrastructure.Sip.Wire, CalloraVoipSdk.Core.Infrastructure.Common.Protocols, CalloraVoipSdk.Core.Infrastructure.Sip.Signaling, CalloraVoipSdk.Core.IntegrationTests

### Community 3 - "TURN Server Core"
Cohesion: 0.15
Nodes (14): TurnServer, CancellationToken, CancellationTokenSource, ConcurrentDictionary, ILogger, int, IPEndPoint, SemaphoreSlim (+6 more)

### Community 4 - "VoipClient Facade & Lines"
Cohesion: 0.05
Nodes (28): IVoipClient, Func, VoipClient, bool, CancellationToken, Exception, Func, IDisposable (+20 more)

### Community 5 - "STUN/TURN Server Modules"
Cohesion: 0.04
Nodes (37): CalloraVoipSdk.Core.Infrastructure.Stun.Messages, CalloraVoipSdk.Core.Infrastructure.Stun.Auth, CalloraVoipSdk.Core.Infrastructure.Turn.Wire, CalloraVoipSdk.Core.Infrastructure.Turn.Client, CalloraVoipSdk.Core.Infrastructure.Stun.Server, CalloraVoipSdk.Core.Infrastructure.Turn.Attributes, CalloraVoipSdk.Core.Infrastructure.Turn.Server, CalloraVoipSdk.Core.Infrastructure.Stun.Wire (+29 more)

### Community 6 - "TURN Client Authentication"
Cohesion: 0.18
Nodes (17): EffectiveCredentials, Response, TurnClient, CancellationToken, Func, ILogger, int, IPEndPoint (+9 more)

### Community 7 - "SIP Wire Protocol"
Cohesion: 0.07
Nodes (18): char, IReadOnlyList, SipHeaderNames, SipHeaderRowRules, HashSet, SipHeaderValueStorage, IReadOnlyList, IReadOnlyList (+10 more)

### Community 8 - "Linux Audio Device"
Cohesion: 0.06
Nodes (16): ConcurrentQueue, IntPtr, LinuxAudioDevice, ActiveCodec, bool, float, G722CodecState, int (+8 more)

### Community 9 - "SIP Stream Framing & Perf"
Cohesion: 0.09
Nodes (18): SipStreamConnection, Action, byte, CancellationToken, CancellationTokenSource, Func, ILogger, int (+10 more)

### Community 10 - "Call Domain Model"
Cohesion: 0.05
Nodes (31): EventArgs, QualityManager, Action, IDisposable, MediaActivity, DateTimeOffset, int, long (+23 more)

### Community 11 - "Media Playback & Recording"
Cohesion: 0.09
Nodes (29): CancellationToken, Task, IRecordingModule, CancellationToken, IReadOnlyCollection, Task, CorePlaybackModule, CoreRecordingModule (+21 more)

### Community 12 - "SIP Transport & TLS"
Cohesion: 0.17
Nodes (8): TlsConfiguration, X509Certificate2, ISipTransportFactory, ILoggerFactory, SipTransportFactory, ILoggerFactory, RecordingTransportFactory, ILoggerFactory

### Community 13 - "TURN Attribute Handling"
Cohesion: 0.13
Nodes (14): StunMessage, IReadOnlyList, CancellationToken, Task, TurnAuthOptions, TurnClientContext, TurnServerRequestAuthenticator, ReadOnlySpan (+6 more)

### Community 14 - "SIP Dialog Transactions"
Cohesion: 0.10
Nodes (17): IPEndPoint, SipCallSessionTransactionService, CancellationToken, Dictionary, HashSet, int, IPEndPoint, IReadOnlyDictionary (+9 more)

### Community 15 - "Windows Audio Device"
Cohesion: 0.08
Nodes (13): BufferedWaveProvider, WindowsAudioDevice, ActiveCodec, bool, float, G722CodecState, int, IReadOnlyDictionary (+5 more)

### Community 16 - "RTP Media Session"
Cohesion: 0.07
Nodes (15): RtpCallMediaSession, bool, byte, CancellationTokenSource, DateTimeOffset, double, int, IReadOnlyDictionary (+7 more)

### Community 17 - "Domain Events & Convenience"
Cohesion: 0.09
Nodes (11): CalloraVoipSdk.Modules, CalloraVoipSdk.Core.Application.Calls, CalloraVoipSdk.Core.Domain.Calls, CalloraVoipSdk.Core.Application.Convenience, CalloraVoipSdk.Core.Application.Lines, CalloraVoipSdk.Core.Domain.Events, CalloraVoipSdk.Core.Domain.Lines, CallConnectStatus (+3 more)

### Community 18 - "SIP Request Handling"
Cohesion: 0.06
Nodes (21): SipCallSessionInitialization, SipDialogManager, Dictionary, IReadOnlyList, object, string, SipDialogPath, IReadOnlyList (+13 more)

### Community 19 - "SIP Call Channel"
Cohesion: 0.12
Nodes (11): SipCoreCallChannel, Action, ILogger, int, IPAddress, IReadOnlyList, List, object (+3 more)

### Community 20 - "Call Media Orchestration"
Cohesion: 0.11
Nodes (11): ActiveMediaEntry, CallMediaOrchestrator, bool, ConcurrentDictionary, ILogger, ILoggerFactory, Task, CallMediaRuntimeMetrics (+3 more)

### Community 21 - "SIP Call Session"
Cohesion: 0.07
Nodes (17): SipCallSession, bool, CancellationToken, ILogger, int, IPEndPoint, IReadOnlyList, object (+9 more)

### Community 22 - "Phone Lines & Dialing"
Cohesion: 0.10
Nodes (16): DialOptions, IReadOnlyDictionary, TimeSpan, ILineChannel, Action, CancellationToken, Task, PhoneLine (+8 more)

### Community 23 - "STUN Server Handlers"
Cohesion: 0.10
Nodes (24): IStunRequestHandler, IPEndPoint, ReadOnlySpan, StunServer, byte, CancellationToken, CancellationTokenSource, ConcurrentDictionary (+16 more)

### Community 24 - "TURN Allocations"
Cohesion: 0.20
Nodes (10): ConcurrentDictionary, ReadOnlyMemory, TurnServerAllocation, CancellationTokenSource, ConcurrentDictionary, DateTimeOffset, IPEndPoint, TcpListener (+2 more)

### Community 25 - "SDK Options & ICE Config"
Cohesion: 0.08
Nodes (15): CalloraVoipSdk.Core.Application.Ports.Connectivity, CalloraVoipSdk, IceRelayAllocation, IPEndPoint, TimeSpan, IceTelemetryEvent, DateTimeOffset, IReadOnlyDictionary (+7 more)

### Community 26 - "RTCP Quality Monitoring"
Cohesion: 0.10
Nodes (19): CallMediaRtpSnapshot, CallRtcpQualityMonitor, bool, CancellationToken, CancellationTokenSource, DateTimeOffset, double, ILogger (+11 more)

### Community 27 - "SIP Call Signaling"
Cohesion: 0.07
Nodes (22): ISet, SipCallSessionDependencies, ILogger, SipInviteRequest, IReadOnlyList, SipTransportProtocol, TimeSpan, SipSessionSdpProvider (+14 more)

### Community 28 - "SIP Telemetry & CDR"
Cohesion: 0.07
Nodes (19): CalloraVoipSdk.Core.Infrastructure.Common.Collections, ClientTelemetrySink, BoundedRingBuffer, int, object, InMemorySipTelemetrySink, int, IReadOnlyCollection (+11 more)

### Community 29 - "STUN Token Auth RFC7635"
Cohesion: 0.06
Nodes (24): KeySizes, InMemoryStunThirdPartyKeyProvider, IReadOnlyDictionary, IStunAccessTokenValidator, ReadOnlyMemory, IStunThirdPartyKeyProvider, Rfc7635AccessTokenValidator, bool (+16 more)

### Community 30 - "Signaling Registration ExecuteRegisterAsync"
Cohesion: 0.06
Nodes (32): RegisterMode, IEnumerable, ISipRegistrationService, CancellationToken, Task, SipRegistrationRequest, SipTransportProtocol, TimeSpan (+24 more)

### Community 31 - "Ports Audio SilenceAudioDevice"
Cohesion: 0.06
Nodes (6): AudioDeviceFormat, AudioDeviceRuntimeSnapshot, IAudioDeviceRuntimeControl, IReadOnlyList, SilenceAudioDevice, IReadOnlyList

### Community 32 - "portal architecture CalloraVoipSdk SDK"
Cohesion: 0.38
Nodes (7): Documentation Build Workflow, NuGet Packages Workflow, Release Documentation Workflow, DocFX API Filter Rules, CalloraVoipSdk.Audio.Linux Package, CalloraVoipSdk.Audio.Windows Package, CalloraVoipSdk.Core Package

### Community 33 - "Signaling Dialogs ISipCallSessionContext"
Cohesion: 0.10
Nodes (15): ISipCallSessionContext, ILogger, IPEndPoint, IReadOnlyList, TimeSpan, SipCallSessionHeaderService, Dictionary, SipCallSessionInboundService (+7 more)

### Community 34 - "Domain Calls ICallChannel"
Cohesion: 0.10
Nodes (10): CallAudioFrame, CallChannelCallbacks, CallDirection, ICallChannel, Action, CancellationToken, Func, IReadOnlyList (+2 more)

### Community 35 - "Domain Calls Call"
Cohesion: 0.16
Nodes (12): Call, bool, CancellationToken, DateTimeOffset, Exception, ILogger, int, IReadOnlyList (+4 more)

### Community 36 - "Application Media IMediaSender"
Cohesion: 0.10
Nodes (13): CancellationToken, Task, MediaConnection, CancellationTokenSource, Channel, int, Task, MediaFrameReceivedEventArgs (+5 more)

### Community 37 - "CalloraVoipSdk.Core.IntegrationTests StubSession"
Cohesion: 0.15
Nodes (9): AdvertisedMediaAddressResolverTests, StubSession, CancellationToken, Fact, Func, IPAddress, IPEndPoint, IReadOnlyList (+1 more)

### Community 38 - "Signaling Ingress SubscribeAsync"
Cohesion: 0.09
Nodes (20): ISipDigestAuthenticator, SipSubscriptionHandle, CancellationToken, Func, int, Task, ValueTask, SipNotifyReceivedEventArgs (+12 more)

### Community 39 - "CalloraVoipSdk.Client.Tests ModuleRegistryTests"
Cohesion: 0.30
Nodes (4): SdkConfiguration, ModuleRegistryTests, Fact, Task

### Community 40 - "Media Sessions IAsyncDisposable"
Cohesion: 0.07
Nodes (21): Action, CancellationToken, IDisposable, Task, MediaFrame, ConferencePlaybackFrameSink, bool, CancellationToken (+13 more)

### Community 41 - "Infrastructure Media IAudioFileCodec"
Cohesion: 0.08
Nodes (26): short, EmptyAudioFileCodecRegistry, AudioFileCodecContext, AudioFileFrame, IAudioFileCodec, CancellationToken, ValueTask, IAudioFileReader (+18 more)

### Community 42 - "Sdp Models SdpMediaDescription"
Cohesion: 0.10
Nodes (12): SdpCryptoAttribute, SdpFingerprint, SdpFmtpAttribute, SdpIceCandidate, SdpMediaDescription, IReadOnlyList, SdpMediaDirection, MediaBuilder (+4 more)

### Community 43 - "Stun Server StunMessage"
Cohesion: 0.12
Nodes (11): InMemoryStunCredentialProvider, IReadOnlyList, IStunCredentialProvider, IStunNonceManager, StunBindingRequestHandler, ILogger, IPEndPoint, IReadOnlyList (+3 more)

### Community 44 - "CalloraVoipSdk.Core.IntegrationTests CapturingSipTransportRuntime"
Cohesion: 0.06
Nodes (28): SipSignalingFormat, IPEndPoint, CapturedSipRequest, CapturingSipTransportRuntime, Action, CancellationToken, Dictionary, Func (+20 more)

### Community 45 - "Rtp Session RtpSession"
Cohesion: 0.11
Nodes (17): ValueTask, RtpSenderStatisticsSnapshot, RtpSession, CancellationToken, Dictionary, ILogger, int, IPEndPoint (+9 more)

### Community 46 - "Signaling Dialogs SipResponse"
Cohesion: 0.50
Nodes (3): SipInviteSuccessAckTests, Fact, Task

### Community 47 - "Signaling Reliability SipReliableProvisionalManager"
Cohesion: 0.08
Nodes (17): SipReliableProvisionalEntry, CancellationToken, Task, TaskCompletionSource, SipReliableProvisionalManager, CancellationToken, CancellationTokenSource, Dictionary (+9 more)

### Community 48 - "Media Sessions CalloraVoipSdk.Core.Application.Media"
Cohesion: 0.09
Nodes (4): CalloraVoipSdk.Core.Application.Media, CalloraVoipSdk.Core.Application.Media.Sessions, CalloraVoipSdk.Core.Infrastructure.Media, CalloraVoipSdk.Core.Application.Ports.Media

### Community 49 - "Rtcp Packets CalloraVoipSdk.Core.Application.Media.Rtcp.Packets"
Cohesion: 0.10
Nodes (17): CalloraVoipSdk.Core.Application.Media.Rtcp.Wire, CalloraVoipSdk.Core.Application.Media.Rtcp.Packets, RtcpByePacket, IReadOnlyList, RtcpPacket, RtcpPacketType, RtcpReceiverReport, IReadOnlyList (+9 more)

### Community 50 - "Application Media CallAudioFrame"
Cohesion: 0.24
Nodes (5): MediaReceiver, Action, bool, object, Action

### Community 51 - "Turn Server TurnTcpConnectionBroker"
Cohesion: 0.07
Nodes (24): TurnTcpConnectionBroker, CancellationToken, ConcurrentDictionary, DateTimeOffset, Guid, ILogger, int, IPEndPoint (+16 more)

### Community 52 - "Signaling Ingress TryParseSipUri"
Cohesion: 0.33
Nodes (3): SipInitialRequestRoutingPlanner, IReadOnlyList, SipOutboundInviteTarget

### Community 53 - "Media Sessions AudioFileFormat"
Cohesion: 0.06
Nodes (28): IOpusDecoder, IOpusEncoder, AudioPayloadTranscoder, ReadOnlySpan, string, AudioPayloadTranscodingPlan, Func, BridgeAudioTranscoder (+20 more)

### Community 54 - "Media Sessions PlaybackSession"
Cohesion: 0.15
Nodes (13): MediaSessionState, PlaybackOptions, TimeSpan, PlaybackSession, bool, CancellationToken, CancellationTokenSource, Exception (+5 more)

### Community 55 - "Sip Adapters SipLineChannel"
Cohesion: 0.05
Nodes (29): LearnedPublicContact, ReregisterOptions, TimeSpan, SipTransport, SipLineChannel, Action, CancellationToken, CancellationTokenSource (+21 more)

### Community 56 - "Sip Routing SipTransportProtocol"
Cohesion: 0.12
Nodes (18): LookupClient, ISipRouteResolver, CancellationToken, Task, SipDnsRouteResolver, CancellationToken, ILogger, IPAddress (+10 more)

### Community 57 - "Media Sessions RecordingSession"
Cohesion: 0.17
Nodes (14): RecordingSession, bool, CancellationToken, CancellationTokenSource, Channel, Exception, Guid, ILogger (+6 more)

### Community 58 - "Infrastructure Media Mp3PassthroughReader"
Cohesion: 0.14
Nodes (10): Mp3FrameHeader, Mp3FrameParser, int, ReadOnlySpan, Mp3PassthroughWriter, bool, CancellationToken, FileStream (+2 more)

### Community 59 - "Rtp JitterBuffer RtpPacket"
Cohesion: 0.11
Nodes (10): IJitterBuffer, DateTimeOffset, JitterBufferAddResult, RtpExtension, ReadOnlyMemory, RtpPacket, IReadOnlyList, ReadOnlyMemory (+2 more)

### Community 60 - "Signaling Subscriptions SipSubscriptionLifecycleManager"
Cohesion: 0.11
Nodes (13): SipSubscriptionIdentifier, SipSubscriptionLease, CancellationTokenSource, DateTimeOffset, SipSubscriptionLifecycleManager, CancellationTokenSource, Dictionary, Func (+5 more)

### Community 61 - "Sip Transactions SipClientTransactionExecutor"
Cohesion: 0.20
Nodes (5): SipClientTransactionExecutor, IDisposable, ILogger, IReadOnlyDictionary, TimeSpan

### Community 62 - "Sip Adapters CalloraVoipSdk.Core.Security"
Cohesion: 0.17
Nodes (9): CalloraVoipSdk.Core.Infrastructure.Sdp, CalloraVoipSdk.Core.Infrastructure.Sip.Adapters, CalloraVoipSdk.Core.Security, CalloraVoipSdk.Core.Application.Ports.Sdp, CalloraVoipSdk.Core.Infrastructure.Sip.Observability, SrtpDecisionReasonCodes, string, MediaPublicationResult (+1 more)

### Community 63 - "CalloraVoipSdk.Conferencing.Performance Program"
Cohesion: 0.13
Nodes (10): CalloraVoipSdk.Conferencing.Performance, BenchmarkResult, BenchmarkSnapshot, CliOptions, double, Program, Action, double (+2 more)

### Community 64 - "Stun Client StunClient"
Cohesion: 0.20
Nodes (15): StunBindingResult, IPEndPoint, StunClient, CancellationToken, ILogger, int, IPEndPoint, RemoteCertificateValidationCallback (+7 more)

### Community 65 - "Windows Infrastructure VoipClient.cs"
Cohesion: 0.12
Nodes (10): CalloraVoipSdk.Audio.Windows, CalloraVoipSdk.Audio.Linux, CalloraVoipSdk.Audio.Abstractions.Domain.Devices, CalloraVoipSdk.Core.Infrastructure.Audio, CalloraVoipSdk.Core.Application.Ports.Audio, CalloraVoipSdk.Audio.Headless, AudioDeviceOptions, ActiveCodec (+2 more)

### Community 66 - "Srtp Context CalloraVoipSdk.Core.Infrastructure.Rtp.Packets"
Cohesion: 0.14
Nodes (11): CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer, CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire, CalloraVoipSdk.Core.Infrastructure.Rtp.Wire, CalloraVoipSdk.Core.Infrastructure.Rtp.Packets, CalloraVoipSdk.Core.Infrastructure.Srtp.Context, CalloraVoipSdk.Core.Infrastructure.Rtp, CalloraVoipSdk.Core.Infrastructure.Rtp.Session, CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto (+3 more)

### Community 67 - "Sip Transport SipTransportRuntime"
Cohesion: 0.15
Nodes (19): HttpListenerContext, IPEndPoint, SipTransportProtocol, SipTransportRuntime, CancellationToken, CancellationTokenSource, ConcurrentDictionary, ILogger (+11 more)

### Community 68 - "CalloraVoipSdk.Core.IntegrationTests AckTestSipCallSessionContext"
Cohesion: 0.07
Nodes (13): SipCallSessionTransactionUtilities, IDictionary, IReadOnlyDictionary, SipDialogState, SipDialogStateChangedEventArgs, SipDialogTerminationReason, SipReasonHeader, AckTestSipCallSessionContext (+5 more)

### Community 69 - "CalloraVoipSdk.Core.IntegrationTests BuildContactUri"
Cohesion: 0.04
Nodes (40): 1. SDK Initialization, 2. Registration, 3. Audio Device Binding, 4. Interactive Call Control, 5. Incoming Call Handling, Cleanup, Demo Walkthrough, What It Demonstrates (+32 more)

### Community 70 - "Application Convenience DefaultAudioCallAttachment"
Cohesion: 0.10
Nodes (12): DefaultAudioCallAttachment, Action, bool, ILogger, object, IMediaReceiver, IMediaSender, MediaConnector (+4 more)

### Community 71 - "CalloraVoipSdk.Core.IntegrationTests SrtpHardeningTests"
Cohesion: 0.17
Nodes (7): RtpPacketCodec, byte, int, ReadOnlySpan, SrtpHardeningTests, Fact, Task

### Community 72 - "Signaling Contracts ISipServerTransactionEngine"
Cohesion: 0.11
Nodes (11): Action, Exception, SipServerTransactionKey, SipServerTransactionRegistration, NoopSipServerTransactionEngine, Action, CancellationToken, Exception (+3 more)

### Community 73 - "Signaling Dialogs SipCallSessionContextAdapter"
Cohesion: 0.28
Nodes (11): StunCredentials, ITurnClient, CancellationToken, IPEndPoint, ReadOnlyMemory, RemoteCertificateValidationCallback, Task, TurnAllocateResult (+3 more)

### Community 74 - "Turn Client TurnTcpDataConnectionFactory"
Cohesion: 0.23
Nodes (8): TurnTcpDataConnectionFactory, CancellationToken, ILogger, IPEndPoint, RemoteCertificateValidationCallback, Stream, Task, TcpClient

### Community 75 - "CalloraVoipSdk.ArchitectureTests CsFiles"
Cohesion: 0.19
Nodes (8): CalloraVoipSdk.ArchitectureTests, Lazy, EngineeringRulesTests, Fact, string, SourceScan, IEnumerable, IReadOnlyCollection

### Community 76 - "Common Timing ScheduledActionScheduler"
Cohesion: 0.12
Nodes (15): PriorityQueue, ScheduledActionScheduler, Action, CancellationToken, CancellationTokenSource, Dictionary, IDisposable, ILogger (+7 more)

### Community 77 - "Rtp JitterBuffer JitterBuffer"
Cohesion: 0.15
Nodes (10): SortedDictionary, JitterBuffer, bool, DateTimeOffset, double, long, object, uint (+2 more)

### Community 78 - "Application Facades IVoipClient"
Cohesion: 0.08
Nodes (21): CancellationToken, Obsolete, Task, Obsolete, ConnectOptions, TimeSpan, ConnectResult, Exception (+13 more)

### Community 79 - "Application Convenience SdkConvenienceOrchestrator"
Cohesion: 0.19
Nodes (10): CallConnectOutcome, LineConnectOutcome, SdkConvenienceOrchestrator, CancellationToken, ConcurrentDictionary, ILogger, ILoggerFactory, int (+2 more)

### Community 80 - "Sdp OfferAnswer SdpCodecDefinition"
Cohesion: 0.24
Nodes (5): IPEndPoint, SdpOfferAnswerNegotiator, IPEndPoint, IReadOnlyList, List

### Community 81 - "Linux Infrastructure AudioDeviceDescriptor"
Cohesion: 0.17
Nodes (5): IReadOnlyList, IReadOnlyList, IReadOnlyList, IReadOnlyList, AudioDeviceDescriptor

### Community 82 - "Ports Sdp SdpMediaNegotiationOptions"
Cohesion: 0.24
Nodes (4): ISdpNegotiator, IPEndPoint, NoopSdpNegotiator, IPEndPoint

### Community 83 - "Signaling Dialogs SendReliableProvisionalAndWaitForPrackAsync"
Cohesion: 0.15
Nodes (14): SipCallSessionUtilities, Action, CancellationToken, Func, ILogger, IPEndPoint, SemaphoreSlim, Task (+6 more)

### Community 84 - "Signaling SessionTimers SipSessionTimerManager"
Cohesion: 0.17
Nodes (10): SipSessionTimerManager, bool, CancellationToken, CancellationTokenSource, Func, ILogger, int, object (+2 more)

### Community 85 - "Sip Transport IPEndPoint"
Cohesion: 0.24
Nodes (5): HttpListener, SipTransportRuntimeUtilities, IPEndPoint, IReadOnlyDictionary, Uri

### Community 86 - "CalloraVoipSdk.Core.IntegrationTests OpusCodecTests"
Cohesion: 0.21
Nodes (7): BenchmarkResult, BenchmarkSnapshot, Program, Action, double, ICollection, JsonSerializerOptions

### Community 87 - "Infrastructure Media IAudioFileWriter"
Cohesion: 0.11
Nodes (14): ProcessStartInfo, IAudioFileWriter, CancellationToken, ValueTask, FfmpegProcessRunner, Action, CancellationToken, Task (+6 more)

### Community 88 - "Sip Wire SipProtocol"
Cohesion: 0.12
Nodes (6): SipProtocol, Dictionary, HashSet, IPEndPoint, string, SipUriComponents

### Community 89 - "Stun Client StunTransport"
Cohesion: 0.09
Nodes (16): CalloraVoipSdk.Core.Infrastructure.Stun.Client, IStunServerResolver, CancellationToken, IPEndPoint, Task, StunChallengeException, StunException, StunServerResolver (+8 more)

### Community 90 - "Stun Client IceServerConfiguration"
Cohesion: 0.08
Nodes (23): IStunClient, CancellationToken, IPEndPoint, RemoteCertificateValidationCallback, Socket, Task, StunIceProbe, AddressFamily (+15 more)

### Community 91 - "CalloraVoipSdk.Core.IntegrationTests ApplyObserved"
Cohesion: 0.18
Nodes (8): Changed, NatPublicContactState, Host, Port, Host, Port, SipRportContactTests, Fact

### Community 92 - "Turn Client CalloraVoipSdk.Core.Infrastructure.Turn.Client"
Cohesion: 0.20
Nodes (8): TurnTcpDataConnection, CancellationToken, Memory, ReadOnlyMemory, Stream, Task, TcpClient, ValueTask

### Community 93 - "Transactions Server SipServerTransactionEngine"
Cohesion: 0.10
Nodes (22): Action, TimeSpan, SipServerTransactionEngine, Action, CancellationToken, ConcurrentDictionary, DateTimeOffset, Exception (+14 more)

### Community 94 - "Rtcp Wire RtcpPacketCodec"
Cohesion: 0.25
Nodes (4): RtcpPacketCodec, IReadOnlyList, ReadOnlySpan, Span

### Community 95 - "Signaling Contracts ISipCallSession"
Cohesion: 0.16
Nodes (5): ISipCallSession, CancellationToken, IPEndPoint, IReadOnlyList, Task

### Community 96 - "Srtp Context ISrtpContext"
Cohesion: 0.13
Nodes (11): Inbound, Outbound, ILogger, RtpSessionOptions, IPEndPoint, ISrtpContext, ReadOnlySpan, RtpSymmetricLatchTests (+3 more)

### Community 97 - "CalloraVoipSdk.Core.IntegrationTests TryBuildNegotiatedAnswer"
Cohesion: 0.13
Nodes (11): SdpIceNegotiationOptions, SdpMediaNegotiationOptions, IReadOnlyList, ISdpSessionSerializer, SdpNegotiator, IPEndPoint, IPEndPoint, SdpCodecPreferenceTests (+3 more)

### Community 98 - "CalloraVoipSdk.Core.IntegrationTests IsForThisLine"
Cohesion: 0.18
Nodes (8): TrunkInboundMatcher, IPAddress, IReadOnlyCollection, TrunkInboundMatcherTests, Fact, IPAddress, IReadOnlyCollection, string

### Community 99 - "Transactions Server SendResponseAsync"
Cohesion: 0.16
Nodes (3): CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server, CalloraVoipSdk.Core.Infrastructure.Sip.Authentication, NoopSipDigestAuthenticator

### Community 100 - "Srtp Context SrtpContext"
Cohesion: 0.17
Nodes (8): SrtpContext, bool, int, object, ReadOnlySpan, Span, SrtpSessionKeys, ulong

### Community 101 - "CalloraVoipSdk.Core.IntegrationTests SrtpContextTests"
Cohesion: 0.24
Nodes (6): SrtpContextTests, byte, Fact, int, ReadOnlySpan, Span

### Community 102 - "Stun Client DnsSrvQuery"
Cohesion: 0.16
Nodes (9): DnsSrvQuery, CancellationToken, int, IPEndPoint, IReadOnlyList, Task, TimeSpan, ushort (+1 more)

### Community 103 - "Infrastructure Media WavAudioFileReader"
Cohesion: 0.18
Nodes (10): DataLength, DataStart, SampleRate, WavAudioFileReader, bool, CancellationToken, FileStream, int (+2 more)

### Community 104 - "Srtp Crypto SrtpCryptoSuite"
Cohesion: 0.19
Nodes (7): Selection, SdesCryptoSelector, Selection, IReadOnlyList, SrtpCryptoSuite, SrtpCryptoSuiteNames, int

### Community 105 - "Application Calls SrtpPolicy"
Cohesion: 0.11
Nodes (10): AnswerSdp, MediaPublicationResult, Parameters, ResolvedSrtpPolicy, SrtpPolicyEvaluator, CallMediaParameters, IPEndPoint, IReadOnlyDictionary (+2 more)

### Community 106 - "CalloraVoipSdk.Core.IntegrationTests RecordingRegistrationService"
Cohesion: 0.12
Nodes (8): CalloraVoipSdk.Core.Infrastructure.Sip.Transport, CalloraVoipSdk.Core.Infrastructure.Sip.Routing, ISipIdentityTrustPolicy, SipSubscribeRequest, TimeSpan, DenyAllSipIdentityTrustPolicy, VoipClientModuleRegistrationSafetyTests, Fact

### Community 107 - "Signaling Dialogs SipDialogManager"
Cohesion: 0.10
Nodes (15): BridgeAudioFormat, SdkConfiguration, ILoggerFactory, IReadOnlyList, IServiceProvider, TimeSpan, SdkOptions, ILoggerFactory (+7 more)

### Community 108 - "Turn Client TryAllocateRelayAsync"
Cohesion: 0.11
Nodes (18): Architecture Gates (ENGINEERING_RULES), CI Workflow (build-and-test), Architecture, Build and test, CalloraVoipSdk, Commercial plugins (private, paid — in development), Contributing, Current feature set (+10 more)

### Community 109 - "CalloraVoipSdk.Core.IntegrationTests SdpSdesAnswerTests"
Cohesion: 0.39
Nodes (4): SdpSdesAnswerTests, Fact, IPEndPoint, string

### Community 110 - "CalloraVoipSdk.Core.IntegrationTests FakeInboundSession"
Cohesion: 0.26
Nodes (5): FakeInboundSession, CancellationToken, IPEndPoint, IReadOnlyList, Task

### Community 111 - "Sdp OfferAnswer CalloraVoipSdk.Core.Infrastructure.Sdp.Models"
Cohesion: 0.12
Nodes (14): CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer, CalloraVoipSdk.Core.Infrastructure.Sdp.Models, CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing, SdpSessionDescription, IReadOnlyList, ISdpOfferAnswerNegotiator, IPEndPoint, IReadOnlyList (+6 more)

### Community 112 - "Transactions Server IDisposable"
Cohesion: 0.10
Nodes (11): EventHandler, IDisposable, IncomingCallSubscription, QualitySubscription, Action, CompositeDisposable, int, DelegateDisposable (+3 more)

### Community 113 - "Sip Transport SipWebSocketConnection"
Cohesion: 0.15
Nodes (12): SipWebSocketConnection, Action, CancellationToken, CancellationTokenSource, Func, ILogger, int, IPEndPoint (+4 more)

### Community 114 - "CalloraVoipSdk.Core.IntegrationTests SrtpMediaPathTests"
Cohesion: 0.30
Nodes (6): SrtpMediaPathTests, Fact, int, IPEndPoint, string, Task

### Community 115 - "Infrastructure DependencyInjection CalloraVoipSdk.DependencyInjection"
Cohesion: 0.18
Nodes (7): Action, IServiceCollection, SdkOptions, CalloraBuilder, Action, IceOptions, IServiceCollection

### Community 116 - "Application Workflows CallState"
Cohesion: 0.19
Nodes (7): DialResult, Exception, DialStatus, DialWaitOptions, TimeSpan, CallState, CallStateRules

### Community 117 - "Common Protocols SplitCommaSeparatedRespectingQuotes"
Cohesion: 0.17
Nodes (5): ProtocolCommonUtilities, SipDigestAuthentication, Dictionary, ReliableProvisionalOptInTests, Fact

### Community 118 - "CalloraVoipSdk.Core.IntegrationTests SipRegistrationExpiresTests"
Cohesion: 0.12
Nodes (11): TurnStreamConnectionExtensions, TurnServerTransport, TurnStreamConnection, CancellationToken, Guid, IPEndPoint, ReadOnlyMemory, SemaphoreSlim (+3 more)

### Community 119 - "CalloraVoipSdk.Core.IntegrationTests RecordingMediaSession"
Cohesion: 0.20
Nodes (9): RecordingMediaSession, CancellationToken, IReadOnlyList, List, ReadOnlyMemory, Task, TimeSpan, uint (+1 more)

### Community 120 - "Sip Adapters TryPublishMediaParameters"
Cohesion: 0.24
Nodes (4): CancellationToken, Func, IPEndPoint, Task

### Community 121 - "CalloraVoipSdk.Audio.Tests CalloraVoipSdk.Audio.Tests.csproj"
Cohesion: 0.14
Nodes (12): net10.0, net8.0, net9.0, Microsoft.NET.Sdk, net10.0, net8.0, net9.0, coverlet.collector (6.0.0) (+4 more)

### Community 122 - "Application Media AesGcmRecordingEncryptionProvider"
Cohesion: 0.13
Nodes (10): IRecordingEncryptionProvider, CancellationToken, ValueTask, AesGcmRecordingEncryptionProvider, byte, CancellationToken, int, ReadOnlySpan (+2 more)

### Community 123 - "Infrastructure Sdp SdpUtilities"
Cohesion: 0.21
Nodes (6): SdpCodecDefinition, ISdpSessionParser, SdpSecurityInspector, SdpUtilities, IReadOnlyDictionary, IReadOnlyList

### Community 124 - "Turn Server TurnMobilityTicketStore"
Cohesion: 0.15
Nodes (9): TurnMobilityService, IPEndPoint, ReadOnlySpan, TurnMobilityTicketStore, ConcurrentDictionary, DateTimeOffset, long, ReadOnlySpan (+1 more)

### Community 125 - "Common Timing ScheduledActionHandle"
Cohesion: 0.15
Nodes (8): CalloraVoipSdk.Core.Infrastructure.Common.Timing, IScheduledActionScheduler, ScheduledActionEntry, Action, ScheduledActionHandle, Action, int, long

### Community 126 - "Application Media MediaSender"
Cohesion: 0.19
Nodes (7): MediaSender, bool, CancellationToken, ILogger, object, ReadOnlyMemory, Task

### Community 127 - "Media Sessions PcmG711Codec"
Cohesion: 0.19
Nodes (9): CalloraVoipSdk.Client.Tests, IVoipClientModule, IVoipClient, AttachProbeModule, FakeFeatureModule, IFakeFeature, IOtherFeature, OtherFeatureModule (+1 more)

### Community 128 - "Common Disposal AsyncDisposeAction"
Cohesion: 0.08
Nodes (19): CalloraVoipSdk.Core.Infrastructure.Common.Disposal, IAsyncDisposable, AsyncDisposeAction, Action, int, ValueTask, DisposeAction, Action (+11 more)

### Community 129 - "Core CalloraVoipSdk.Core.csproj"
Cohesion: 0.17
Nodes (11): Concentus (2.2.2), DnsClient (1.8.0), net10.0, net8.0, net9.0, Microsoft.Extensions.DependencyInjection.Abstractions (8.0.0), Microsoft.Extensions.Hosting.Abstractions (8.0.0), Microsoft.Extensions.Logging.Abstractions (8.0.0) (+3 more)

### Community 130 - "Sip Transactions SipResponseEnvelope"
Cohesion: 0.42
Nodes (4): RetransmissionWaitOutcome, CancellationToken, Func, Task

### Community 131 - "CalloraVoipSdk.Core.IntegrationTests QosMetricsTests"
Cohesion: 0.30
Nodes (4): QosMetricsTests, DateTimeOffset, Fact, Func

### Community 132 - "Infrastructure Sdp SdpSessionDescription"
Cohesion: 0.14
Nodes (7): PhoneLineManager, CancellationToken, ConcurrentDictionary, Func, IReadOnlyCollection, Task, LineId

### Community 133 - "CalloraVoipSdk.Core.IntegrationTests Full_chain_encrypts_and_decrypts_via_real_media_session"
Cohesion: 0.47
Nodes (3): SrtpSignalingToMediaE2eTests, Fact, string

### Community 134 - "CalloraVoipSdk.Core.Performance CalloraVoipSdk.sln"
Cohesion: 0.18
Nodes (4): net10.0, net8.0, net9.0, Microsoft.NET.Sdk

### Community 135 - "Media Sessions CallRecordingFrameSource"
Cohesion: 0.20
Nodes (7): CallRecordingFrameSource, bool, CancellationToken, ILogger, ValueTask, CallMediaTapContractTests, Fact

### Community 136 - "Infrastructure Media WavAudioFileWriter"
Cohesion: 0.20
Nodes (8): Span, WavAudioFileWriter, bool, CancellationToken, FileStream, int, uint, ValueTask

### Community 137 - "Signaling Formatting SipSessionTimerPolicy"
Cohesion: 0.29
Nodes (3): SipSessionTimerPolicy, IDictionary, int

### Community 138 - "Srtp Crypto SrtpKeyDerivation"
Cohesion: 0.27
Nodes (5): SrtpKeyDerivation, byte, ReadOnlySpan, SrtpKeyMaterial, ReadOnlyMemory

### Community 139 - "Audio Abstractions CalloraVoipSdk.Audio.Abstractions.csproj"
Cohesion: 0.20
Nodes (8): net10.0, net8.0, net9.0, Microsoft.NET.Sdk, net10.0, net8.0, net9.0, Microsoft.NET.Sdk

### Community 140 - "Application Media ICallMediaSession"
Cohesion: 0.20
Nodes (8): ICallMediaSession, CancellationToken, ReadOnlyMemory, Task, TimeSpan, ICallMediaSessionFactory, RtpCallMediaSessionFactory, ILoggerFactory

### Community 141 - "Rtp Session RtpSequenceValidator"
Cohesion: 0.24
Nodes (6): RtpSequenceResult, RtpSequenceValidator, bool, int, uint, ushort

### Community 142 - "Sdp OfferAnswer NegotiateAnswer"
Cohesion: 0.31
Nodes (7): ISipTransportRuntime, Action, CancellationToken, IPEndPoint, IReadOnlyDictionary, IReadOnlyList, Task

### Community 143 - "CalloraVoipSdk.Core.IntegrationTests StartRegistration"
Cohesion: 0.15
Nodes (11): [0.9.0] - 2026-04-14, [2.0.0] - 2026-07-07, [3.1.1] - 2026-07-08, Added, Added, Changelog, Fixed, Fixed (+3 more)

### Community 144 - "Transactions Server SipServerTransactionKey"
Cohesion: 0.29
Nodes (5): SipDomainCertificateValidator, IReadOnlyList, string, X509Certificate2, X509Extension

### Community 145 - "Sip Transport RequestReceived"
Cohesion: 0.51
Nodes (4): SipWireTraceLogger, ILogger, IPEndPoint, IReadOnlyDictionary

### Community 146 - "Stun Server StunNonceManager"
Cohesion: 0.27
Nodes (5): StunNonceManager, ConcurrentDictionary, DateTimeOffset, long, TimeSpan

### Community 147 - "CalloraVoipSdk.Client.Tests CalloraVoipSdk.Client.Tests.csproj"
Cohesion: 0.20
Nodes (9): net10.0, net8.0, net9.0, coverlet.collector (6.0.0), Microsoft.Extensions.DependencyInjection (8.0.1), Microsoft.NET.Test.Sdk (17.6.0), xunit (2.4.2), xunit.runner.visualstudio (2.4.5) (+1 more)

### Community 148 - "CalloraVoipSdk.Core.IntegrationTests CalloraVoipSdk.Core.IntegrationTests.csproj"
Cohesion: 0.20
Nodes (9): net10.0, net8.0, net9.0, coverlet.collector (6.0.0), Microsoft.Extensions.DependencyInjection (8.0.1), Microsoft.NET.Test.Sdk (17.6.0), xunit (2.4.2), xunit.runner.visualstudio (2.4.5) (+1 more)

### Community 149 - "Infrastructure DependencyInjection CalloraHostedService"
Cohesion: 0.32
Nodes (6): IHostedService, CalloraHostedService, CancellationToken, ILogger, IVoipClient, Task

### Community 150 - "Client CalloraVoipSdk.Client.csproj"
Cohesion: 0.22
Nodes (8): net10.0, net8.0, net9.0, Microsoft.Extensions.DependencyInjection.Abstractions (8.0.0), Microsoft.Extensions.Hosting.Abstractions (8.0.0), Microsoft.Extensions.Logging.Abstractions (8.0.0), Microsoft.Extensions.Options (8.0.2), Microsoft.NET.Sdk

### Community 151 - "Application Media CallMediaRuntimeMetrics"
Cohesion: 0.31
Nodes (7): RecordingTransportRuntime, Action, CancellationToken, IDisposable, IPEndPoint, IReadOnlyDictionary, Task

### Community 152 - "Sip Wire TryFromRequest"
Cohesion: 0.18
Nodes (8): TurnAllocateRequestHandler, AddressFamily, Func, ILogger, IPEndPoint, TcpListener, UdpClient, TurnServerOptions

### Community 153 - "CalloraVoipSdk.SoakTests CalloraVoipSdk.SoakTests.csproj"
Cohesion: 0.22
Nodes (8): net10.0, net8.0, net9.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.6.0), xunit (2.4.2), xunit.runner.visualstudio (2.4.5), Microsoft.NET.Sdk

### Community 154 - "Stun Client CalloraVoipSdk.Core.Infrastructure.Stun.Client"
Cohesion: 0.31
Nodes (6): Assembly, IAudioDeviceProvider, HeadlessAudioDevice, IAudioDevice, PlatformAudioDeviceFactory, ILogger

### Community 155 - "Sip Routing ResolveAsync"
Cohesion: 0.29
Nodes (7): Mp3PassthroughReader, bool, CancellationToken, FileStream, int, Memory, ValueTask

### Community 157 - "Sip Wire ISipWireCodec"
Cohesion: 0.32
Nodes (3): ISipWireCodec, IReadOnlyDictionary, ReadOnlySpan

### Community 160 - "Stun Wire ReadMessageAsync"
Cohesion: 0.39
Nodes (5): StunTcpFramer, CancellationToken, Memory, Stream, Task

### Community 161 - "Application Facades InvalidOperationException"
Cohesion: 0.22
Nodes (5): InvalidOperationException, VoipClientInitializationException, ModuleFeatureUnavailableException, SipFinalResponseException, SipRegistrationFailedException

### Community 162 - "Audio Linux CalloraVoipSdk.Audio.Linux.csproj"
Cohesion: 0.29
Nodes (6): PortAudioSharp2 (1.0.6), net10.0, net8.0, net9.0, NAudio.Core (2.3.0), Microsoft.NET.Sdk

### Community 163 - "Ports Audio AudioConnectionParameters"
Cohesion: 0.22
Nodes (8): SipResponseEnvelope, SipClientTransactionRequest, Action, IPEndPoint, IReadOnlyDictionary, TimeSpan, SipClientTransactionResult, IReadOnlyList

### Community 164 - "Rtp Session IRtpSession"
Cohesion: 0.33
Nodes (6): TurnStreamFrame, TurnStreamFramer, CancellationToken, Memory, Stream, Task

### Community 166 - "CalloraVoipSdk.Core.IntegrationTests StunSharedSocketTests.cs"
Cohesion: 0.29
Nodes (3): CalloraVoipSdk.Performance, CliOptions, double

### Community 167 - "Rtp Profile RtpAvpProfile"
Cohesion: 0.33
Nodes (3): CalloraVoipSdk.Core.Infrastructure.Rtp.Profile, RtpAvpProfile, byte

### Community 168 - "CalloraVoipSdk.Conferencing.Performance CalloraVoipSdk.Conferencing.Performance.csproj"
Cohesion: 0.33
Nodes (4): net10.0, net8.0, net9.0, Microsoft.NET.Sdk

### Community 169 - "Infrastructure DependencyInjection SdkOptionsValidator"
Cohesion: 0.22
Nodes (6): CalloraVoipSdk.DependencyInjection, IValidateOptions, SdkOptionsValidator, SdkOptions, ServiceCollectionExtensions, ValidateOptionsResult

### Community 170 - "Audio Windows CalloraVoipSdk.Audio.Windows.csproj"
Cohesion: 0.33
Nodes (5): NAudio (2.3.0), net10.0, net8.0, net9.0, Microsoft.NET.Sdk

### Community 171 - "Common Network ResolveAsync"
Cohesion: 0.33
Nodes (4): RemoteEndPointResolver, CancellationToken, IPEndPoint, Task

### Community 172 - "Rtcp Wire IRtcpPacketCodec"
Cohesion: 0.50
Nodes (3): IRtcpPacketCodec, IReadOnlyList, ReadOnlySpan

### Community 174 - "Sip Adapters HandleIncomingInvite"
Cohesion: 0.25
Nodes (8): 1. Connect and place a call, 2. Runtime audio device control, 3. Handle inbound calls, 4. Advanced event-driven flow, 5. Manual media control, 6. Bridge two active calls, 7. Pin the audio codec, Quickstart

### Community 175 - "Signaling Contracts ISipCallSignalingService"
Cohesion: 0.36
Nodes (3): SearchValues, SipDigestChallengeSelector, IEnumerable

### Community 177 - "Signaling Formatting TryValidateSdpRequest"
Cohesion: 0.21
Nodes (6): StunWireConstants, int, uint, ushort, TurnXorPeerAddressAttribute, IPEndPoint

### Community 178 - "CalloraVoipSdk.ArchitectureTests CalloraVoipSdk.ArchitectureTests.csproj"
Cohesion: 0.33
Nodes (5): net10.0, Microsoft.NET.Test.Sdk (17.6.0), xunit (2.4.2), xunit.runner.visualstudio (2.4.5), Microsoft.NET.Sdk

### Community 179 - "CalloraVoipSdk.Audio.Tests SmokeTests.cs"
Cohesion: 0.40
Nodes (3): CalloraVoipSdk.Audio.Tests, SmokeTests, Fact

### Community 180 - "CalloraVoipSdk.SoakTests SmokeTests.cs"
Cohesion: 0.40
Nodes (3): CalloraVoipSdk.SoakTests, SmokeTests, Fact

### Community 181 - "CalloraVoipSdk.Media.Performance CalloraVoipSdk.Media.Performance.csproj"
Cohesion: 0.40
Nodes (4): net10.0, net8.0, net9.0, Microsoft.NET.Sdk

### Community 184 - "Domain Lines ReregisterOptions"
Cohesion: 0.29
Nodes (6): 2.0.0 — 2026-07-07, 3.0.0 — 2026-07-07, 3.1.0 — 2026-07-08, 3.1.1 — 2026-07-08, Changelog, Release highlights

### Community 186 - "CalloraVoipSdk.Core.IntegrationTests DelegateDisposable"
Cohesion: 0.33
Nodes (4): AdvertisedMediaAddressResolver, Func, ILogger, IPAddress

### Community 187 - "CalloraVoipSdk.Core.IntegrationTests SipInDialogPublicContactTests"
Cohesion: 0.29
Nodes (5): SipMergedInviteTracker, ConcurrentDictionary, DateTimeOffset, int, TimeSpan

### Community 189 - "Application Managers QualitySubscription"
Cohesion: 0.33
Nodes (5): Architecture Overview, Dependency Rule, Events, Layer Responsibilities, VoipClient

### Community 190 - "Application Media CompositeDisposable"
Cohesion: 0.40
Nodes (4): Call Lifecycle, Important Rules, Inbound Call State Machine, Outbound Call State Machine

### Community 193 - "Common Network ResolveAdvertisedLocalEndPoint"
Cohesion: 0.12
Nodes (6): CalloraVoipSdk.Core.Infrastructure.Common.Network, CalloraVoipSdk.Core.Infrastructure.Sip.Transactions, LocalEndPointAdvertisementResolver, LocalEndPointHostResolver, RegisterMode, RetransmissionWaitOutcome

### Community 195 - "Sip Transport ValidateTlsServerCertificate"
Cohesion: 0.50
Nodes (3): SslPolicyErrors, X509Certificate, X509Chain

### Community 197 - "CalloraVoipSdk.Core.IntegrationTests NoopDisposable"
Cohesion: 0.40
Nodes (4): SipCallSessionConfiguration, IPEndPoint, IReadOnlyList, TimeSpan

### Community 199 - "portal architecture RTP Send Path"
Cohesion: 0.29
Nodes (6): Jitter Buffer, Media Pipeline, Receive Path (Inbound Audio), RTCP, Send Path (Outbound Audio), SRTP

### Community 207 - ".TryDecodeXorAddressValue"
Cohesion: 0.60
Nodes (3): TurnWireAddressCodec, IPEndPoint, ReadOnlySpan

### Community 208 - "[1.0.2] - 2026-04-17"
Cohesion: 0.50
Nodes (4): [1.0.2] - 2026-04-17, Added (previously listed under Unreleased), Changed (previously listed under Unreleased), Deprecated

### Community 209 - "[3.0.0] - 2026-07-07"
Cohesion: 0.50
Nodes (4): [3.0.0] - 2026-07-07, Added, Changed, Removed

### Community 210 - "[3.2.0] - 2026-07-08"
Cohesion: 0.50
Nodes (4): [3.2.0] - 2026-07-08, Added, Changed, Fixed

### Community 211 - "TurnServerChannelBinding"
Cohesion: 0.50
Nodes (3): TurnServerChannelBinding, DateTimeOffset, IPEndPoint

### Community 212 - "[3.1.0] - 2026-07-08"
Cohesion: 0.67
Nodes (3): [3.1.0] - 2026-07-08, Added, Fixed

### Community 213 - "[3.1.2] - 2026-07-08"
Cohesion: 0.67
Nodes (3): [3.1.2] - 2026-07-08, Added, Fixed

### Community 214 - "Installation"
Cohesion: 0.67
Nodes (3): Installation, Local development via `ProjectReference`, NuGet

## Knowledge Gaps
- **230 isolated node(s):** `net8.0`, `net9.0`, `net10.0`, `Microsoft.NET.Sdk`, `net8.0` (+225 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **43 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `CalloraVoipSdk.Core.Domain.Calls` connect `Domain Events & Convenience` to `ICE Agent Negotiation`, `CalloraVoipSdk.Core.IntegrationTests TryBuildNegotiatedAnswer`, `Domain Calls ICallChannel`, `Domain Calls Call`, `Srtp Context CalloraVoipSdk.Core.Infrastructure.Rtp.Packets`, `SIP Infrastructure Namespaces`, `Call Domain Model`, `Media Sessions CalloraVoipSdk.Core.Application.Media`, `Rtcp Packets CalloraVoipSdk.Core.Application.Media.Rtcp.Packets`, `Application Workflows CallState`, `SDK Options & ICE Config`, `SIP Call Signaling`, `Sip Adapters CalloraVoipSdk.Core.Security`, `CalloraVoipSdk.Conferencing.Performance Program`?**
  _High betweenness centrality (0.073) - this node is a cross-community bridge._
- **Why does `CalloraVoipSdk.Core.Infrastructure.Stun.Wire` connect `STUN/TURN Server Modules` to `Stun Wire ReadMessageAsync`, `Signaling Formatting TryValidateSdpRequest`, `Sip Adapters CalloraVoipSdk.Core.Security`, `STUN Server Handlers`?**
  _High betweenness centrality (0.058) - this node is a cross-community bridge._
- **Why does `CalloraVoipSdk.Core.Application.Media` connect `Media Sessions CalloraVoipSdk.Core.Application.Media` to `ICE Agent Negotiation`, `Windows Infrastructure VoipClient.cs`, `Domain Lines SipAddress`, `Srtp Context CalloraVoipSdk.Core.Infrastructure.Rtp.Packets`, `Application Media IMediaSender`, `Application Media AesGcmRecordingEncryptionProvider`, `Application Convenience DefaultAudioCallAttachment`, `Media Sessions IAsyncDisposable`, `Call Domain Model`, `Media Playback & Recording`, `Transactions Server IDisposable`, `Domain Events & Convenience`, `Rtcp Packets CalloraVoipSdk.Core.Application.Media.Rtcp.Packets`, `Call Media Orchestration`, `Media Sessions PlaybackSession`, `SDK Options & ICE Config`, `RTCP Quality Monitoring`, `Sip Adapters CalloraVoipSdk.Core.Security`?**
  _High betweenness centrality (0.056) - this node is a cross-community bridge._
- **What connects `net8.0`, `net9.0`, `net10.0` to the rest of the system?**
  _232 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `ICE Agent Negotiation` be split into smaller, more focused modules?**
  _Cohesion score 0.05590386624869383 - nodes in this community are weakly interconnected._
- **Should `STUN Attribute Codec` be split into smaller, more focused modules?**
  _Cohesion score 0.0519219736087206 - nodes in this community are weakly interconnected._
- **Should `SIP Infrastructure Namespaces` be split into smaller, more focused modules?**
  _Cohesion score 0.13793103448275862 - nodes in this community are weakly interconnected._