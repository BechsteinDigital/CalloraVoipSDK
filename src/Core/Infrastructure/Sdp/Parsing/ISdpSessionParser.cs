using System.Diagnostics.CodeAnalysis;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

/// <summary>
/// Parses raw SDP text into structured session models.
/// </summary>
internal interface ISdpSessionParser
{
    /// <summary>
    /// Parses SDP text. Throws on malformed mandatory lines or when a wire limit is exceeded.
    /// Prefer <see cref="TryParse"/> for untrusted remote input.
    /// </summary>
    SdpSessionDescription Parse(string sdp);

    /// <summary>
    /// Non-throwing parse for untrusted remote SDP: returns <see langword="false"/> and a null
    /// <paramref name="result"/> for any malformed or over-limit input, never throwing (K4). All
    /// remote-facing call sites use this instead of <see cref="Parse"/>.
    /// </summary>
    bool TryParse(string? sdp, [NotNullWhen(true)] out SdpSessionDescription? result);
}

