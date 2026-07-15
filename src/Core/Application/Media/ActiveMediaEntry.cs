using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Application.Media;

internal sealed record ActiveMediaEntry(
    ICallMediaSession Session,
    CallRtcpQualityMonitor QualityMonitor,
    ICallChannel Channel,
    Action<CallAudioFrame> InboundHandler,
    Action<byte, int> InboundDtmfHandler,
    Action<CallMediaRuntimeMetrics> MetricsHandler,
    Action<CallQualitySnapshot> QualityHandler,
    IVideoMediaStream? Video = null,
    Action<byte[], uint>? InboundVideoHandler = null,
    Action? CongestionHandler = null);
