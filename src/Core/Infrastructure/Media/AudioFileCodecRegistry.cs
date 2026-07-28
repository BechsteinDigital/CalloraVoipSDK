using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Media;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

/// <summary>
/// Default registry for audio file codecs used by recording and playback services.
/// </summary>
internal sealed class AudioFileCodecRegistry : IAudioFileCodecRegistry
{
    private readonly IReadOnlyDictionary<AudioFileFormat, IAudioFileCodec> _codecs;

    /// <summary>
    /// Creates a codec registry with WAV and MP3 adapters.
    /// </summary>
    /// <param name="loggerFactory">
    /// Optional logger factory. When supplied, the MP3 codec logs transcode-encode failures that
    /// occur during writer disposal instead of swallowing them silently.
    /// </param>
    public AudioFileCodecRegistry(ILoggerFactory? loggerFactory = null)
    {
        _codecs = new Dictionary<AudioFileFormat, IAudioFileCodec>
        {
            [AudioFileFormat.Wav] = new WavAudioFileCodec(),
            [AudioFileFormat.Mp3] = new Mp3AudioFileCodec(loggerFactory?.CreateLogger<Mp3AudioFileCodec>()),
        };
    }

    /// <inheritdoc />
    public bool TryGetCodec(AudioFileFormat format, out IAudioFileCodec codec)
        => _codecs.TryGetValue(format, out codec!);
}
