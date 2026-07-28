using CalloraVoipSdk.Core.Application.Ports.Media;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

/// <summary>
/// MP3 codec supporting both passthrough frame mode and PCM16 transcode mode.
/// </summary>
internal sealed class Mp3AudioFileCodec : IAudioFileCodec
{
    private const string Mp3PassthroughCodecName = "MP3-PASSTHROUGH";
    private readonly WavAudioFileCodec _wavCodec = new();
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates an MP3 codec.
    /// </summary>
    /// <param name="logger">
    /// Optional logger used to report transcode-encode failures during writer disposal; when
    /// <see langword="null"/>, such failures are swallowed (never thrown from DisposeAsync).
    /// </param>
    public Mp3AudioFileCodec(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<IAudioFileWriter> CreateWriterAsync(
        string filePath,
        AudioFileCodecContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsPassthroughMode(context))
            return new Mp3PassthroughWriter(filePath);

        return await Mp3TranscodingWriter
            .CreateAsync(filePath, context, _wavCodec, _logger, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IAudioFileReader> CreateReaderAsync(
        string filePath,
        AudioFileCodecContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsPassthroughMode(context))
            return new Mp3PassthroughReader(filePath, context);

        return await Mp3TranscodingReader.CreateAsync(filePath, context, _wavCodec, ct).ConfigureAwait(false);
    }

    private static bool IsPassthroughMode(AudioFileCodecContext context)
        => string.Equals(context.CodecName, Mp3PassthroughCodecName, StringComparison.OrdinalIgnoreCase);

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
