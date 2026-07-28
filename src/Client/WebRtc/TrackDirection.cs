namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The negotiated direction of a media track's m-line (RFC 3264 §5.1): whether this peer sends and/or
/// receives media on it. Distinct from <see cref="MediaDirection"/>, which labels the in/out direction an
/// <see cref="IMediaTap"/> observes; this is the SDP directionality a track is offered with.
/// </summary>
public enum TrackDirection
{
    /// <summary>The track both sends and receives media (<c>a=sendrecv</c>), the default.</summary>
    SendRecv,

    /// <summary>The track only sends media from this peer (<c>a=sendonly</c>).</summary>
    SendOnly,

    /// <summary>The track only receives media at this peer (<c>a=recvonly</c>).</summary>
    RecvOnly,

    /// <summary>The track neither sends nor receives (<c>a=inactive</c>), keeping the m-line as a transport anchor.</summary>
    Inactive,
}
