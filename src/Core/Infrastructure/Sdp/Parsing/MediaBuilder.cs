using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

internal sealed class MediaBuilder
{
    private readonly IReadOnlyList<int> _mLineOrder;

    public MediaBuilder(string mediaType, int port, string profile, IDictionary<int, SdpCodecDefinition> codecs, IReadOnlyList<int> mLineOrder)
    {
        MediaType = mediaType;
        Port = port;
        Profile = profile;
        Codecs = codecs;
        _mLineOrder = mLineOrder;
    }

    public string MediaType { get; }
    public int Port { get; }
    public string Profile { get; }

    /// <summary>Consecutive ports from the m-line's <c>/n</c> suffix; 1 when absent (#160 P2-11).</summary>
    public int PortCount { get; init; } = 1;

    /// <summary>The raw fmt tokens of the m-line, opaque for a non-RTP profile (#160 P2-11).</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>Tracks the at-most-once attributes of this media section (#160 P2-15).</summary>
    public SdpSingletonGuard Singletons { get; } = new();

    public IDictionary<int, SdpCodecDefinition> Codecs { get; }

    public string? ConnectionAddress { get; set; }
    public SdpMediaDirection? Direction { get; set; }
    public int? Ptime { get; set; }
    public int? MaxPtime { get; set; }
    public bool RtcpMux { get; set; }
    public int? RtcpPort { get; set; }
    public string? Mid { get; set; }
    public SdpMsid? Msid { get; set; }
    public SdpBandwidth? Bandwidth { get; set; }
    public string? IceUfrag { get; set; }
    public string? IcePwd { get; set; }
    public string? IceOptions { get; set; }
    public bool EndOfCandidates { get; set; }
    public SdpFingerprint? Fingerprint { get; set; }
    public string? DtlsSetup { get; set; }

    public List<SdpFmtpAttribute> Fmtp { get; } = [];
    public List<SdpRtcpFeedback> RtcpFeedback { get; } = [];
    public List<SdpIceCandidate> Candidates { get; } = [];
    public List<SdpCryptoAttribute> Crypto { get; } = [];
    public List<SdpExtmap> Extensions { get; } = [];
    public List<SdpRid> Rids { get; } = [];
    public SdpSimulcast? Simulcast { get; set; }

    public SdpMediaDescription Build(SdpMediaDirection sessionDirection) =>
        new()
        {
            MediaType = MediaType,
            Port = Port,
            PortCount = PortCount,
            Profile = Profile,
            Formats = Formats,
            Direction = Direction ?? sessionDirection,
            Codecs = _mLineOrder
                .Where(pt => Codecs.ContainsKey(pt))
                .Select(pt => Codecs[pt])
                .ToArray(),
            ConnectionAddress = ConnectionAddress,
            Ptime = Ptime,
            MaxPtime = MaxPtime,
            RtcpMux = RtcpMux,
            RtcpPort = RtcpPort,
            Mid = Mid,
            Msid = Msid,
            Bandwidth = Bandwidth,
            IceUfrag = IceUfrag,
            IcePwd = IcePwd,
            IceOptions = IceOptions,
            EndOfCandidates = EndOfCandidates,
            Fmtp = Fmtp.AsReadOnly(),
            RtcpFeedback = RtcpFeedback.AsReadOnly(),
            Candidates = Candidates.AsReadOnly(),
            Crypto = Crypto.AsReadOnly(),
            Extensions = Extensions.AsReadOnly(),
            Rids = Rids.AsReadOnly(),
            Simulcast = Simulcast,
            Fingerprint = Fingerprint,
            DtlsSetup = DtlsSetup
        };
}
