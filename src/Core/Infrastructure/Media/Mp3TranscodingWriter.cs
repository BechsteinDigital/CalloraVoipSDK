using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Media;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

internal sealed class Mp3TranscodingWriter : IAudioFileWriter
{
    // Media #16: upper bound for the final ffmpeg encode invoked from DisposeAsync. Dispose has no
    // ambient CancellationToken, so a self-imposed deadline prevents an unresponsive ffmpeg from
    // hanging teardown indefinitely.
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(30);

    private readonly string _outputPath;
    private readonly string _tempWavPath;
    private readonly int _sampleRate;
    private readonly IAudioFileWriter _wavWriter;
    private readonly ILogger? _logger;
    private bool _disposed;

    private Mp3TranscodingWriter(
        string outputPath,
        string tempWavPath,
        int sampleRate,
        IAudioFileWriter wavWriter,
        ILogger? logger)
    {
        _outputPath = outputPath;
        _tempWavPath = tempWavPath;
        _sampleRate = sampleRate;
        _wavWriter = wavWriter;
        _logger = logger;
    }

    /// <summary>
    /// Creates a transcoding writer. The intermediate WAV writer is opened asynchronously here so
    /// no blocking-on-async occurs at construction time (Media #16).
    /// </summary>
    public static async Task<Mp3TranscodingWriter> CreateAsync(
        string filePath,
        AudioFileCodecContext context,
        WavAudioFileCodec wavCodec,
        ILogger? logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));
        ArgumentNullException.ThrowIfNull(wavCodec);

        var sampleRate = context.SampleRate > 0 ? context.SampleRate : 8000;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempWavPath = Path.Combine(
            Path.GetTempPath(),
            $"voipsdk-mp3-encode-{Guid.NewGuid():N}.wav");

        var wavContext = new AudioFileCodecContext(
            PayloadType: context.PayloadType,
            ClockRate: context.ClockRate,
            SampleRate: sampleRate,
            SamplesPerFrame: Math.Max(1, context.SamplesPerFrame),
            CodecName: "L16");

        try
        {
            var wavWriter = await wavCodec
                .CreateWriterAsync(tempWavPath, wavContext, ct)
                .ConfigureAwait(false);

            return new Mp3TranscodingWriter(filePath, tempWavPath, sampleRate, wavWriter, logger);
        }
        catch
        {
            Mp3AudioFileCodec.TryDeleteFile(tempWavPath);
            throw;
        }
    }

    public long BytesWritten => _wavWriter.BytesWritten;

    public async ValueTask WriteFrameAsync(MediaFrame frame, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Mp3TranscodingWriter));

        if ((frame.Payload.Length & 1) != 0)
            throw new InvalidOperationException("MP3 transcode writer expects PCM16 payload with even byte length.");

        await _wavWriter.WriteFrameAsync(frame, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Media #16: DisposeAsync must not throw. The intermediate WAV writer is drained, ffmpeg is
        // driven under a bounded token, and any encode failure is logged rather than propagated.
        using var cts = new CancellationTokenSource(EncodeTimeout);
        try
        {
            await _wavWriter.DisposeAsync().ConfigureAwait(false);
            await FfmpegProcessRunner.RunAsync(
                psi =>
                {
                    psi.ArgumentList.Add("-y");
                    psi.ArgumentList.Add("-hide_banner");
                    psi.ArgumentList.Add("-loglevel");
                    psi.ArgumentList.Add("error");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(_tempWavPath);
                    psi.ArgumentList.Add("-vn");
                    psi.ArgumentList.Add("-ac");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-ar");
                    psi.ArgumentList.Add(_sampleRate.ToString());
                    psi.ArgumentList.Add("-codec:a");
                    psi.ArgumentList.Add("libmp3lame");
                    psi.ArgumentList.Add("-q:a");
                    psi.ArgumentList.Add("4");
                    psi.ArgumentList.Add(_outputPath);
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Encoding MP3 output {OutputPath} failed during writer disposal.",
                _outputPath);
        }
        finally
        {
            Mp3AudioFileCodec.TryDeleteFile(_tempWavPath);
        }
    }
}
