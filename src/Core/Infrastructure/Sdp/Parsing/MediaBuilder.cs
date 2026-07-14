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

    public IDictionary<int, SdpCodecDefinition> Codecs { get; }

    public string? ConnectionAddress { get; set; }
    public SdpMediaDirection? Direction { get; set; }
    public int? Ptime { get; set; }
    public int? MaxPtime { get; set; }
    public bool RtcpMux { get; set; }
    public int? RtcpPort { get; set; }
    public string? Mid { get; set; }
    public int? Bandwidth { get; set; }
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

    public SdpMediaDescription Build(SdpMediaDirection sessionDirection, string fallbackConnectionAddress) =>
        new()
        {
            MediaType = MediaType,
            Port = Port,
            Profile = Profile,
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
            Fingerprint = Fingerprint,
            DtlsSetup = DtlsSetup
        };
}
